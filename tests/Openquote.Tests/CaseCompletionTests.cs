using System.Text.Json.Nodes;
using Openquote.Fields;
using Openquote.Packs;
using Openquote.Records;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class CaseCompletionTests
{
    private const string Intake = """
        { "format": "openquote.fields/1", "pack": "care", "type": "intake", "version": 1, "role": "opens",
          "fields": [ { "name": "date", "kind": "date" } ] }
        """;

    private const string Closing = """
        { "format": "openquote.fields/1", "pack": "care", "type": "closing", "version": 1, "role": "closes",
          "completes": { "field": "reason", "codes": ["completed", "referred"] },
          "fields": [ { "name": "date", "kind": "date" }, { "name": "reason", "kind": "coded", "scheme": "reason" } ] }
        """;

    private static (VaultContent Content, FieldCatalog Fields) Read(params VaultFile[] files)
    {
        var content = VaultReader.Read(files);
        return (content, new FieldCatalog(content.Fields, [new PackManifest("care", 1, "care", new Dictionary<string, int>(), [])]));
    }

    private int _n;

    private VaultFile Record(string type, string day, JsonNode? reason = null)
    {
        var n = ++_n;
        var fields = new JsonObject { ["date"] = day };
        if (reason is not null) fields["reason"] = reason;
        return File(Json(n, $"{type}-{n}", "create", entityType: type, fields: fields), "subjects/s1");
    }

    private static JsonObject Code(string code, bool primary = false)
    {
        var value = new JsonObject { ["scheme"] = "reason", ["version"] = 1, ["code"] = code };
        if (primary) value["primary"] = true;
        return value;
    }

    private SubjectCase OnlyCase(string closing, params VaultFile[] records)
    {
        var (content, fields) = Read([File("fields/care/intake/v1.json", Intake), File("fields/care/closing/v1.json", closing), .. records]);
        Assert.Empty(content.Unreadable);
        Assert.Empty(fields.Issues);
        return Assert.Single(CaseReader.Read("s1", EntityMerger.Merge(content.Changes).Values, fields).Cases);
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("referred", true)]
    [InlineData("lost-contact", false)]
    public void A_closing_ended_the_work_as_planned_when_its_field_holds_one_of_the_packs_codes(string reason, bool completed)
    {
        var only = OnlyCase(Closing, Record("intake", "2026-03-02"), Record("closing", "2026-05-20", Code(reason)));

        Assert.Equal(completed, only.Completed);
    }

    [Fact]
    public void A_closing_without_a_reason_did_not_end_the_work_as_planned()
    {
        var only = OnlyCase(Closing, Record("intake", "2026-03-02"), Record("closing", "2026-05-20"));

        Assert.False(only.Completed);
    }

    [Fact]
    public void Of_several_reasons_the_one_marked_first_says()
    {
        var marked = OnlyCase(Closing, Record("intake", "2026-03-02"),
            Record("closing", "2026-05-20", new JsonArray(Code("lost-contact"), Code("completed", primary: true))));
        Assert.True(marked.Completed);

        var unmarked = OnlyCase(Closing, Record("intake", "2026-03-02"),
            Record("closing", "2026-05-20", new JsonArray(Code("completed"), Code("referred"))));
        Assert.False(unmarked.Completed);
    }

    [Fact]
    public void An_open_case_and_a_closing_whose_packs_say_nothing_tell_nothing()
    {
        var open = OnlyCase(Closing, Record("intake", "2026-03-02"));
        Assert.Null(open.Completed);

        var silent = OnlyCase(Closing.Replace("\"completes\": { \"field\": \"reason\", \"codes\": [\"completed\", \"referred\"] },", ""),
            Record("intake", "2026-03-02"), Record("closing", "2026-05-20", Code("completed")));
        Assert.NotNull(silent.Closing);
        Assert.Null(silent.Completed);
    }

    [Fact]
    public void Completes_that_does_not_fit_the_types_fields_or_role_is_named_and_not_applied()
    {
        var (_, unknown) = Read(File("fields/care/closing/v1.json", Closing.Replace("\"field\": \"reason\"", "\"field\": \"outcome\"")));
        Assert.Equal(FieldIssueKind.CompletesNoCodedField, Assert.Single(unknown.Issues).Kind);
        Assert.Null(unknown.Completes("closing"));

        var uncoded = Read(File("fields/care/closing/v1.json", Closing.Replace("\"kind\": \"coded\", \"scheme\": \"reason\"", "\"kind\": \"text\""))).Fields;
        Assert.Equal(FieldIssueKind.CompletesNoCodedField, Assert.Single(uncoded.Issues).Kind);
        Assert.Null(uncoded.Completes("closing"));

        var opening = Read(File("fields/care/closing/v1.json", Closing.Replace("\"role\": \"closes\"", "\"role\": \"opens\""))).Fields;
        Assert.Equal(FieldIssueKind.CompletesWithoutClosing, Assert.Single(opening.Issues).Kind);
        Assert.Null(opening.Completes("closing"));
    }

    [Theory]
    [InlineData("\"completes\": \"reason\"")]
    [InlineData("\"completes\": { \"codes\": [\"completed\"] }")]
    [InlineData("\"completes\": { \"field\": \"reason\" }")]
    [InlineData("\"completes\": { \"field\": \"reason\", \"codes\": [] }")]
    [InlineData("\"completes\": { \"field\": \"reason\", \"codes\": [\"completed\", \"completed\"] }")]
    [InlineData("\"completes\": { \"field\": \"reason\", \"codes\": \"completed\" }")]
    public void A_field_file_whose_completes_is_malformed_is_unreadable(string completes)
    {
        var (content, _) = Read(File("fields/care/closing/v1.json",
            Closing.Replace("\"completes\": { \"field\": \"reason\", \"codes\": [\"completed\", \"referred\"] }", completes)));
        Assert.Single(content.Unreadable);
    }
}
