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

        var diff = ReportDiff.Compare(content.Runs[0].Run, content.Runs[1].Run);
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
