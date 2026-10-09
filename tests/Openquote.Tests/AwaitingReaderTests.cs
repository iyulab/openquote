using System.Text.Json.Nodes;
using Openquote.Fields;
using Openquote.Packs;
using Openquote.Records;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class AwaitingReaderTests
{
    private const string Referral = """
        { "format": "openquote.fields/1", "pack": "care", "type": "referral", "version": 1,
          "awaits": { "field": "outcome", "pending": ["waiting"] },
          "fields": [ { "name": "date", "kind": "date" }, { "name": "outcome", "kind": "coded", "scheme": "outcome" } ] }
        """;

    private static (VaultContent Content, FieldCatalog Fields) Read(params VaultFile[] files)
    {
        var content = VaultReader.Read(files);
        return (content, new FieldCatalog(content.Fields, [new PackManifest("care", 1, "care", new Dictionary<string, int>(), [])]));
    }

    private int _n;

    private VaultFile Record(string type, string? day, JsonNode? outcome = null, string folder = "subjects/s1")
    {
        var n = ++_n;
        var fields = new JsonObject();
        if (day is not null) fields["date"] = day;
        if (outcome is not null) fields["outcome"] = outcome;
        return File(Json(n, $"{type}-{n}", "create", entityType: type, fields: fields), folder);
    }

    private static JsonObject Code(string code) => new() { ["scheme"] = "outcome", ["version"] = 1, ["code"] = code };

    [Fact]
    public void A_record_waits_while_its_answer_is_blank_or_a_code_that_says_none_has_come()
    {
        var (content, fields) = Read(
            File("fields/care/referral/v1.json", Referral),
            Record("referral", "2026-09-22", Code("waiting")),
            Record("referral", "2026-09-10"),
            Record("referral", "2026-09-01", Code("taken-up")),
            Record("referral", null),
            Record("session", "2026-09-05"));
        Assert.Empty(content.Unreadable);
        Assert.Empty(fields.Issues);
        Assert.Equal(new Awaiting("outcome", ["waiting"]), fields.Awaits("referral"), new AwaitingComparer());

        var waiting = AwaitingReader.Read(EntityMerger.Merge(content.Changes).Values, fields);

        // The oldest first, the one with no day last; the answered referral and the session are not waiting.
        Assert.Equal([new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 22), (DateOnly?)null], waiting.Select(w => w.Day));
        Assert.All(waiting, w => Assert.Equal(["s1"], w.People));
    }

    [Fact]
    public void Awaits_that_does_not_fit_the_types_fields_is_named_and_not_applied()
    {
        var (_, unknown) = Read(File("fields/care/referral/v1.json", Referral.Replace("\"field\": \"outcome\"", "\"field\": \"reply\"")));
        Assert.Equal(FieldIssueKind.AwaitsUnknownField, Assert.Single(unknown.Issues).Kind);
        Assert.Null(unknown.Awaits("referral"));

        var (_, uncoded) = Read(File("fields/care/referral/v1.json", Referral.Replace("\"kind\": \"coded\", \"scheme\": \"outcome\"", "\"kind\": \"text\"")));
        Assert.Equal(FieldIssueKind.AwaitsCodesOfUncodedField, Assert.Single(uncoded.Issues).Kind);
        Assert.Null(uncoded.Awaits("referral"));
    }

    [Theory]
    [InlineData("\"awaits\": \"outcome\"")]
    [InlineData("\"awaits\": { \"pending\": [\"waiting\"] }")]
    [InlineData("\"awaits\": { \"field\": \"outcome\", \"pending\": \"waiting\" }")]
    [InlineData("\"awaits\": { \"field\": \"outcome\", \"pending\": [\"waiting\", \"waiting\"] }")]
    public void A_field_file_whose_awaits_is_malformed_is_unreadable(string awaits)
    {
        var (content, _) = Read(File("fields/care/referral/v1.json", Referral.Replace("\"awaits\": { \"field\": \"outcome\", \"pending\": [\"waiting\"] }", awaits)));
        Assert.Single(content.Unreadable);
    }

    private sealed class AwaitingComparer : IEqualityComparer<Awaiting?>
    {
        public bool Equals(Awaiting? x, Awaiting? y) => x?.Field == y?.Field && (x?.Pending ?? []).SequenceEqual(y?.Pending ?? []);
        public int GetHashCode(Awaiting? obj) => obj?.Field.GetHashCode() ?? 0;
    }
}
