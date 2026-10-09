using System.Text.Json.Nodes;
using Openquote.Fields;
using Openquote.Packs;
using Openquote.Records;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class CaseReaderTests
{
    // An intake opens a case and a closing, dated by when it closed, closes it; a session and a referral have no role.
    private static readonly FieldCatalog Fields = Catalog(
        ("intake", """ "role": "opens", """, "date"),
        ("closing", """ "role": "closes", "dated": "closed", """, "closed"),
        ("session", "", "date"),
        ("referral", "", "date"));

    private static FieldCatalog Catalog(params (string Type, string Meta, string DateField)[] types)
    {
        var content = VaultReader.Read(types.Select(t => File($"fields/care/{t.Type}/v1.json",
            $$"""{ "format": "openquote.fields/1", "pack": "care", "type": "{{t.Type}}", "version": 1, {{t.Meta}} "fields": [ { "name": "{{t.DateField}}", "kind": "date" } ] }""")));
        Assert.Empty(content.Unreadable);
        return new FieldCatalog(content.Fields, [new PackManifest("care", 1, "care", new Dictionary<string, int>(), [])]);
    }

    private int _n;

    // A record of `type` under subject s1 (or the given folder), dated `day` in its dating field; "" leaves it undated.
    private JsonObject Record(string type, string day, string folder = "subjects/s1", JsonObject? more = null)
    {
        var n = ++_n;
        var fields = more ?? new JsonObject();
        if (day != "") fields[type == "closing" ? "closed" : "date"] = day;
        var change = Json(n, $"{type}-{n}", "create", entityType: type, fields: fields);
        change["_folder"] = folder;
        return change;
    }

    private static List<Entity> Entities(params JsonObject[] changes)
    {
        var content = VaultReader.Read(changes.Select(c =>
        {
            var folder = (string)c["_folder"]!;
            c.Remove("_folder");
            return File(c, folder);
        }));
        Assert.Empty(content.Unreadable);
        return [.. EntityMerger.Merge(content.Changes).Values];
    }

    private static string[] Types(IEnumerable<Entity> records) => [.. records.Select(r => r.Reference.Type)];

    [Fact]
    public void An_intake_opens_a_case_its_sessions_join_and_a_closing_closes_it()
    {
        var cases = CaseReader.Read("s1", Entities(
            Record("session", "2026-03-10"), Record("intake", "2026-03-02"), Record("closing", "2026-04-20"), Record("referral", "2026-04-01")), Fields);

        var only = Assert.Single(cases.Cases);
        Assert.Equal(["intake", "session", "referral", "closing"], Types(only.Records)); // in time order
        Assert.Equal("intake", only.Opening!.Reference.Type);
        Assert.Equal("closing", only.Closing!.Reference.Type);
        Assert.Equal((new DateOnly(2026, 3, 2), new DateOnly(2026, 4, 20)), (only.Start, only.End!.Value));
        Assert.False(only.IsOpen);
        Assert.Empty(only.AfterClosing);
        Assert.Empty(cases.Undated);
    }

    [Fact]
    public void A_second_intake_after_a_closing_opens_a_second_case()
    {
        var cases = CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-04-20"),
            Record("intake", "2026-09-01"), Record("session", "2026-09-08")), Fields).Cases;

        Assert.Equal(2, cases.Count);
        Assert.Equal(["intake", "closing"], Types(cases[0].Records));
        Assert.Equal(["intake", "session"], Types(cases[1].Records));
        Assert.True(cases[1].IsOpen);
        Assert.Null(cases[1].End);
    }

    [Fact]
    public void An_intake_while_a_case_is_open_ends_it_unclosed_and_opens_another()
    {
        var cases = CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("session", "2026-03-09"), Record("intake", "2026-06-01")), Fields).Cases;

        Assert.Equal(2, cases.Count);
        Assert.True(cases[0].FollowedByOpening);
        Assert.False(cases[0].IsOpen);
        Assert.Null(cases[0].Closing); // not counted as closed
        Assert.True(cases[1].IsOpen);
    }

    [Fact]
    public void Records_after_a_closing_follow_that_case_and_open_no_other()
    {
        var cases = CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-04-20"),
            Record("session", "2026-05-11"), Record("closing", "2026-05-25")), Fields).Cases;

        var only = Assert.Single(cases);
        Assert.Equal(["intake", "closing"], Types(only.Records));
        Assert.Equal(["session", "closing"], Types(only.AfterClosing)); // a follow-up and the closing that confirms it
        Assert.Equal(new DateOnly(2026, 4, 20), only.End);              // the first closing ends it
    }

    [Fact]
    public void Records_before_any_intake_begin_a_case_without_one()
    {
        var cases = CaseReader.Read("s1", Entities(
            Record("session", "2026-01-12"), Record("session", "2026-01-19"), Record("closing", "2026-02-02"),
            Record("intake", "2026-03-02")), Fields).Cases;

        Assert.Equal(2, cases.Count);
        Assert.Null(cases[0].Opening);
        Assert.Equal(["session", "session", "closing"], Types(cases[0].Records));
        Assert.Equal(new DateOnly(2026, 1, 12), cases[0].Start);
        Assert.Equal("intake", cases[1].Opening!.Reference.Type);
    }

    [Fact]
    public void A_closing_with_nothing_before_it_is_a_case_of_its_own()
    {
        var only = Assert.Single(CaseReader.Read("s1", Entities(Record("closing", "2026-02-02")), Fields).Cases);

        Assert.Equal(["closing"], Types(only.Records));
        Assert.Null(only.Opening);
        Assert.Equal(only.Closing, only.Records[0]);
    }

    [Fact]
    public void A_group_session_joins_the_case_of_each_subject_it_lists()
    {
        var entities = Entities(
            Record("intake", "2026-03-02"),
            Record("intake", "2026-03-03", "subjects/s2"),
            Record("session", "2026-03-10", "groups/g1", new JsonObject { ["attendees"] = new JsonArray("s1", "s2") }),
            Record("session", "2026-03-11", "groups/g1", new JsonObject { ["attendees"] = new JsonArray("s2") }));

        Assert.Equal(["intake", "session"], Types(Assert.Single(CaseReader.Read("s1", entities, Fields).Cases).Records));
        Assert.Equal(["intake", "session", "session"], Types(Assert.Single(CaseReader.Read("s2", entities, Fields).Cases).Records));
    }

    [Fact]
    public void A_record_without_a_date_is_in_no_case_and_is_listed_apart()
    {
        var cases = CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("session", ""), Record("session", "", more: new JsonObject { ["date"] = "March" })), Fields);

        Assert.Equal(["intake"], Types(Assert.Single(cases.Cases).Records));
        Assert.Equal(["session", "session"], Types(cases.Undated));
    }

    [Fact]
    public void Records_of_one_day_are_read_in_the_order_they_were_written()
    {
        // A closing and a new intake on one day: the closing, written first, closes the old case.
        var cases = CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-06-01"), Record("intake", "2026-06-01")), Fields).Cases;

        Assert.Equal(2, cases.Count);
        Assert.Equal("closing", cases[0].Closing!.Reference.Type);
        Assert.False(cases[0].FollowedByOpening);
    }

    [Fact]
    public void Another_subjects_records_destroyed_records_and_the_subject_itself_are_left_out()
    {
        var intake = Record("intake", "2026-03-02");
        var another = Record("session", "2026-03-04", "subjects/s2");
        var gone = Record("session", "2026-03-05");
        var goneAt = _n;
        var subject = Json(++_n, "s1", "create", entityType: "subject", fields: new JsonObject { ["name"] = "x" });
        subject["_folder"] = "subjects/s1";
        var destroyed = Json(++_n, (string)gone["entity"]!["id"]!, "destroy", [goneAt], entityType: "session");
        destroyed["_folder"] = "subjects/s1";

        var entities = Entities(intake, another, gone, subject, destroyed);

        Assert.Equal(["intake"], Types(Assert.Single(CaseReader.Read("s1", entities, Fields).Cases).Records));
    }

    [Fact]
    public void With_no_pack_giving_roles_every_dated_record_is_one_case()
    {
        var plain = Catalog(("intake", "", "date"), ("session", "", "date"));

        var only = Assert.Single(CaseReader.Read("s1", Entities(Record("intake", "2026-03-02"), Record("session", "2026-03-09")), plain).Cases);

        Assert.Null(only.Opening);
        Assert.True(only.IsOpen);
    }

    // The same, with a follow-up expected within 28 days of a closing.
    private static readonly FieldCatalog Following = Catalog(
        ("intake", """ "role": "opens", """, "date"),
        ("closing", """ "role": "closes", "dated": "closed", "followUpDays": 28, """, "closed"),
        ("session", "", "date"));

    private static readonly DateOnly Closed = new(2026, 4, 20);

    [Fact]
    public void A_closing_that_expects_a_follow_up_is_due_its_days_later_and_waits_until_then()
    {
        var only = Assert.Single(CaseReader.Read("s1", Entities(Record("intake", "2026-03-02"), Record("closing", "2026-04-20")), Following).Cases);

        Assert.Equal(Closed.AddDays(28), only.FollowUpDue);
        Assert.Null(only.FirstAfterClosing);
        Assert.Equal(FollowUp.Waiting, only.FollowUpOn(Closed.AddDays(28)));
        Assert.Equal(FollowUp.Overdue, only.FollowUpOn(Closed.AddDays(29)));
    }

    [Theory]
    [InlineData("2026-05-04", FollowUp.Done)]
    [InlineData("2026-05-18", FollowUp.Done)] // on the day it is due
    [InlineData("2026-05-25", FollowUp.Late)]
    public void Any_record_of_the_subject_after_the_closing_is_its_follow_up(string day, FollowUp expected)
    {
        var only = Assert.Single(CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-04-20"), Record("session", day)), Following).Cases);

        Assert.Equal("session", only.FirstAfterClosing!.Reference.Type);
        Assert.Equal(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), only.FirstAfterClosingDay);
        Assert.Equal(expected, only.FollowUpOn(new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void A_late_record_still_to_come_leaves_the_follow_up_overdue_on_a_day_before_it()
    {
        var only = Assert.Single(CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-04-20"), Record("session", "2026-06-01")), Following).Cases);

        Assert.Equal(FollowUp.Overdue, only.FollowUpOn(new DateOnly(2026, 5, 25)));
        Assert.Equal(FollowUp.Late, only.FollowUpOn(new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public void A_record_of_the_closing_day_written_after_the_closing_is_the_cases_and_not_its_follow_up()
    {
        var only = Assert.Single(CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-04-20"), Record("session", "2026-04-20"), Record("session", "2026-04-27")), Following).Cases);

        Assert.Equal(["intake", "closing", "session"], Types(only.Records));
        Assert.Equal(new DateOnly(2026, 4, 20), only.End);
        Assert.Equal("2026-04-27", (string?)only.AfterClosing.Single().Fields["date"].GetString());
        Assert.Equal(new DateOnly(2026, 4, 27), only.FirstAfterClosingDay);
        Assert.Equal(FollowUp.Done, only.FollowUpOn(new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void A_closing_day_alone_leaves_the_follow_up_waiting()
    {
        var only = Assert.Single(CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-04-20"), Record("session", "2026-04-20")), Following).Cases);

        Assert.Null(only.FirstAfterClosing);
        Assert.Empty(only.AfterClosing);
        Assert.Equal(FollowUp.Waiting, only.FollowUpOn(new DateOnly(2026, 4, 21)));
    }

    [Fact]
    public void A_new_intake_on_the_closing_day_opens_the_next_case_and_is_not_its_follow_up()
    {
        var cases = CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-04-20"), Record("intake", "2026-04-20")), Following).Cases;

        Assert.Equal(2, cases.Count);
        Assert.Null(cases[0].FirstAfterClosing);
        Assert.Equal(FollowUp.Waiting, cases[0].FollowUpOn(new DateOnly(2026, 4, 21)));
    }

    [Fact]
    public void A_new_intake_after_the_closing_is_the_follow_up_of_the_case_before()
    {
        var cases = CaseReader.Read("s1", Entities(
            Record("intake", "2026-03-02"), Record("closing", "2026-04-20"), Record("intake", "2026-05-01")), Following).Cases;

        Assert.Equal(2, cases.Count);
        Assert.Equal("intake", cases[0].FirstAfterClosing!.Reference.Type);
        Assert.Equal(FollowUp.Done, cases[0].FollowUpOn(new DateOnly(2026, 6, 30)));
        Assert.Equal(FollowUp.NotExpected, cases[1].FollowUpOn(new DateOnly(2026, 6, 30))); // still open
    }

    [Fact]
    public void A_closing_that_expects_nothing_leaves_no_follow_up()
    {
        var only = Assert.Single(CaseReader.Read("s1", Entities(Record("intake", "2026-03-02"), Record("closing", "2026-04-20")), Fields).Cases);

        Assert.Null(only.FollowUpDue);
        Assert.Equal(FollowUp.NotExpected, only.FollowUpOn(new DateOnly(2027, 1, 1)));
    }
}
