using System.Text.Json.Nodes;
using Openquote.Fields;
using Openquote.Packs;
using Openquote.Records;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class PlannedReaderTests
{
    private const string Session = """
        { "format": "openquote.fields/1", "pack": "care", "type": "session", "version": 1,
          "plans": "next",
          "fields": [ { "name": "date", "kind": "date" }, { "name": "next", "kind": "date" }, { "name": "note", "kind": "text" } ] }
        """;

    private static (VaultContent Content, FieldCatalog Fields) Read(params VaultFile[] files)
    {
        var content = VaultReader.Read(files);
        return (content, new FieldCatalog(content.Fields, [new PackManifest("care", 1, "care", new Dictionary<string, int>(), [])]));
    }

    private int _n;

    private VaultFile Record(string subject, string? day, string? next, string type = "session")
    {
        var n = ++_n;
        var fields = new JsonObject();
        if (day is not null) fields["date"] = day;
        if (next is not null) fields["next"] = next;
        return File(Json(n, $"{type}-{n}", "create", entityType: type, fields: fields), $"subjects/{subject}");
    }

    [Fact]
    public void A_subjects_next_meeting_is_the_day_its_latest_record_names()
    {
        var (content, fields) = Read(
            File("fields/care/session/v1.json", Session),
            // s1: the later session set a new day in place of the earlier one's.
            Record("s1", "2026-10-01", "2026-10-08"),
            Record("s1", "2026-10-08", "2026-10-15"),
            // s2: its latest session names no day, so what the earlier one set no longer holds.
            Record("s2", "2026-10-02", "2026-10-09"),
            Record("s2", "2026-10-09", null),
            // s3: one session, naming a day sooner than s1's; a record with no day of its own sets nothing.
            Record("s3", "2026-10-05", "2026-10-12"),
            Record("s3", null, "2026-12-01"));
        Assert.Empty(content.Unreadable);
        Assert.Empty(fields.Issues);
        Assert.Equal("next", fields.Plans("session"));

        var planned = PlannedReader.Read(EntityMerger.Merge(content.Changes).Values, fields);

        Assert.Equal([("s3", new DateOnly(2026, 10, 12)), ("s1", new DateOnly(2026, 10, 15))], planned.Select(p => (p.Subject, p.Day)));
        Assert.Equal("2026-10-08", planned[1].Record.Fields["date"].GetString());
    }

    [Fact]
    public void A_type_with_no_plans_sets_no_meeting()
    {
        var (content, fields) = Read(File("fields/care/session/v1.json", Session.Replace("\"plans\": \"next\",", "")), Record("s1", "2026-10-01", "2026-10-08"));
        Assert.Null(fields.Plans("session"));
        Assert.Empty(PlannedReader.Read(EntityMerger.Merge(content.Changes).Values, fields));
    }

    [Theory]
    [InlineData("\"plans\": \"later\",")]
    [InlineData("\"plans\": \"note\",")]
    public void Plans_naming_no_date_field_of_the_type_is_named_and_not_applied(string plans)
    {
        var (_, fields) = Read(File("fields/care/session/v1.json", Session.Replace("\"plans\": \"next\",", plans)));
        Assert.Equal(FieldIssueKind.PlansNoDateField, Assert.Single(fields.Issues).Kind);
        Assert.Null(fields.Plans("session"));
    }

    [Theory]
    [InlineData("\"plans\": \"\",")]
    [InlineData("\"plans\": 3,")]
    [InlineData("\"plans\": [\"next\"],")]
    public void A_field_file_whose_plans_is_malformed_is_unreadable(string plans)
    {
        var (content, _) = Read(File("fields/care/session/v1.json", Session.Replace("\"plans\": \"next\",", plans)));
        Assert.Single(content.Unreadable);
    }
}
