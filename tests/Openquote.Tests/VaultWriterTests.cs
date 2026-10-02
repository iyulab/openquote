using System.Text.Json.Nodes;
using Openquote.Classification;
using Openquote.Records;
using Openquote.Reports;
using Openquote.Vault;

namespace Openquote.Tests;

public class VaultWriterTests
{
    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus9", TimeSpan.FromHours(9), "plus9", "plus9");
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private static VaultWriter Writer(string device = "dev1") =>
        new(device, new StepClock(new DateTimeOffset(2026, 3, 31, 14, 59, 59, TimeSpan.Zero)));

    private static Dictionary<string, JsonNode?> Fields(params (string Key, JsonNode? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    private static (VaultContent Content, IReadOnlyDictionary<EntityRef, Entity> Entities) Read(IEnumerable<VaultFile> files)
    {
        var content = VaultReader.Read(files);
        Assert.Empty(content.Unreadable);
        return (content, EntityMerger.Merge(content.Changes));
    }

    [Fact]
    public void What_it_writes_reads_back_as_the_same_entities()
    {
        var w = Writer();
        var subject = w.CreateSubject(Fields(("name", "someone"), ("phone", null)));
        var subjectId = Read([subject]).Content.Changes[0].Entity.Id;
        var item = w.CreateInSubject(subjectId, "session", Fields(("day", "2026-04-01")), new Dictionary<string, string> { ["day"] = "suggestion" });

        var (content, entities) = Read([subject, item]);

        Assert.Equal($"subjects/{subjectId}", subject.Path[..subject.Path.LastIndexOf('/')]);
        Assert.StartsWith($"subjects/{subjectId}/", item.Path, StringComparison.Ordinal);
        Assert.EndsWith(".dev1.json", item.Path, StringComparison.Ordinal);
        Assert.Equal("someone", entities[new EntityRef("subject", subjectId)].Fields["name"].GetString());
        Assert.Equal(subjectId, entities[new EntityRef("subject", subjectId)].Subject);
        Assert.All(entities.Values.Where(e => e.Reference.Type == "session"), e => Assert.Equal(subjectId, e.Subject));
        var session = content.Changes.Single(c => c.Entity.Type == "session");
        Assert.Equal("suggestion", session.Source["day"]);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 1, TimeSpan.FromHours(9)), session.At); // local time (UTC 15:00:01 is the next day at +09:00), whole seconds
    }

    private static JsonObject Coded(int version, string code) => new() { ["scheme"] = "kind", ["version"] = version, ["code"] = code };

    [Fact]
    public void A_group_keeps_its_sessions_in_its_own_folder_and_counts_its_attendees()
    {
        var w = Writer();
        var a = w.CreateSubject(Fields(("name", "a")));
        var b = w.CreateSubject(Fields(("name", "b")));
        var (aId, bId) = (Read([a]).Content.Changes[0].Entity.Id, Read([b]).Content.Changes[0].Entity.Id);
        var group = w.CreateGroup(Fields(("name", "friendship")));
        var groupId = Read([group]).Content.Changes[0].Entity.Id;
        var together = w.CreateInGroup(groupId, "session",
            Fields(("day", "2026-03-10"), ("kind", Coded(1, "a")), ("attendees", new JsonArray(bId, aId, aId))));
        var alone = w.CreateInSubject(aId, "session", Fields(("day", "2026-03-11"), ("kind", Coded(1, "a"))));

        var (content, entities) = Read([a, b, group, together, alone]);

        Assert.StartsWith($"groups/{groupId}/", together.Path, StringComparison.Ordinal);
        var session = entities.Values.Single(e => e.Reference.Type == "session" && e.Group is not null);
        Assert.Equal(groupId, session.Group);
        Assert.Null(session.Subject);
        Assert.Equal(new[] { aId, bId }.Order(StringComparer.Ordinal), session.People);
        Assert.Equal(groupId, entities[new EntityRef("group", groupId)].Group);
        Assert.Empty(entities[new EntityRef("group", groupId)].People);

        var form = TestReports.Form("monthly", 1, "M", "session", "day", "kind", "kind", 1, null);
        var catalog = new SchemeCatalog([new Scheme("kind", 1, [new("a", "A", null, false)])], []);
        var run = ReportRunner.RunMonth(form, 2026, 3, entities.Values, catalog);
        var cell = Assert.Single(run.Cells);
        Assert.Equal(2, cell.Count);                            // two sessions
        Assert.Equal(2, run.PeopleOf(cell.Records)!.Count);     // two people, a counted once
        Assert.Equal(2, run.PeopleOf(run.Total)!.Count);
    }

    [Fact]
    public void Nothing_but_a_case_or_a_session_is_kept_in_another_entitys_folder()
    {
        var w = Writer();
        Assert.Throws<ArgumentException>(() => w.CreateInGroup("g", "subject", Fields(("name", "x"))));
        Assert.Throws<ArgumentException>(() => w.CreateInSubject("s", "group", Fields(("name", "x"))));
    }

    [Fact]
    public void A_run_record_keeps_who_each_record_is_about()
    {
        var form = TestReports.Form("monthly", 1, "M", "session", "day", "kind", "kind", 1, null);
        var formFile = new VaultFile("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
            """{"format":"openquote.report/0","report":"monthly","version":1,"label":"M","counts":"session","period":{"unit":"month","field":"day"},"rows":{"field":"kind","scheme":"kind","version":1}}"""));
        var people = new Dictionary<string, IReadOnlyList<string>> { ["r1"] = ["s1"], ["r2"] = ["s1", "s2"], ["r3"] = [] };
        var run = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [],
            [TestReports.Cell("a", null, ["r1", "r2"])], ["r3"], [], [], [], people);
        var without = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [], [TestReports.Cell("a", null, ["r1"])], [], [], [], []);
        var w = Writer();

        var content = Read([w.RunRecord(run), w.RunRecord(without), formFile]).Content;

        var kept = content.Runs[0].Run;
        Assert.Equal(["s1", "s2"], kept.PeopleOf(kept.Cells[0].Records));
        Assert.Empty(kept.PeopleOf(kept.Pending)!);
        Assert.Null(content.Runs[1].Run.PeopleOf(content.Runs[1].Run.Total)); // kept before people were counted: unknown, not zero
    }

    [Theory]
    [InlineData("""["r1"]""")]          // a record of the cells left out of the total
    [InlineData("""["r1","r2","r9"]""")] // a record in the total that no cell, pending or unmapped holds
    public void A_run_record_whose_total_is_not_its_parts_is_unreadable(string totalRecords)
    {
        var run = RunJson(total: $$"""{"count":2,"records":{{totalRecords}}}""");

        var content = VaultReader.Read([MonthlyForm, new VaultFile("runs/2026/0192f400-0000-7000-8000-000000000001.dev1.json", System.Text.Encoding.UTF8.GetBytes(run))]);

        Assert.Empty(content.Runs);
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(content.Unreadable).Reason);
    }

    [Fact]
    public void A_run_records_stored_counts_are_not_trusted_over_its_records()
    {
        var run = RunJson(cellCount: 99, total: """{"count":99,"records":["r1","r2"]}""");

        var content = VaultReader.Read([MonthlyForm, new VaultFile("runs/2026/0192f400-0000-7000-8000-000000000001.dev1.json", System.Text.Encoding.UTF8.GetBytes(run))]);

        var kept = Assert.Single(content.Runs).Run;
        Assert.Equal(["r1", "r2"], kept.Cells[0].Records);
        Assert.Equal(2, kept.Total.Count);
    }

    private static readonly VaultFile MonthlyForm = new("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
        """{"format":"openquote.report/0","report":"monthly","version":1,"label":"M","counts":"session","period":{"unit":"month","field":"day"},"rows":{"field":"kind","scheme":"kind","version":1}}"""));

    private static string RunJson(string total, int cellCount = 2) =>
        $$"""{"format":"openquote.run/0","id":"0192f400-0000-7000-8000-000000000001","device":"dev1","at":"2026-04-01T09:00:00+09:00","report":{"report":"monthly","version":1},"period":{"from":"2026-03-01","to":"2026-03-31"},"cells":[{"row":"a","column":null,"count":{{cellCount}},"records":["r1","r2"]}],"pending":{"count":0,"records":[]},"unmapped":{"count":0,"records":[]},"total":{{total}}}""";

    [Fact]
    public void A_run_record_whose_people_miss_a_record_is_unreadable()
    {
        var formFile = new VaultFile("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
            """{"format":"openquote.report/0","report":"monthly","version":1,"label":"M","counts":"session","period":{"unit":"month","field":"day"},"rows":{"field":"kind","scheme":"kind","version":1}}"""));
        var run = """{"format":"openquote.run/0","id":"0192f400-0000-7000-8000-000000000001","device":"dev1","at":"2026-04-01T09:00:00+09:00","report":{"report":"monthly","version":1},"period":{"from":"2026-03-01","to":"2026-03-31"},"cells":[{"row":"a","column":null,"count":2,"records":["r1","r2"]}],"pending":{"count":0,"records":[]},"unmapped":{"count":0,"records":[]},"total":{"count":2,"records":["r1","r2"]},"people":{"r1":["s1"]}}""";

        var content = VaultReader.Read([formFile, new VaultFile("runs/2026/0192f400-0000-7000-8000-000000000001.dev1.json", System.Text.Encoding.UTF8.GetBytes(run))]);

        Assert.Empty(content.Runs);
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(content.Unreadable).Reason);
    }

    [Fact]
    public void Every_file_gets_a_new_path_in_time_order()
    {
        var w = Writer();
        var files = Enumerable.Range(0, 50).Select(_ => w.CreatePractitioner(Fields(("name", "x")))).ToList();

        Assert.Equal(50, files.Select(f => f.Path).Distinct().Count());
        Assert.Equal(files.Select(f => f.Path), files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.All(files, f => Assert.StartsWith("practitioners/", f.Path, StringComparison.Ordinal));
        Assert.All(Read(files).Entities.Values, e => Assert.Null(e.Subject));
    }

    [Fact]
    public void An_update_has_seen_the_whole_entity_and_settles_a_conflict()
    {
        var one = Writer("dev1");
        var two = Writer("dev2");
        var create = one.CreatePractitioner(Fields(("name", "old")));
        var entity = Read([create]).Entities.Values.Single();

        // Two devices edit without seeing each other...
        var left = one.Update(entity, Fields(("name", "left")));
        var right = two.Update(entity, Fields(("name", "right")));
        var conflicted = Read([create, left, right]).Entities.Values.Single();
        Assert.Equal(2, conflicted.Conflicts["name"].Count);
        Assert.Equal(2, conflicted.Heads.Count);

        // ...and a person picks one on either device.
        var settle = one.Update(conflicted, Fields(("name", "left")));
        var settled = Read([create, left, right, settle]).Entities.Values.Single();

        Assert.Empty(settled.Conflicts);
        Assert.Equal("left", settled.Fields["name"].GetString());
        Assert.Single(settled.Heads);
    }

    private static readonly SchemeCatalog SplitCatalog = new(
        [
            new Scheme("kind", 1, [new("c", "C", null, false), new("a", "A", null, false)]),
            new Scheme("kind", 2, [new("p", "P", null, false), new("q", "Q", null, false), new("r", "R", null, false), new("x", "X", null, false)]),
            new Scheme("other", 2, [new("p", "P", null, false)]),
        ],
        [new Crosswalk("kind", 1, 2, [("c", "p"), ("c", "q"), ("a", "x")])]);

    private static JsonObject KindValue(int v, string code) => new() { ["scheme"] = "kind", ["version"] = v, ["code"] = code };

    private static (VaultWriter Writer, VaultFile[] Files, Entity Session) OneSession(string code)
    {
        var w = Writer();
        var subject = w.CreateSubject(Fields(("name", "x")));
        var subjectId = Read([subject]).Content.Changes[0].Entity.Id;
        var item = w.CreateInSubject(subjectId, "session", Fields(("day", "2026-03-05"), ("kind", KindValue(1, code))));
        return (w, [subject, item], Read([subject, item]).Entities.Values.Single(e => e.Reference.Type == "session"));
    }

    [Fact]
    public void A_reclassification_moves_a_pending_record_into_a_cell()
    {
        var form = TestReports.Form("monthly", 2, "Monthly", "session", "day", "kind", "kind", 2, null);
        var (w, files, session) = OneSession("c");
        Assert.Single(ReportRunner.RunMonth(form, 2026, 3, [session], SplitCatalog).Pending);

        var reclassify = w.Reclassify(session, "kind", new CodedValue("kind", 2, "q"), SplitCatalog);
        var (content, after) = Read([.. files, reclassify]);

        Assert.Equal(ChangeOp.Reclassify, content.Changes.Single(c => c.Path == reclassify.Path).Op);
        var run = ReportRunner.RunMonth(form, 2026, 3, after.Values, SplitCatalog);
        Assert.Empty(run.Pending);
        Assert.Equal("q", Assert.Single(run.Cells).Row);
    }

    [Theory]
    [InlineData("kind", 2, "r")]     // in the newer version, but not one of the record's candidates
    [InlineData("kind", 2, "zzz")]   // not an item at all
    [InlineData("kind", 1, "a")]     // the older version, which is not the one the record waits in
    [InlineData("other", 2, "p")]    // another scheme
    public void A_reclassification_outside_the_candidates_is_refused(string scheme, int version, string code)
    {
        var (w, _, session) = OneSession("c");

        var error = Assert.Throws<ArgumentException>(() => w.Reclassify(session, "kind", new CodedValue(scheme, version, code), SplitCatalog));

        Assert.Equal("choice", error.ParamName);
    }

    [Fact]
    public void A_record_that_is_not_waiting_takes_no_reclassification()
    {
        var (w, _, session) = OneSession("a"); // a has one link, so the record is already placed in v2

        var error = Assert.Throws<ArgumentException>(() => w.Reclassify(session, "kind", new CodedValue("kind", 2, "x"), SplitCatalog));

        Assert.Contains("not waiting", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_record_is_filed_under_its_year()
    {
        var run = new ReportRun(TestReports.Form("monthly", 1, "M", "session", "day", "kind", "kind", 1, null),
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [], [], [], [], [], []);

        var file = Writer().RunRecord(run);

        Assert.Matches(@"^runs/2026/[0-9a-f-]{36}\.dev1\.json$", file.Path);
        Assert.Contains("\"openquote.run/0\"", System.Text.Encoding.UTF8.GetString(file.Content.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_record_reads_back_as_the_run_it_records_and_explains_a_later_one()
    {
        var form = TestReports.Form("monthly", 1, "M", "session", "day", "kind", "kind", 1, "who");
        var formFile = new VaultFile("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
            """{"format":"openquote.report/0","report":"monthly","version":1,"label":"M","counts":"session","period":{"unit":"month","field":"day"},"rows":{"field":"kind","scheme":"kind","version":1},"columns":{"field":"who"}}"""));
        var earlier = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [new ReportScheme("kind", 1, ["1-2"], [])],
            [TestReports.Cell("a", "p1", ["r1", "r2"]), TestReports.Cell("b", null, ["r3"])], ["r4"], [], [], []);
        var later = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [new ReportScheme("kind", 1, ["1-2"], [])],
            [TestReports.Cell("a", "p1", ["r1"]), TestReports.Cell("b", null, ["r2", "r3"])], [], ["r4"], ["r5"], []);
        var w = Writer();
        var files = new[] { w.RunRecord(earlier), w.RunRecord(later), formFile };

        var content = Read(files).Content;

        Assert.Equal(2, content.Runs.Count);
        var kept = content.Runs[0];
        Assert.Equal(files[0].Path, kept.Path);
        Assert.Equal("dev1", kept.Device);
        Assert.Equal(form, kept.Run.Report);
        Assert.Equal(["1-2"], kept.Run.Crosswalks);
        Assert.Equal(earlier.Total, kept.Run.Total);
        Assert.Null(kept.Run.Cells[1].Column);

        var diff = ReportDiff.Compare(content.Runs[0].Run, content.Runs[1].Run, content.Catalog());
        Assert.Equal(["r5"], diff.Late);
        Assert.Equal(["r2", "r4"], diff.Moved);
        Assert.Equal(["r1", "r3"], diff.Unchanged);
    }

    private static readonly ReportDefinition Monthly = TestReports.Form("monthly", 1, "M", "session", "day", "kind", "kind", 1, null);

    private static ReportRun March(string[] unmapped, string[] blank, string[] conflicted) =>
        new(Monthly, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [new ReportScheme("kind", 1, [], [])], [], [], unmapped, blank, conflicted);

    private static VaultFile RunFile(string rest) => new("runs/2026/0199b2e0-3a57-7012-8c64-4f1d2e3b5a71.desk01.json",
        System.Text.Encoding.UTF8.GetBytes("{ " + RunHead + ", " + rest + " }"));

    private const string RunHead = """ "format": "openquote.run/0", "id": "0199b2e0-3a57-7012-8c64-4f1d2e3b5a71", "device": "desk01", "at": "2026-04-02T09:00:00+01:00", "report": { "report": "monthly", "version": 1 }, "schemes": { "kind": { "version": 1 } }, "period": { "from": "2026-03-01", "to": "2026-03-31" } """;

    [Fact]
    public void A_run_record_with_blank_or_conflicted_records_is_format_1_and_lists_both_and_any_other_stays_format_0()
    {
        var w = Writer();
        var files = new[] { w.RunRecord(March(["r1"], ["r2"], [])), w.RunRecord(March(["r1"], [], ["r3"])), w.RunRecord(March(["r1"], [], [])), MonthlyForm };

        var content = Read(files).Content;

        string Text(int i) => System.Text.Encoding.UTF8.GetString(files[i].Content.Span);
        foreach (var json in new[] { Text(0), Text(1) })
        {
            Assert.Contains("openquote.run/1", json, StringComparison.Ordinal);
            Assert.Contains("\"blank\"", json, StringComparison.Ordinal);
            Assert.Contains("\"conflicted\"", json, StringComparison.Ordinal);
        }
        Assert.Contains("openquote.run/0", Text(2), StringComparison.Ordinal);
        Assert.DoesNotContain("\"blank\"", Text(2), StringComparison.Ordinal);
        Assert.DoesNotContain("\"conflicted\"", Text(2), StringComparison.Ordinal);
        Assert.Equal(["r1"], content.Runs[0].Run.Unmapped);
        Assert.Equal(["r2"], content.Runs[0].Run.Blank);
        Assert.Equal(["r3"], content.Runs[1].Run.Conflicted);
        Assert.Equal(["r1", "r3"], content.Runs[1].Run.Total);
    }

    [Fact]
    public void A_format_0_run_record_that_lists_blank_records_within_unmapped_reads_them_apart()
    {
        // As engines before format 1 wrote it: blank records are also unmapped, and in the total once.
        var file = RunFile(""" "cells": [], "pending": { "records": [] }, "unmapped": { "records": [ "r1", "r2" ] }, "blank": { "records": [ "r2" ] }, "total": { "records": [ "r1", "r2" ] } """);

        var content = VaultReader.Read([file, MonthlyForm]);

        var run = Assert.Single(content.Runs).Run;
        Assert.Equal(["r1"], run.Unmapped);
        Assert.Equal(["r2"], run.Blank);
        Assert.Equal(["r1", "r2"], run.Total);
    }

    [Fact]
    public void A_format_0_run_record_with_a_blank_record_outside_unmapped_is_unreadable()
    {
        var file = RunFile(""" "cells": [ { "row": "a", "column": null, "records": [ "r1" ] } ], "pending": { "records": [] }, "unmapped": { "records": [] }, "blank": { "records": [ "r1" ] }, "total": { "records": [ "r1" ] } """);

        var content = VaultReader.Read([file, MonthlyForm]);

        Assert.Empty(content.Runs);
        Assert.Contains("unmapped", Assert.Single(content.Unreadable).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_format_1_run_record_that_places_a_record_twice_is_unreadable()
    {
        var file = Writer().RunRecord(March(["r1"], ["r1"], []));

        var content = VaultReader.Read([file, MonthlyForm]);

        Assert.Empty(content.Runs);
        Assert.Contains("each record once", Assert.Single(content.Unreadable).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_of_a_form_counting_in_the_version_in_force_keeps_the_version_and_the_days_it_changed()
    {
        var formFile = new VaultFile("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
            """{"format":"openquote.report/1","report":"monthly","version":1,"label":"M","counts":"session","period":{"unit":"month","field":"day"},"dimensions":[{"field":"kind","scheme":"kind","version":"in-force"}]}"""));
        var counted = Monthly.WithRowVersion(2);
        var run = new ReportRun(counted, new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30),
            [new ReportScheme("kind", 2, ["1-2"],
                [new SchemeBoundary(new DateOnly(2026, 4, 1), 1, null), new SchemeBoundary(new DateOnly(2026, 4, 15), null, 2)])],
            [], [], ["r1"], [], []);
        var file = Writer().RunRecord(run);

        var content = VaultReader.Read([file, formFile]);

        Assert.Null(Assert.Single(content.Reports).RowVersion);
        var kept = Assert.Single(content.Runs).Run;
        Assert.Equal(counted, kept.Report);
        Assert.Equal(run.Boundaries, kept.Boundaries);
        Assert.Contains("\"boundaries\"", System.Text.Encoding.UTF8.GetString(file.Content.Span), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openquote.report/0", "\"in-force\"")]
    [InlineData("openquote.report/1", "\"latest\"")]
    [InlineData("openquote.report/1", "0")]
    public void Only_a_format_1_report_form_may_count_in_the_version_in_force(string format, string version)
    {
        var formFile = new VaultFile("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
            "{\"format\":\"" + format + "\",\"report\":\"monthly\",\"version\":1,\"label\":\"M\",\"counts\":\"session\",\"period\":{\"unit\":\"month\",\"field\":\"day\"},"
            + (format.EndsWith("/0", StringComparison.Ordinal)
                ? "\"rows\":{\"field\":\"kind\",\"scheme\":\"kind\",\"version\":" + version + "}}"
                : "\"dimensions\":[{\"field\":\"kind\",\"scheme\":\"kind\",\"version\":" + version + "}]}")));

        var content = VaultReader.Read([formFile]);

        Assert.Empty(content.Reports);
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(content.Unreadable).Reason);
    }

    private static VaultFile FormFile(string body) => new("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
        """{"report":"monthly","version":1,"label":"M","counts":"session","period":{"unit":"month","field":"day"},""" + body));

    [Fact]
    public void Rows_and_a_column_and_the_same_two_dimensions_are_one_form()
    {
        var v0 = VaultReader.Read([FormFile(""" "format":"openquote.report/0","rows":{"field":"kind","scheme":"kind","version":1},"columns":{"field":"who"}}""")]);
        var v1 = VaultReader.Read([FormFile(""" "format":"openquote.report/1","dimensions":[{"field":"kind","scheme":"kind","version":1},{"field":"who"}]}""")]);

        Assert.Equal(Assert.Single(v0.Reports), Assert.Single(v1.Reports));
    }

    [Fact]
    public void A_run_keyed_by_three_dimensions_reads_back_as_the_run_it_records()
    {
        var formFile = FormFile(""" "format":"openquote.report/1","dimensions":[{"field":"kind","scheme":"kind","version":1},{"field":"level","scheme":"level","version":2},{"field":"who"}]}""");
        var form = Assert.Single(VaultReader.Read([formFile]).Reports);
        var run = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31),
            [new ReportScheme("kind", 1, [], []), new ReportScheme("level", 2, ["1-2"], [])],
            [new ReportCell(["a", "mid", null], ["r1"]), new ReportCell(["a", "mid", "p1"], ["r2", "r3"])], ["r4"], [], [], []);
        var file = Writer().RunRecord(run);

        var kept = Assert.Single(VaultReader.Read([file, formFile]).Runs).Run;

        Assert.Contains("\"openquote.run/1\"", System.Text.Encoding.UTF8.GetString(file.Content.Span), StringComparison.Ordinal);
        Assert.Equal(form, kept.Report);
        Assert.Equal(run.Cells.Select(c => c.Key), kept.Cells.Select(c => c.Key));
        Assert.Equal(["1-2"], kept.Schemes[1].Crosswalks);
        Assert.Equal(run.Total, kept.Total);
    }

    [Fact]
    public void A_format_1_form_reads_dimensions_of_the_subjects_and_filters()
    {
        var formFile = FormFile(""" "format":"openquote.report/1","dimensions":[{"field":"kind","scheme":"kind","version":1},{"field":"level","scheme":"level","version":"in-force","of":"subject"}],"filters":[{"field":"grade","of":"subject","in":["2","3"]},{"field":"kind","scheme":"kind","version":1,"in":["a"]}]}""");

        var form = Assert.Single(VaultReader.Read([formFile]).Reports);

        Assert.Equal(new ReportDimension("level", "level", null, OfSubject: true), form.Dimensions[1]);
        Assert.Equal([new ReportFilter(new ReportDimension("grade", OfSubject: true), ["2", "3"]), new ReportFilter(new ReportDimension("kind", "kind", 1), ["a"])], form.Filters);
        Assert.Equal(["kind", "level"], form.Schemes);
    }

    [Theory]
    [InlineData(""" "dimensions":[{"field":"kind","scheme":"kind","version":1,"of":"group"}]}""")] // only the subjects
    [InlineData(""" "dimensions":[{"field":"kind","scheme":"kind","version":1}],"filters":[{"field":"grade"}]}""")] // no values
    [InlineData(""" "dimensions":[{"field":"kind","scheme":"kind","version":1}],"filters":[{"field":"grade","in":[]}]}""")] // lets nothing through
    [InlineData(""" "dimensions":[{"field":"kind","scheme":"kind","version":1}],"filters":[{"field":"kind","scheme":"kind","version":2,"in":["a"]}]}""")] // one scheme, two versions
    public void A_format_1_form_with_a_dimension_or_filter_the_engine_cannot_run_is_unreadable(string rest)
    {
        var content = VaultReader.Read([FormFile(""" "format":"openquote.report/1",""" + rest)]);

        Assert.Empty(content.Reports);
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(content.Unreadable).Reason);
    }

    [Fact]
    public void A_run_with_no_single_subject_value_reads_back_with_a_null_place()
    {
        var formFile = FormFile(""" "format":"openquote.report/1","dimensions":[{"field":"kind","scheme":"kind","version":1},{"field":"level","scheme":"level","version":1,"of":"subject"}]}""");
        var form = Assert.Single(VaultReader.Read([formFile]).Reports);
        var run = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31),
            [new ReportScheme("kind", 1, [], []), new ReportScheme("level", 1, [], [])],
            [new ReportCell(["a", null], ["r1"]), new ReportCell(["a", "mid"], ["r2"])], [], [], [], []);

        var kept = Assert.Single(VaultReader.Read([Writer().RunRecord(run), formFile]).Runs).Run;

        Assert.Equal(run.Cells.Select(c => c.Key), kept.Cells.Select(c => c.Key));
    }

    [Theory]
    [InlineData("""[null, "mid", "p1"]""")] // a classified place holds a code
    [InlineData("""["a", "mid"]""")] // one place per dimension
    public void A_cell_key_that_does_not_fit_the_form_is_unreadable(string key)
    {
        var formFile = FormFile(""" "format":"openquote.report/1","dimensions":[{"field":"kind","scheme":"kind","version":1},{"field":"level","scheme":"level","version":2},{"field":"who"}]}""");
        var runFile = new VaultFile("runs/2026/0199b2e0-3a57-7012-8c64-4f1d2e3b5a71.desk01.json", System.Text.Encoding.UTF8.GetBytes(
            """{"format":"openquote.run/1","id":"0199b2e0-3a57-7012-8c64-4f1d2e3b5a71","device":"desk01","at":"2026-04-02T09:00:00+01:00","report":{"report":"monthly","version":1},"schemes":{"kind":{"version":1},"level":{"version":2}},"period":{"from":"2026-03-01","to":"2026-03-31"},"cells":[{"key":""" + key + ""","records":["r1"]}],"pending":{"records":[]},"unmapped":{"records":[]},"blank":{"records":[]},"conflicted":{"records":[]},"total":{"records":["r1"]}}"""));

        var content = VaultReader.Read([formFile, runFile]);

        Assert.Empty(content.Runs);
        Assert.Contains("key", Assert.Single(content.Unreadable).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_record_without_its_report_form_is_unreadable()
    {
        var form = TestReports.Form("monthly", 1, "M", "session", "day", "kind", "kind", 1, null);
        var file = Writer().RunRecord(new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [], [], [], [], [], []));

        var content = VaultReader.Read([file]);

        Assert.Empty(content.Runs);
        Assert.Equal(file.Path, Assert.Single(content.Unreadable).Path);
    }

    [Theory]
    [InlineData("DEV1")]
    [InlineData("abc")]
    [InlineData("dev-1")]
    public void A_device_id_outside_the_name_rule_is_refused(string device)
    {
        Assert.Throws<ArgumentException>(() => new VaultWriter(device));
    }

    [Fact]
    public void A_device_is_named_by_the_device_entity_it_created_and_anyone_may_rename_it()
    {
        var one = Writer("pcone");
        var two = Writer("pctwo");
        var named = one.CreateDevice(Fields(("name", "Counselling room")));
        var unnamed = two.CreateDevice(Fields(("name", "  ")));
        var (content, entities) = Read([named, unnamed]);

        Assert.StartsWith("devices/", named.Path, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["pcone"] = "Counselling room" }, DeviceNames.Of(entities.Values));
        var mine = DeviceNames.EntityOf(entities.Values, "pcone")!;
        Assert.Null(DeviceNames.EntityOf(entities.Values, "pcthree"));

        // The other device renames it; the name still belongs to the device that made the entity.
        var renamed = two.Update(mine, Fields(("name", "Front desk")));
        var (_, after) = Read([named, unnamed, renamed]);
        Assert.Equal("Front desk", DeviceNames.Of(after.Values)["pcone"]);
        Assert.Single(DeviceNames.Of(after.Values));
        Assert.Empty(content.Unreadable);
    }

    [Fact]
    public void A_device_entity_is_not_kept_in_a_subject_folder() =>
        Assert.Throws<ArgumentException>(() => Writer().CreateInSubject("s1", DeviceNames.EntityType, Fields()));

    [Fact]
    public void Refuses_changes_that_would_not_read_back()
    {
        var w = Writer();
        var create = w.CreatePractitioner(Fields(("name", "x")));
        var entity = Read([create]).Entities.Values.Single();

        Assert.Throws<ArgumentException>(() => w.Update(entity, Fields()));
        Assert.Throws<ArgumentException>(() => w.Update(entity, Fields(("name", "y")), new Dictionary<string, string> { ["name"] = "guess" }));
        Assert.Throws<ArgumentException>(() => w.Update(entity, Fields(("name", "y")), new Dictionary<string, string> { ["other"] = "manual" }));
        Assert.Throws<ArgumentException>(() => w.CreateInSubject("s1", "subject", Fields(("name", "x"))));
    }
}
