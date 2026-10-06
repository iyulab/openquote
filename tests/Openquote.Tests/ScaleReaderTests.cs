using System.Text.Json.Nodes;
using Openquote.Fields;
using Openquote.Packs;
using Openquote.Records;
using Openquote.Scales;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class ScaleReaderTests
{
    private const string ScalesFile = """
        { "format": "openquote.scales/0", "pack": "care.scale", "version": 1,
          "responses": { "type": "response", "scale": "scale", "score": "score" },
          "scales": {
            "mood-9": { "direction": "lower-is-better", "min": 0, "max": 27,
              "licence": { "terms": "Free to use", "source": "https://example.org/terms", "retrieved": "2026-10-05" } },
            "worry-7": { "direction": "lower-is-better", "min": 0, "max": 21 } } }
        """;

    private static readonly PackManifest[] Packs = [new PackManifest("care", 1, "care", new Dictionary<string, int>(), [])];

    private static VaultContent Content(params VaultFile[] more) => VaultReader.Read([
        File("fields/care/intake/v1.json", """{ "format": "openquote.fields/1", "pack": "care", "type": "intake", "version": 1, "role": "opens", "fields": [ { "name": "date", "kind": "date" } ] }"""),
        File("fields/care/closing/v1.json", """{ "format": "openquote.fields/1", "pack": "care", "type": "closing", "version": 1, "role": "closes", "fields": [ { "name": "date", "kind": "date" } ] }"""),
        File("fields/care/response/v1.json", """{ "format": "openquote.fields/1", "pack": "care", "type": "response", "version": 1, "fields": [ { "name": "date", "kind": "date" }, { "name": "scale", "kind": "coded", "scheme": "scale" }, { "name": "score", "kind": "number" } ] }"""),
        File("scales/care.scale/v1.json", ScalesFile),
        .. more,
    ]);

    private static readonly VaultContent Definitions = Content();
    private static readonly FieldCatalog Fields = new(Definitions.Fields, Packs);
    private static readonly ScaleCatalog Scales = new(Definitions.Scales, Packs);

    private int _n;

    private JsonObject Record(string type, string day, JsonObject? more = null)
    {
        var n = ++_n;
        var fields = more ?? new JsonObject();
        if (day != "") fields["date"] = day;
        var change = Json(n, $"{type}-{n}", "create", entityType: type, fields: fields);
        return change;
    }

    private JsonObject Response(string day, string scale, JsonNode? score) =>
        Record("response", day, new JsonObject { ["scale"] = new JsonObject { ["scheme"] = "scale", ["version"] = 1, ["code"] = scale }, ["score"] = score });

    private static SubjectCases Cases(params JsonObject[] changes)
    {
        var content = VaultReader.Read(changes.Select(c => File(c)));
        Assert.Empty(content.Unreadable);
        return CaseReader.Read("s1", EntityMerger.Merge(content.Changes).Values, Fields);
    }

    [Fact]
    public void A_scales_file_is_read_with_each_scale_its_direction_range_and_licence()
    {
        Assert.Empty(Definitions.Unreadable);
        var set = Assert.Single(Definitions.Scales);
        Assert.Equal(("care.scale", 1, "response", "scale", "score"), (set.Pack, set.Version, set.Type, set.ScaleField, set.ScoreField));
        var mood = set.Scales["mood-9"];
        Assert.Equal((ScaleDirection.LowerIsBetter, 0m, 27m), (mood.Direction, mood.Min, mood.Max));
        Assert.Equal(new ScaleLicence("Free to use", "https://example.org/terms", new DateOnly(2026, 10, 5)), mood.Licence);
        Assert.Null(set.Scales["worry-7"].Licence);
        Assert.Empty(Scales.Check(Fields));
    }

    [Theory]
    [InlineData("""{ "format": "openquote.scales/0", "pack": "care.scale", "version": 1, "responses": { "type": "subject", "scale": "scale", "score": "score" }, "scales": { "a": { "direction": "lower-is-better", "min": 0, "max": 1 } } }""")]
    [InlineData("""{ "format": "openquote.scales/0", "pack": "care.scale", "version": 1, "responses": { "type": "response", "scale": "score", "score": "score" }, "scales": { "a": { "direction": "lower-is-better", "min": 0, "max": 1 } } }""")]
    [InlineData("""{ "format": "openquote.scales/0", "pack": "care.scale", "version": 1, "responses": { "type": "response", "scale": "scale", "score": "score" }, "scales": {} }""")]
    [InlineData("""{ "format": "openquote.scales/0", "pack": "care.scale", "version": 1, "responses": { "type": "response", "scale": "scale", "score": "score" }, "scales": { "a": { "direction": "better", "min": 0, "max": 1 } } }""")]
    [InlineData("""{ "format": "openquote.scales/0", "pack": "care.scale", "version": 1, "responses": { "type": "response", "scale": "scale", "score": "score" }, "scales": { "a": { "direction": "lower-is-better", "min": 5, "max": 5 } } }""")]
    [InlineData("""{ "format": "openquote.scales/0", "pack": "care.scale", "version": 1, "responses": { "type": "response", "scale": "scale", "score": "score" }, "scales": { "a": { "direction": "lower-is-better", "min": 0, "max": 1, "licence": { "source": "x" } } } }""")]
    [InlineData("""{ "format": "openquote.scales/0", "pack": "care.scale", "version": 1, "responses": { "type": "response", "scale": "scale", "score": "score" }, "scales": { "a": { "direction": "lower-is-better", "min": 0, "max": 1, "licence": { "terms": "x", "retrieved": "5 Oct" } } } }""")]
    public void A_scales_file_that_does_not_say_its_responses_or_a_usable_scale_is_unreadable(string json)
    {
        var content = VaultReader.Read([File("scales/care.scale/v1.json", json)]);

        var bad = Assert.Single(content.Unreadable);
        Assert.Equal(UnreadableReason.Invalid, bad.Reason);
        Assert.Empty(content.Scales);
    }

    [Fact]
    public void A_scales_file_at_a_path_that_disagrees_is_a_name_mismatch_and_its_kind_is_read_from_the_path()
    {
        var content = VaultReader.Read([File("scales/other/v1.json", ScalesFile)]);

        Assert.Equal(UnreadableReason.NameMismatch, Assert.Single(content.Unreadable).Reason);
        Assert.Equal(new VaultFileKind("scales", Name: "care.scale", Version: 2), VaultFileKind.Of("scales/care.scale/v2.json"));
    }

    [Fact]
    public void The_baseline_is_a_cases_first_score_and_the_end_its_last_up_to_the_closing()
    {
        var cases = Cases(
            Record("intake", "2026-03-02"), Response("2026-03-02", "mood-9", 18), Response("2026-03-16", "mood-9", 14),
            Response("2026-04-06", "mood-9", 9), Record("closing", "2026-04-20"),
            Response("2026-05-11", "mood-9", 6)); // a follow-up after the closing is not the case's end

        var only = Assert.Single(ScaleReader.Read(cases, Fields, Scales));
        var mood = Assert.Single(only.Scales);
        Assert.Equal(("mood-9", 18m, new DateOnly(2026, 3, 2)), (mood.Scale.Code, mood.Baseline.Score, mood.Baseline.Day));
        Assert.Equal((9m, new DateOnly(2026, 4, 6)), (mood.Last.Score, mood.Last.Day));
        Assert.Equal(3, mood.Responses);
        Assert.True(mood.Paired);
        Assert.Equal(-9m, mood.Change);
        Assert.Empty(only.Unusable);
    }

    [Fact]
    public void A_case_whose_scores_fall_on_one_day_is_not_paired_and_has_no_change()
    {
        var cases = Cases(Record("intake", "2026-03-02"), Response("2026-03-02", "worry-7", 12), Response("2026-03-02", "worry-7", 11));

        var worry = Assert.Single(Assert.Single(ScaleReader.Read(cases, Fields, Scales)).Scales);

        Assert.False(worry.Paired);
        Assert.Null(worry.Change);
        Assert.Equal(11m, worry.Last.Score); // the last written that day
    }

    [Fact]
    public void Each_scale_in_a_case_is_read_on_its_own_and_each_case_starts_again()
    {
        var cases = Cases(
            Record("intake", "2026-03-02"), Response("2026-03-02", "mood-9", 18), Response("2026-03-03", "worry-7", 15),
            Response("2026-03-30", "worry-7", 10), Record("closing", "2026-04-01"),
            Record("intake", "2026-09-01"), Response("2026-09-01", "mood-9", 20));

        var read = ScaleReader.Read(cases, Fields, Scales);

        Assert.Equal(2, read.Count);
        Assert.Equal(["mood-9", "worry-7"], read[0].Scales.Select(s => s.Scale.Code));
        Assert.Equal([false, true], read[0].Scales.Select(s => s.Paired));
        Assert.Equal(20m, Assert.Single(read[1].Scales).Baseline.Score); // the second case's own baseline
    }

    [Fact]
    public void A_response_that_gives_no_score_is_listed_apart_with_the_reason()
    {
        var cases = Cases(
            Record("intake", "2026-03-02"),
            Response("2026-03-03", "unknown", 5),
            Response("2026-03-04", "mood-9", 30),
            Response("2026-03-05", "mood-9", "many"),
            Record("response", "2026-03-06", new JsonObject { ["score"] = 4 }),
            Response("2026-03-07", "mood-9", 8));

        var only = Assert.Single(ScaleReader.Read(cases, Fields, Scales));

        Assert.Equal(
            [UnusableReason.UnknownScale, UnusableReason.OutOfRange, UnusableReason.NoScore, UnusableReason.NoScale],
            only.Unusable.Select(u => u.Reason));
        Assert.Equal(8m, Assert.Single(only.Scales).Baseline.Score);
    }

    [Fact]
    public void A_summary_counts_the_cases_closed_in_the_days_by_scale_and_keeps_those_without_a_score_in_view()
    {
        var first = ScaleReader.Read(Cases(
            Record("intake", "2026-03-02"), Response("2026-03-02", "mood-9", 18), Response("2026-04-06", "mood-9", 9),
            Record("closing", "2026-04-20"),
            Record("intake", "2026-05-04"), Response("2026-05-04", "mood-9", 15), Record("closing", "2026-05-10"),
            Record("intake", "2026-06-01")), Fields, Scales);
        var second = ScaleReader.Read(Cases(Record("intake", "2026-03-10"), Record("closing", "2026-04-15")), Fields, Scales);

        var summary = ScaleReader.Summarize([.. first, .. second], new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 31));

        Assert.Equal((3, 1), (summary.Closed, summary.Unscored)); // the open case is not counted
        var mood = Assert.Single(summary.Scales);
        Assert.Equal(("mood-9", 2, 1), (mood.Scale.Code, mood.Scored, mood.Paired));
    }

    [Fact]
    public void A_code_two_packs_give_stands_as_the_first_pack_gives_it_and_the_fields_are_checked()
    {
        var other = File("scales/extra/v1.json", """{ "format": "openquote.scales/0", "pack": "extra", "version": 1, "responses": { "type": "check", "scale": "which", "score": "total" }, "scales": { "mood-9": { "direction": "higher-is-better", "min": 0, "max": 10 } } }""");
        var content = Content(other);
        var scales = new ScaleCatalog(content.Scales, Packs);

        Assert.Equal(ScaleDirection.LowerIsBetter, scales.Find("response", "mood-9")!.Direction);
        Assert.Null(scales.Find("check", "mood-9"));
        Assert.Equal(
            [ScaleIssueKind.DuplicateScale, ScaleIssueKind.ScaleFieldNotCoded, ScaleIssueKind.ScoreFieldNotNumber],
            scales.Check(new FieldCatalog(content.Fields, Packs)).Select(i => i.Kind));
    }
}
