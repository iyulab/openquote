using System.Text.Json;
using System.Text.Json.Nodes;
using Openquote.Gil;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

/// <summary>
/// Not checks but a measurement: how often the first suggestion is the topic a session's notes are about,
/// under different threshold policies, on synthetic sessions — eight common topics and one rare topic to
/// confirm, notes written either in the words the settled sessions use or in other words for the same
/// thing. Run the built test executable with
/// <c>-class Openquote.Tests.SuggestionQualityMeasurement -explicit only -showliveoutput</c>.
/// </summary>
public class SuggestionQualityMeasurement
{
    private static readonly DateOnly On = new(2026, 9, 1);

    // Each topic in two voices: the words settled sessions use (Same), and other words for the same thing (Other).
    private static readonly (string Code, string[] Same, string[] Other)[] Topics =
    [
        ("study", ["cannot focus on studying before exams", "grades dropped this term", "falls behind in maths homework"],
                  ["the test next week makes revision impossible", "marks are lower than last semester", "assignments keep piling up unfinished"]),
        ("friends", ["eats lunch alone, friends drifted away", "argued with a close friend", "left out of the group chat by classmates"],
                    ["nobody to sit with at break", "fell out with the person they trusted most", "peers ignore their messages"]),
        ("family", ["frequent arguments with parents about the phone", "tension with a sibling at home", "parents are divorcing"],
                   ["mum and dad shout about screen time", "a brother picks fights every evening", "the household is splitting up"]),
        ("anxiety", ["short of breath before presentations", "worries a lot and feels nervous", "panics in crowded places"],
                    ["chest tightens when speaking in front of the class", "mind races about what could go wrong", "heart pounds on the busy bus"]),
        ("mood", ["feels low and has no energy", "nothing is fun anymore", "cries often without a reason"],
                 ["empty and flat most days", "lost interest in hobbies they loved", "tearful for no clear cause"]),
        ("anger", ["throws things when angry", "loses temper with teachers", "got into a fight at school"],
                  ["slammed the door and broke a chair", "snapped and swore at staff", "hit another pupil in the corridor"]),
        ("sleep", ["cannot fall asleep at night", "wakes up tired every morning", "sleeps through the alarm"],
                  ["lies awake until three", "exhausted from the moment the day starts", "misses first lesson after oversleeping"]),
        ("gaming", ["plays games all night", "spends hours on the phone", "cannot stop playing online games"],
                   ["up till dawn on the console", "screen in hand from waking to bedtime", "logs in again whenever told to stop"]),
    ];

    private static readonly (string Code, string[] Same, string[] Other) Rare =
        ("crisis", ["said they want to end their life", "has thoughts of self harm"], ["talked about not wanting to be here", "has been cutting their arm"]);

    private static readonly string[] Openings = ["came in after class.", "referred by the homeroom teacher.", "asked to talk today."];
    private static readonly string[] Closings = ["will check in next week.", "agreed on a small goal.", "parents to be contacted."];
    private static readonly string[] Methods = ["in-person", "phone", "group"];

    private const string Scheme = """
        { "format": "openquote.scheme/0", "scheme": "topic", "version": 1, "items": [
          { "code": "study", "label": "Study", "suggest": true }, { "code": "friends", "label": "Friends", "suggest": true },
          { "code": "family", "label": "Family", "suggest": true }, { "code": "anxiety", "label": "Anxiety", "suggest": true },
          { "code": "mood", "label": "Mood", "suggest": true }, { "code": "anger", "label": "Anger", "suggest": true },
          { "code": "sleep", "label": "Sleep", "suggest": true }, { "code": "gaming", "label": "Gaming", "suggest": true },
          { "code": "crisis", "label": "Crisis" } ] }
        """;

    private const string MethodScheme = """
        { "format": "openquote.scheme/0", "scheme": "method", "version": 1, "items": [
          { "code": "in-person", "label": "In person" }, { "code": "phone", "label": "Phone" }, { "code": "group", "label": "Group" } ] }
        """;

    private const string Says = """
        { "format": "openquote.suggestions/0", "pack": "care", "version": 1, "schemes": { "topic": { "1": { "crisis": "confirm" } } } }
        """;

    private const string Fields = """
        { "format": "openquote.fields/0", "pack": "care", "type": "session", "version": 1, "fields": [
          { "name": "client", "kind": "reference", "type": "subject" },
          { "name": "topic", "kind": "coded", "scheme": "topic" },
          { "name": "method", "kind": "coded", "scheme": "method" },
          { "name": "grade", "kind": "text" },
          { "name": "note", "kind": "text", "tier": "narrative" } ] }
        """;

    private static string Note(Random random, string[] sentences, string[]? aside)
    {
        var parts = new List<string> { Openings[random.Next(Openings.Length)] };
        parts.AddRange(sentences.OrderBy(_ => random.Next()).Take(2 + random.Next(2)));
        if (aside is not null)
        {
            parts.Add(aside[random.Next(aside.Length)]);
        }
        parts.Add(Closings[random.Next(Closings.Length)]);
        return string.Join(' ', parts);
    }

    /// <summary><paramref name="settledCount"/> settled sessions in the usual words, about 3% of them on the rare topic.</summary>
    private static List<VaultFile> Vault(int settledCount, Random random)
    {
        List<VaultFile> files =
        [
            File("schemes/topic/v1.json", Scheme),
            File("schemes/method/v1.json", MethodScheme),
            File("fields/care/session/v1.json", Fields),
            File("suggestions/care/v1.json", Says),
        ];
        for (var i = 0; i < settledCount; i++)
        {
            var (code, same, _) = i % 33 == 7 ? Rare : Topics[random.Next(Topics.Length)];
            var aside = random.Next(4) == 0 ? Topics[random.Next(Topics.Length)].Same : null;
            var session = new JsonObject
            {
                ["client"] = $"p{random.Next(60)}",
                ["note"] = Note(random, same, aside),
                ["grade"] = $"{1 + random.Next(3)}",
                ["method"] = new JsonObject { ["scheme"] = "method", ["version"] = 1, ["code"] = Methods[random.Next(Methods.Length)] },
                ["topic"] = new JsonObject { ["scheme"] = "topic", ["version"] = 1, ["code"] = code },
            };
            files.Add(File(Json(i + 10, $"s{i}", "create", fields: session)));
        }
        return files;
    }

    private static Dictionary<string, JsonElement> Draft(Random random, string[] sentences) => new()
    {
        ["note"] = JsonSerializer.SerializeToElement(Note(random, sentences, null)),
        ["grade"] = JsonSerializer.SerializeToElement($"{1 + random.Next(3)}"),
        ["method"] = JsonSerializer.SerializeToElement(new { scheme = "method", version = 1, code = Methods[random.Next(Methods.Length)] }),
    };

    public static TheoryData<int> Sizes => [20, 60, 240, 1000];

    [Theory(Explicit = true)]
    [MemberData(nameof(Sizes))]
    public async Task Measure_the_first_suggestion_against_the_notes(int settledCount)
    {
        var cancel = TestContext.Current.CancellationToken;
        var content = VaultReader.Read(Vault(settledCount, new Random(1)));
        (string Name, ThresholdPolicy Policy)[] policies =
        [
            ("none", ThresholdPolicy.None),
            ("fixed 0.1", new(1, int.MaxValue, 0.1)),
            ("fixed 0.3", new(1, int.MaxValue, 0.3)),
            ("fixed 0.5", new(1, int.MaxValue, 0.5)),
            ("replay 0.6", new(0.6, 30, 0.5)),
            ("replay 0.7 (default)", ThresholdPolicy.Default),
            ("replay 0.8", new(0.8, 30, 0.5)),
        ];
        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine($"measure: {settledCount} settled · 8 topics + rare (chance 1/8 = 12.5%)");
        foreach (var (name, policy) in policies)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var suggester = await CodeSuggester.BuildAsync(content, "session", On, policy, cancel);
            var building = started.Elapsed;
            var line = $"  {name,-22} build {building.TotalMilliseconds,6:F0} ms";
            foreach (var voice in new[] { "same", "other" })
            {
                var random = new Random(2);
                int first = 0, inThree = 0, trustedFirst = 0, asked = 0;
                foreach (var (code, same, other) in Topics)
                {
                    for (var i = 0; i < 15; i++)
                    {
                        var codes = await First(suggester, Draft(random, voice == "same" ? same : other), cancel);
                        asked++;
                        first += codes.Count > 0 && codes[0].Code == code ? 1 : 0;
                        trustedFirst += codes.Count > 0 && codes[0].Code == code && codes[0].Trusted ? 1 : 0;
                        inThree += codes.Take(3).Any(c => c.Code == code) ? 1 : 0;
                    }
                }
                line += $" · {voice}: first {100.0 * first / asked,5:F1}% (trusted {100.0 * trustedFirst / asked,5:F1}%) top3 {100.0 * inThree / asked,5:F1}%";
            }
            // The rare topic to confirm: offered at all, offered with records as evidence, offered as a layer's answer —
            // for sessions that are about it, and for sessions that are not (where it would only alarm).
            var rare = new Random(3);
            int about = 0, aboutShown = 0, aboutEvidence = 0, aboutTrusted = 0, notAbout = 0, notShown = 0, notEvidence = 0, notTrusted = 0, aboutNearest = 0, notNearest = 0;
            foreach (var voice in new[] { Rare.Same, Rare.Other })
            {
                for (var i = 0; i < 20; i++)
                {
                    var crisis = (await First(suggester, Draft(rare, voice), cancel)).FirstOrDefault(c => c.Confirm);
                    about++;
                    aboutShown += crisis is null ? 0 : 1;
                    aboutEvidence += crisis is { Basis: SuggestionBasis.SimilarRecords } ? 1 : 0;
                    aboutTrusted += crisis is { Trusted: true } ? 1 : 0;
                    aboutNearest += crisis is { Nearest: true } ? 1 : 0;
                }
            }
            foreach (var (_, same, other) in Topics)
            {
                foreach (var voice in new[] { same, other })
                {
                    for (var i = 0; i < 5; i++)
                    {
                        var crisis = (await First(suggester, Draft(rare, voice), cancel)).FirstOrDefault(c => c.Confirm);
                        notAbout++;
                        notShown += crisis is null ? 0 : 1;
                        notEvidence += crisis is { Basis: SuggestionBasis.SimilarRecords } ? 1 : 0;
                        notTrusted += crisis is { Trusted: true } ? 1 : 0;
                        notNearest += crisis is { Nearest: true } ? 1 : 0;
                    }
                }
            }
            line += $" · rare shown {aboutShown}/{about} vs {notShown}/{notAbout}, with records {aboutEvidence}/{about} vs {notEvidence}/{notAbout}, answered {aboutTrusted}/{about} vs {notTrusted}/{notAbout}, nearest {aboutNearest}/{about} vs {notNearest}/{notAbout}";
            output.WriteLine(line);
        }
    }

    private static async Task<IReadOnlyList<SuggestedCode>> First(CodeSuggester suggester, Dictionary<string, JsonElement> draft, CancellationToken cancel) =>
        (await suggester.SuggestAsync(draft, cancel)).FirstOrDefault(s => s.Field == "topic")?.Codes ?? [];
}
