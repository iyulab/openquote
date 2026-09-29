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

        var form = new ReportDefinition("monthly", 1, "M", "session", "day", "kind", "kind", 1, null);
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
        var form = new ReportDefinition("monthly", 1, "M", "session", "day", "kind", "kind", 1, null);
        var formFile = new VaultFile("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
            """{"format":"openquote.report/0","report":"monthly","version":1,"label":"M","counts":"session","period":{"unit":"month","field":"day"},"rows":{"field":"kind","scheme":"kind","version":1}}"""));
        var people = new Dictionary<string, IReadOnlyList<string>> { ["r1"] = ["s1"], ["r2"] = ["s1", "s2"], ["r3"] = [] };
        var run = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [],
            [new ReportCell("a", null, ["r1", "r2"])], ["r3"], [], people);
        var without = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [], [new ReportCell("a", null, ["r1"])], [], []);
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

    [Fact]
    public void A_reclassification_moves_a_pending_record_into_a_cell()
    {
        var catalog = new SchemeCatalog(
            [new Scheme("kind", 1, [new("c", "C", null, false)]), new Scheme("kind", 2, [new("p", "P", null, false), new("q", "Q", null, false)])],
            [new Crosswalk("kind", 1, 2, [("c", "p"), ("c", "q")])]);
        var form = new ReportDefinition("monthly", 2, "Monthly", "session", "day", "kind", "kind", 2, null);
        JsonObject Coded(int v, string code) => new() { ["scheme"] = "kind", ["version"] = v, ["code"] = code };

        var w = Writer();
        var subject = w.CreateSubject(Fields(("name", "x")));
        var subjectId = Read([subject]).Content.Changes[0].Entity.Id;
        var item = w.CreateInSubject(subjectId, "session", Fields(("day", "2026-03-05"), ("kind", Coded(1, "c"))));
        var before = Read([subject, item]).Entities.Values;
        Assert.Single(ReportRunner.RunMonth(form, 2026, 3, before, catalog).Pending);

        var reclassify = w.Reclassify(before.Single(e => e.Reference.Type == "session"), "kind", Coded(2, "q"));
        var (content, after) = Read([subject, item, reclassify]);

        Assert.Equal(ChangeOp.Reclassify, content.Changes.Single(c => c.Path == reclassify.Path).Op);
        var run = ReportRunner.RunMonth(form, 2026, 3, after.Values, catalog);
        Assert.Empty(run.Pending);
        Assert.Equal("q", Assert.Single(run.Cells).Row);
    }

    [Fact]
    public void A_run_record_is_filed_under_its_year()
    {
        var run = new ReportRun(new ReportDefinition("monthly", 1, "M", "session", "day", "kind", "kind", 1, null),
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [], [], [], []);

        var file = Writer().RunRecord(run);

        Assert.Matches(@"^runs/2026/[0-9a-f-]{36}\.dev1\.json$", file.Path);
        Assert.Contains("\"openquote.run/0\"", System.Text.Encoding.UTF8.GetString(file.Content.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_record_reads_back_as_the_run_it_records_and_explains_a_later_one()
    {
        var form = new ReportDefinition("monthly", 1, "M", "session", "day", "kind", "kind", 1, "who");
        var formFile = new VaultFile("reports/monthly/v1.json", System.Text.Encoding.UTF8.GetBytes(
            """{"format":"openquote.report/0","report":"monthly","version":1,"label":"M","counts":"session","period":{"unit":"month","field":"day"},"rows":{"field":"kind","scheme":"kind","version":1},"columns":{"field":"who"}}"""));
        var earlier = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ["1-2"],
            [new ReportCell("a", "p1", ["r1", "r2"]), new ReportCell("b", null, ["r3"])], ["r4"], []);
        var later = new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ["1-2"],
            [new ReportCell("a", "p1", ["r1"]), new ReportCell("b", null, ["r2", "r3"])], [], ["r4", "r5"]);
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

    [Fact]
    public void A_run_record_without_its_report_form_is_unreadable()
    {
        var form = new ReportDefinition("monthly", 1, "M", "session", "day", "kind", "kind", 1, null);
        var file = Writer().RunRecord(new ReportRun(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [], [], [], []));

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
