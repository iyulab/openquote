using Openquote.Reports;
using Openquote.Classification;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class VaultDefinitionTests
{
    private const string SchemeV1 = """
        { "format": "openquote.scheme/0", "scheme": "kind", "version": 1,
          "items": [ { "code": "a", "label": "A", "suggest": true }, { "code": "a/b", "label": "B", "parent": "a" } ] }
        """;

    private const string Crosswalk12 = """
        { "format": "openquote.crosswalk/0", "scheme": "kind", "from": 1, "to": 2, "links": [ ["a", "x"], ["a/b", "x"] ] }
        """;

    private const string ReportV1 = """
        { "format": "openquote.report/0", "report": "monthly", "version": 1, "label": "Monthly", "counts": "item",
          "period": { "unit": "month", "field": "day" }, "rows": { "field": "kind", "scheme": "kind", "version": 1 },
          "columns": { "field": "owner" } }
        """;

    [Fact]
    public void Reads_schemes_crosswalks_and_report_forms()
    {
        var content = VaultReader.Read(
        [
            File("schemes/kind/v1.json", SchemeV1),
            File("schemes/kind/v1-v2.json", Crosswalk12),
            File("reports/monthly/v1.json", ReportV1),
        ]);

        Assert.Empty(content.Unreadable);
        var scheme = Assert.Single(content.Schemes);
        Assert.Equal(("kind", 1), (scheme.Name, scheme.Version));
        Assert.True(scheme.Items[0].Suggest);
        Assert.False(scheme.Items[1].Suggest); // off unless allowed
        Assert.Equal("a", scheme.Items[1].Parent);
        Assert.Equal([("a", "x"), ("a/b", "x")], Assert.Single(content.Crosswalks).Links);
        var report = Assert.Single(content.Reports);
        Assert.Equal(("monthly", 1, "item", "day"), (report.Name, report.Version, report.Counts, report.Period.Field));
        Assert.Equal([new ReportDimension("kind", "kind", 1), new ReportDimension("owner")], report.Dimensions);
    }

    [Theory]
    [InlineData("schemes/kind/v2.json", UnreadableReason.NameMismatch)]
    [InlineData("schemes/other/v1.json", UnreadableReason.NameMismatch)]
    public void A_scheme_in_the_wrong_place_is_reported(string path, UnreadableReason reason)
    {
        Assert.Equal(reason, Assert.Single(VaultReader.Read([File(path, SchemeV1)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("""{ "format": "openquote.scheme/0", "scheme": "kind", "version": 1, "items": [ { "code": "a", "label": "A" }, { "code": "a", "label": "A2" } ] }""")]
    [InlineData("""{ "format": "openquote.scheme/0", "scheme": "kind", "version": 1, "items": [ { "code": "a/b", "label": "B", "parent": "a" } ] }""")]
    [InlineData("""{ "format": "openquote.scheme/0", "scheme": "kind", "version": 1, "items": [ { "code": "a" } ] }""")]
    public void An_inconsistent_scheme_is_reported(string json)
    {
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("schemes/kind/v1.json", json)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("""{ "format": "openquote.crosswalk/0", "scheme": "kind", "from": 2, "to": 1, "links": [] }""", "schemes/kind/v2-v1.json")]
    [InlineData("""{ "format": "openquote.crosswalk/0", "scheme": "kind", "from": 1, "to": 2, "links": [ ["a"] ] }""", "schemes/kind/v1-v2.json")]
    public void An_invalid_crosswalk_is_reported(string json, string path)
    {
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File(path, json)]).Unreadable).Reason);
    }

    [Fact]
    public void A_report_with_an_unsupported_period_is_reported()
    {
        var weekly = ReportV1.Replace("\"month\"", "\"week\"", StringComparison.Ordinal);

        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("reports/monthly/v1.json", weekly)]).Unreadable).Reason);
    }

    [Fact]
    public void A_truncated_definition_is_reported_and_does_not_stop_the_read()
    {
        var content = VaultReader.Read(
        [
            File("schemes/kind/v1.json", SchemeV1[..20]),
            File("reports/monthly/v1.json", ReportV1),
        ]);

        Assert.Equal(UnreadableReason.Malformed, Assert.Single(content.Unreadable).Reason);
        Assert.Single(content.Reports);
    }

    [Fact]
    public void Reads_when_a_scheme_version_is_in_force()
    {
        var json = """
            { "format": "openquote.scheme/0", "scheme": "kind", "version": 1, "effective": { "from": "2026-03-01", "to": "2027-02-28" },
              "items": [ { "code": "a", "label": "A" } ] }
            """;
        var scheme = Assert.Single(VaultReader.Read([File("schemes/kind/v1.json", json)]).Schemes);
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2027, 2, 28)), (scheme.EffectiveFrom!.Value, scheme.EffectiveTo!.Value));
    }

    [Theory]
    [InlineData("""{ "to": "2027-02-28" }""")]
    [InlineData("""{ "from": "2026-3-1" }""")]
    [InlineData("""{ "from": "2026-03-01", "to": "2026-02-28" }""")]
    public void An_invalid_effective_range_is_reported(string effective)
    {
        var json = $$"""{ "format": "openquote.scheme/0", "scheme": "kind", "version": 1, "effective": {{effective}}, "items": [] }""";
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("schemes/kind/v1.json", json)]).Unreadable).Reason);
    }

    private const string Local = """
        { "format": "openquote.scheme/1", "scheme": "kind.local", "version": 1, "extends": { "scheme": "kind", "version": 1 },
          "items": [ { "code": "a-group", "label": "A, in a group", "anchor": "a" }, { "code": "a-call", "label": "A, by phone", "anchor": "a" } ] }
        """;

    [Fact]
    public void Reads_a_scheme_that_extends_another_with_the_item_each_of_its_items_counts_as()
    {
        var content = VaultReader.Read([File("schemes/kind.local/v1.json", Local)]);

        var scheme = Assert.Single(content.Schemes);
        Assert.Equal(new SchemeVersion("kind", 1), scheme.Extends);
        Assert.Equal(["a", "a"], scheme.Items.Select(i => i.Anchor));
        Assert.Equal(1, content.RequiredVersion);
        Assert.Equal(0, VaultReader.Read([]).RequiredVersion);
    }

    [Theory]
    [InlineData("""{ "format": "openquote.scheme/0", "scheme": "kind.local", "version": 1, "extends": { "scheme": "kind", "version": 1 }, "items": [ { "code": "x", "label": "X", "anchor": "a" } ] }""")]
    [InlineData("""{ "format": "openquote.scheme/1", "scheme": "kind.local", "version": 1, "extends": { "scheme": "kind", "version": 1 }, "items": [ { "code": "x", "label": "X" } ] }""")]
    [InlineData("""{ "format": "openquote.scheme/1", "scheme": "kind.local", "version": 1, "items": [ { "code": "x", "label": "X", "anchor": "a" } ] }""")]
    [InlineData("""{ "format": "openquote.scheme/1", "scheme": "kind.local", "version": 1, "extends": { "scheme": "kind.local", "version": 1 }, "items": [] }""")]
    public void An_extension_without_format_1_an_anchor_for_every_item_or_another_scheme_to_extend_is_reported(string json)
    {
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("schemes/kind.local/v1.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void Reads_the_relation_a_format_1_crosswalk_states_for_a_link()
    {
        var json = """
            { "format": "openquote.crosswalk/1", "scheme": "kind", "from": 1, "to": 2,
              "links": [ ["a", "a", "equivalent"], ["b", "x", "narrower"], ["c", "y", "broader"], ["d", "z", "retired"], ["e", "w"] ] }
            """;

        var content = VaultReader.Read([File("schemes/kind/v1-v2.json", json)]);

        var crosswalk = Assert.Single(content.Crosswalks);
        Assert.Equal(5, crosswalk.Links.Count);
        Assert.Equal(LinkRelation.Broader, crosswalk.Relations[("c", "y")]);
        Assert.False(crosswalk.Relations.ContainsKey(("e", "w")));
        Assert.Equal(1, content.RequiredVersion);
    }

    [Theory]
    [InlineData("openquote.crosswalk/0", """["a", "a", "equivalent"]""")]
    [InlineData("openquote.crosswalk/1", """["a", "a", "same"]""")]
    [InlineData("openquote.crosswalk/1", """["a", "a", "equivalent", "x"]""")]
    public void A_relation_outside_format_1_or_not_one_of_the_four_is_reported(string format, string link)
    {
        var json = $$"""{ "format": "{{format}}", "scheme": "kind", "from": 1, "to": 2, "links": [ {{link}} ] }""";

        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("schemes/kind/v1-v2.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void Reads_a_crosswalk_into_another_scheme_named_after_both()
    {
        var json = """{ "format": "openquote.crosswalk/1", "scheme": "kind", "from": 2, "into": "neis.school", "to": 1, "links": [ ["a", "n1"] ] }""";

        var content = VaultReader.Read([File("schemes/kind/v2-neis.school.v1.json", json)]);

        var crosswalk = Assert.Single(content.Crosswalks);
        Assert.Equal(("neis.school", "kind/2-neis.school/1"), (crosswalk.TargetScheme, crosswalk.Name));
        Assert.Equal(1, content.RequiredVersion);
        Assert.Equal(new VaultFileKind("crosswalk", Name: "kind", From: 2, To: 1, Into: "neis.school"), VaultFileKind.Of("schemes/kind/v2-neis.school.v1.json"));
    }

    [Theory]
    [InlineData("schemes/kind/v2-neis.v1.json", """{ "format": "openquote.crosswalk/0", "scheme": "kind", "from": 2, "into": "neis", "to": 1, "links": [] }""", UnreadableReason.Invalid)]
    [InlineData("schemes/kind/v2-kind.v3.json", """{ "format": "openquote.crosswalk/1", "scheme": "kind", "from": 2, "into": "kind", "to": 3, "links": [] }""", UnreadableReason.Invalid)]
    [InlineData("schemes/kind/v2-v1.json", """{ "format": "openquote.crosswalk/1", "scheme": "kind", "from": 2, "into": "neis", "to": 1, "links": [] }""", UnreadableReason.NameMismatch)]
    [InlineData("schemes/kind/v2-neis.v1.json", """{ "format": "openquote.crosswalk/1", "scheme": "kind", "from": 2, "to": 1, "links": [] }""", UnreadableReason.Invalid)]
    public void A_crosswalk_across_schemes_needs_format_1_another_scheme_and_a_name_that_says_so(string path, string json, UnreadableReason reason)
    {
        Assert.Equal(reason, Assert.Single(VaultReader.Read([File(path, json)]).Unreadable).Reason);
    }
}
