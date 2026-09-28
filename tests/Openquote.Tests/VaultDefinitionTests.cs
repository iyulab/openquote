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
        Assert.Equal(("monthly", 1, "item", "day", "kind", "kind", 1, "owner"),
            (report.Name, report.Version, report.Counts, report.PeriodField, report.RowField, report.RowScheme, report.RowVersion, report.ColumnField));
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
}
