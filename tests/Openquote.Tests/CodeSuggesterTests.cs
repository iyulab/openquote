using System.Text.Json;
using System.Text.Json.Nodes;
using Openquote.Gil;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class CodeSuggesterTests
{
    private static readonly DateOnly March = new(2026, 3, 10);
    private static readonly DateOnly May = new(2026, 5, 10);

    private const string TopicV1 = """
        { "format": "openquote.scheme/0", "scheme": "topic", "version": 1, "items": [
          { "code": "stress", "label": "Stress", "suggest": true },
          { "code": "housing", "label": "Housing", "suggest": true },
          { "code": "crisis", "label": "Crisis" } ] }
        """;

    // From April a new version names the stress item differently; the crosswalk carries settled values to it.
    private const string TopicV2 = """
        { "format": "openquote.scheme/0", "scheme": "topic", "version": 2, "effective": { "from": "2026-04-01" }, "items": [
          { "code": "wellbeing", "label": "Wellbeing", "suggest": true },
          { "code": "housing", "label": "Housing", "suggest": true },
          { "code": "crisis", "label": "Crisis" } ] }
        """;

    private const string Crosswalk = """
        { "format": "openquote.crosswalk/0", "scheme": "topic", "from": 1, "to": 2,
          "links": [ ["stress", "wellbeing"], ["housing", "housing"], ["crisis", "crisis"] ] }
        """;

    private const string SessionFields = """
        { "format": "openquote.fields/0", "pack": "care", "type": "session", "version": 1, "fields": [
          { "name": "client", "kind": "reference", "type": "subject" },
          { "name": "topic", "kind": "coded", "scheme": "topic" },
          { "name": "note", "kind": "text", "tier": "narrative" } ] }
        """;

    private static JsonObject Session(string client, string note, string topic) => new()
    {
        ["client"] = client,
        ["note"] = note,
        ["topic"] = new JsonObject { ["scheme"] = "topic", ["version"] = 1, ["code"] = topic },
    };

    private static List<VaultFile> Vault(params VaultFile[] records) =>
    [
        File("schemes/topic/v1.json", TopicV1),
        File("schemes/topic/v2.json", TopicV2),
        File("schemes/topic/v1-v2.json", Crosswalk),
        File("fields/care/session/v1.json", SessionFields),
        .. records,
    ];

    private static readonly VaultFile[] Settled =
    [
        File(Json(1, "e1", "create", fields: Session("p1", "stress at work and trouble sleeping", "stress"))),
        File(Json(2, "e2", "create", fields: Session("p2", "needs housing support, referred to the city office", "housing"))),
        File(Json(3, "e3", "create", fields: Session("p3", "said they want to end their life, safety plan made", "crisis"))),
    ];

    private static Dictionary<string, JsonElement> Draft(params (string Field, string Value)[] values) =>
        values.ToDictionary(v => v.Field, v => JsonSerializer.SerializeToElement(v.Value));

    private static Task<CodeSuggester> Build(DateOnly date, params VaultFile[] records) =>
        CodeSuggester.BuildAsync(VaultReader.Read(Vault(records)), "session", date, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Suggests_the_code_of_the_closest_settled_record_with_that_record_as_evidence()
    {
        var suggester = await Build(March, Settled);

        var suggestions = await suggester.SuggestAsync(Draft(("note", "stress at work, cannot sleep well")), TestContext.Current.CancellationToken);

        var topic = Assert.Single(suggestions);
        Assert.Equal(("topic", "topic", 1), (topic.Field, topic.Scheme, topic.Version));
        // The order is Gil's (with few settled records, the most frequent value can come first); the
        // closest record is the evidence of its own code.
        Assert.Equal("e1", Assert.Single(topic.Codes, c => c.Code == "stress").Similar[0]);
    }

    [Fact]
    public async Task Never_suggests_an_item_its_scheme_does_not_mark_for_suggestion()
    {
        var suggester = await Build(March, Settled);

        var suggestions = await suggester.SuggestAsync(Draft(("note", "wants to end their life, a safety plan")), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(suggestions.SelectMany(s => s.Codes), c => c.Code == "crisis");
    }

    [Fact]
    public async Task Follows_what_a_pack_says_over_the_scheme_setting_apart_an_item_to_confirm()
    {
        const string Says = """
            { "format": "openquote.suggestions/0", "pack": "care", "version": 1,
              "schemes": { "topic": { "1": { "crisis": "confirm", "stress": "off" } } } }
            """;
        var suggester = await CodeSuggester.BuildAsync(VaultReader.Read([.. Vault(Settled), File("suggestions/care/v1.json", Says)]), "session", March,
            TestContext.Current.CancellationToken);

        var crisis = await suggester.SuggestAsync(Draft(("note", "said they want to end their life")), TestContext.Current.CancellationToken);
        var stress = await suggester.SuggestAsync(Draft(("note", "stress at work, cannot sleep well")), TestContext.Current.CancellationToken);

        var confirm = Assert.Single(Assert.Single(crisis).Codes, c => c.Code == "crisis");
        Assert.True(confirm.Confirm);
        Assert.Equal("e3", confirm.Similar[0]);
        Assert.All(stress.SelectMany(s => s.Codes), c => Assert.NotEqual("stress", c.Code));
        Assert.All(stress.SelectMany(s => s.Codes).Where(c => c.Code != "crisis"), c => Assert.False(c.Confirm));
    }

    [Fact]
    public async Task Suggests_in_the_version_in_force_on_the_date_carrying_settled_values_to_it()
    {
        var suggester = await Build(May, Settled);

        var suggestions = await suggester.SuggestAsync(Draft(("note", "stress at work, cannot sleep well")), TestContext.Current.CancellationToken);

        var topic = Assert.Single(suggestions);
        Assert.Equal(2, topic.Version);
        Assert.Equal("e1", Assert.Single(topic.Codes, c => c.Code == "wellbeing").Similar[0]);
        Assert.DoesNotContain(topic.Codes, c => c.Code == "stress");
    }

    [Fact]
    public async Task Who_a_record_is_about_is_not_evidence()
    {
        // This person's earlier records are all about stress. Knowing only who the new record is about,
        // no settled record is like it: the person is not what makes records alike.
        var suggester = await Build(March,
        [
            .. Settled,
            File(Json(4, "e4", "create", fields: Session("p9", "stress again at work", "stress"))),
            File(Json(5, "e5", "create", fields: Session("p9", "work stress and sleep", "stress"))),
        ]);

        var suggestions = await suggester.SuggestAsync(Draft(("client", "p9")), TestContext.Current.CancellationToken);

        Assert.All(suggestions.SelectMany(s => s.Codes), c => Assert.Empty(c.Similar));
    }

    [Fact]
    public async Task Learns_only_from_settled_records()
    {
        // e4 was changed on two devices at once and e5 was destroyed: neither has a value anyone settled.
        var suggester = await Build(March,
        [
            .. Settled,
            File(Json(4, "e4", "create", fields: Session("p4", "first", "stress"))),
            File(Json(5, "e4", "update", baseIds: [4], fields: new JsonObject { ["note"] = "here" })),
            File(Json(6, "e4", "update", baseIds: [4], device: "dev2", fields: new JsonObject { ["note"] = "there" })),
            File(Json(7, "e5", "create", fields: Session("p5", "gone", "housing"))),
            File(Json(8, "e5", "destroy", baseIds: [7])),
        ]);

        Assert.Equal(3, suggester.Remembered);
    }

    /// <summary>
    /// Not a check but a measurement: builds a suggester from <c>OPENQUOTE_MEASURE_RECORDS</c> settled
    /// sessions (default 2000, about a year of one counselor's work) with a few hundred characters of
    /// written content each, and times the build and two suggestions. Run the built test executable with
    /// <c>-method Openquote.Tests.CodeSuggesterTests.Measure_building_from_a_years_records -explicit only -showliveoutput</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Measure_building_from_a_years_records()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("OPENQUOTE_MEASURE_RECORDS"), out var n) ? n : 2000;
        string[] words =
        [
            "school", "friend", "teacher", "class", "exam", "sleep", "family", "home", "mother", "father",
            "anger", "worry", "grades", "phone", "game", "lunch", "club", "late", "absent", "argument",
            "tired", "lonely", "moving", "money", "rent", "landlord", "office", "support", "plan", "talked",
        ];
        string[] codes = ["stress", "housing"];
        var random = new Random(1);
        var records = new VaultFile[count];
        for (var i = 0; i < count; i++)
        {
            var note = string.Join(' ', Enumerable.Range(0, 50).Select(_ => words[random.Next(words.Length)]));
            records[i] = File(Json(i + 10, $"m{i}", "create", fields: Session($"p{i % 40}", note, codes[random.Next(codes.Length)])));
        }
        var content = VaultReader.Read(Vault(records));
        var cancel = TestContext.Current.CancellationToken;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var suggester = await CodeSuggester.BuildAsync(content, "session", March, cancel);
        var building = started.Elapsed;
        started.Restart();
        await suggester.SuggestAsync(Draft(("note", "worry about rent, the landlord and moving home")), cancel);
        var first = started.Elapsed;
        started.Restart();
        await suggester.SuggestAsync(Draft(("note", "tired in class after a late game on the phone")), cancel);
        var second = started.Elapsed;

        TestContext.Current.TestOutputHelper!.WriteLine(
            $"measure: {suggester.Remembered} settled sessions · build {building.TotalMilliseconds:F0} ms · first suggestion {first.TotalMilliseconds:F0} ms · next {second.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Suggests_nothing_for_a_type_with_no_coded_field_to_suggest_for()
    {
        var suggester = await CodeSuggester.BuildAsync(VaultReader.Read(Vault(Settled)), "subject", March, TestContext.Current.CancellationToken);

        Assert.Empty(suggester.Fields);
        Assert.Empty(await suggester.SuggestAsync(Draft(("name", "someone")), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_field_the_draft_already_holds_is_not_suggested_for()
    {
        var suggester = await Build(March, Settled);
        var draft = Draft(("note", "stress at work, cannot sleep well"));
        draft["topic"] = JsonSerializer.SerializeToElement(new { scheme = "topic", version = 1, code = "housing" });

        Assert.Empty(await suggester.SuggestAsync(draft, TestContext.Current.CancellationToken));
    }
}
