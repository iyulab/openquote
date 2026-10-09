using System.Text;
using System.Text.Json.Nodes;
using Openquote.Classification;
using Openquote.Exports;
using Openquote.Fields;
using Openquote.Records;
using Openquote.Reports;
using Openquote.Vault;

namespace Openquote.Tests;

public class ActivityRecordTests
{
    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private static readonly SchemeCatalog Catalog = new([new Scheme("kind", 1, [new("training", "Training", null, false), new("talk", "Talk", null, false)])], []);

    private static JsonObject Kind(string code) => new() { ["scheme"] = "kind", ["version"] = 1, ["code"] = code };

    private static Dictionary<string, JsonNode?> Fields(params (string Key, JsonNode? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    private static VaultFile Json(string path, string json) => new(path, Encoding.UTF8.GetBytes(json));

    // A subject with one training-kind record of its own, and two activities: a training given and a talk.
    private static (List<VaultFile> Files, VaultWriter Writer) Build()
    {
        var w = new VaultWriter("dev1", new StepClock());
        var subject = w.CreateSubject(Fields(("name", "someone")));
        var subjectId = VaultReader.Read([subject]).Changes[0].Entity.Id;
        return ([
            subject,
            w.CreateInSubject(subjectId, "work", Fields(("date", "2026-03-02"), ("kind", Kind("training")), ("hours", 1))),
            w.CreateActivity("work", Fields(("date", "2026-03-10"), ("kind", Kind("training")), ("hours", 3))),
            w.CreateActivity("work", Fields(("date", "2026-03-20"), ("kind", Kind("talk")), ("hours", 2))),
        ], w);
    }

    private static IReadOnlyDictionary<EntityRef, Entity> Merge(IEnumerable<VaultFile> files)
    {
        var content = VaultReader.Read(files);
        Assert.Empty(content.Unreadable);
        return EntityMerger.Merge(content.Changes);
    }

    [Fact]
    public void An_activity_is_kept_in_the_activity_folder_about_no_one_and_stays_there_when_changed()
    {
        var (files, w) = Build();
        Assert.All(files.Skip(2), f => Assert.Matches(@"^activities/[^/]+\.dev1\.json$", f.Path));

        var activities = Merge(files).Values.Where(e => e.Activity).ToList();
        Assert.Equal(2, activities.Count);
        Assert.All(activities, a =>
        {
            Assert.Null(a.Subject);
            Assert.Null(a.Group);
            Assert.Empty(a.People);
        });
        Assert.DoesNotContain(Merge(files).Values, e => e.Subject is not null && e.Activity);

        var talk = activities.Single(a => a.Fields["hours"].GetInt32() == 2);
        var changed = w.Update(talk, Fields(("hours", 4)));
        Assert.StartsWith("activities/", changed.Path, StringComparison.Ordinal);
        var after = Merge([.. files, changed])[talk.Reference];
        Assert.True(after.Activity);
        Assert.Equal(4, after.Fields["hours"].GetInt32());
    }

    [Fact]
    public void Nothing_kept_on_its_own_goes_in_the_activity_folder()
    {
        var w = new VaultWriter("dev1", new StepClock());
        foreach (var type in new[] { "subject", "group", "practitioner", DeviceNames.EntityType })
            Assert.Throws<ArgumentException>(() => w.CreateActivity(type, Fields(("name", "x"))));
    }

    [Fact]
    public void A_form_counts_activities_from_format_2_only_so_an_earlier_format_counts_the_same_in_any_engine()
    {
        var (files, _) = Build();
        var entities = Merge(files).Values;
        var form1 = new ReportDefinition("work", 1, "Work", "work", new ReportPeriod("date"), [new ReportDimension("kind", "kind", 1)])
        {
            Measures = [ReportMeasure.Records],
            Sums = ["hours"],
        };
        var form2 = form1 with { Version = 2, Activities = true };

        var one = ReportRunner.RunMonth(form1, 2026, 3, entities, Catalog);
        Assert.Single(one.Total);                                  // the subject's record alone
        Assert.Equal(1m, one.SumOf("hours", one.Total)!.Value.Sum);

        var two = ReportRunner.RunMonth(form2, 2026, 3, entities, Catalog);
        Assert.Equal(3, two.Total.Count);                          // and both activities
        Assert.Equal(6m, two.SumOf("hours", two.Total)!.Value.Sum);
        Assert.Equal(2, two.Cells.Single(c => c.Key[0] == "training").Count);
        Assert.Empty(two.PeopleOf(two.Cells.Single(c => c.Key[0] == "talk").Records)!); // an activity is about no one
    }

    [Fact]
    public void A_form_that_counts_activities_is_written_and_read_as_format_2()
    {
        var form = new ReportDefinition("work", 1, "Work", "work", new ReportPeriod("date"), [new ReportDimension("kind", "kind", 1)]) { Activities = true };
        var file = DefinitionWriter.Report(form);
        Assert.Contains("\"openquote.report/2\"", Encoding.UTF8.GetString(file.Content.Span), StringComparison.Ordinal);

        var read = Assert.Single(VaultReader.Read([file]).Reports);
        Assert.True(read.Activities);
        Assert.Equal(form, read);
        Assert.NotEqual(form with { Activities = false }, read);
        // Without it, the same shape is format 0, which an earlier engine reads.
        Assert.Contains("\"openquote.report/0\"", Encoding.UTF8.GetString(DefinitionWriter.Report(form with { Activities = false }).Content.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void Runs_of_a_form_counting_activities_and_of_one_that_does_not_are_not_compared()
    {
        var (files, _) = Build();
        var entities = Merge(files).Values;
        var form = new ReportDefinition("work", 1, "Work", "work", new ReportPeriod("date"), [new ReportDimension("kind", "kind", 1)]);
        var without = ReportRunner.RunMonth(form, 2026, 3, entities, Catalog);
        var with = ReportRunner.RunMonth(form with { Version = 2, Activities = true }, 2026, 3, entities, Catalog);
        var refused = Assert.Throws<ArgumentException>(() => ReportDiff.Compare(without, with, Catalog));
        Assert.Contains("activity records", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_export_form_lists_activities_from_format_2_only()
    {
        var (files, _) = Build();
        var entities = Merge(files).Values;
        string Export(int format) => $$"""
            {"format":"openquote.export/{{format}}","export":"work","version":{{format}},"label":"Work","rows":"work","period":{"field":"date"},
             "columns":[{"label":"Date","field":"date"},{"label":"Hours","field":"hours"}]}
            """;
        var content = VaultReader.Read([Json("exports/work/v1.json", Export(1)), Json("exports/work/v2.json", Export(2))]);
        Assert.Empty(content.Unreadable);
        var fields = new FieldCatalog([], []);
        var one = content.Exports.Single(e => e.Version == 1);
        var two = content.Exports.Single(e => e.Version == 2);
        Assert.False(one.Activities);
        Assert.True(two.Activities);
        Assert.Single(ExportRunner.Run(one, new(2026, 3, 1), new(2026, 3, 31), entities, Catalog, fields).Rows);
        Assert.Equal(3, ExportRunner.Run(two, new(2026, 3, 1), new(2026, 3, 31), entities, Catalog, fields).Rows.Count);
    }

    [Fact]
    public void A_pack_keeps_a_type_in_the_activity_folder_only_when_it_says_so()
    {
        const string Work = """
            {"format":"openquote.fields/0","pack":"work","type":"work","version":1,"under":["activity"],
             "fields":[{"name":"date","kind":"date","required":true}]}
            """;
        var content = VaultReader.Read([Json("fields/work/work/v1.json", Work)]);
        Assert.Empty(content.Unreadable);
        var catalog = new FieldCatalog(content.Fields, []);
        Assert.Equal(["activity"], catalog.KeptUnder("work"));
        Assert.Equal(["subject", "group"], catalog.KeptUnder("session")); // no pack says: never the activity folder
        Assert.Equal("activity", VaultFileKind.Of("activities/abc.dev1.json").Kind);
    }
}
