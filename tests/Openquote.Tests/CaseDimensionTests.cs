using System.Text;
using System.Text.Json.Nodes;
using Openquote.Classification;
using Openquote.Fields;
using Openquote.Packs;
using Openquote.Records;
using Openquote.Reports;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class CaseDimensionTests
{
    // An intake opens a case and says where the person came from; a closing closes it and says how it ended.
    private static readonly FieldCatalog Fields = Catalog(("intake", """ "role": "opens", """), ("closing", """ "role": "closes", """), ("session", ""));

    private static readonly SchemeCatalog Schemes = new(
        [
            new Scheme("source", 1, [new("school", "School", null, false), new("self", "Self", null, false)]),
            new Scheme("ending", 1, [new("planned", "Planned", null, false), new("dropout", "Dropout", null, false)]),
        ],
        []);

    private static FieldCatalog Catalog(params (string Type, string Meta)[] types)
    {
        var content = VaultReader.Read(types.Select(t => File($"fields/care/{t.Type}/v1.json",
            $$"""{ "format": "openquote.fields/1", "pack": "care", "type": "{{t.Type}}", "version": 1, {{t.Meta}} "fields": [ { "name": "date", "kind": "date" } ] }""")));
        Assert.Empty(content.Unreadable);
        return new FieldCatalog(content.Fields, [new PackManifest("care", 1, "care", new Dictionary<string, int>(), [])]);
    }

    private int _n;
    private readonly List<VaultFile> _files = [];

    private static JsonObject Code(string scheme, string code) => new() { ["scheme"] = scheme, ["version"] = 1, ["code"] = code };

    private void Add(string type, string subject, string day, string? field = null, JsonNode? value = null)
    {
        var n = ++_n;
        var fields = new JsonObject { ["date"] = day };
        if (field is not null) fields[field] = value;
        _files.Add(File(Json(n, $"{type}-{n}", "create", entityType: type, fields: fields), $"subjects/{subject}"));
    }

    private IReadOnlyCollection<Entity> Entities()
    {
        var content = VaultReader.Read(_files);
        Assert.Empty(content.Unreadable);
        return [.. EntityMerger.Merge(content.Changes).Values];
    }

    // Closings by how they ended and by where the person came from, as the intake of the same case says.
    private static readonly ReportDefinition BySource = new("closings", 1, "Closings", "closing", new ReportPeriod("date", PeriodUnit.Year),
        [new ReportDimension("ending", "ending", 1), new ReportDimension("source", "source", 1, OfKind: "intake")]);

    [Fact]
    public void A_closing_is_placed_by_the_intake_of_the_case_it_closed()
    {
        Add("intake", "a", "2026-03-02", "source", Code("source", "school"));
        Add("closing", "a", "2026-04-20", "ending", Code("ending", "dropout"));
        Add("intake", "a", "2026-05-01", "source", Code("source", "self"));      // a second case: its own source
        Add("closing", "a", "2026-06-10", "ending", Code("ending", "planned"));
        Add("intake", "b", "2026-03-05", "source", Code("source", "school"));
        Add("closing", "b", "2026-04-02", "ending", Code("ending", "planned"));

        var run = ReportRunner.RunContaining(BySource, new DateOnly(2026, 6, 1), Entities(), Schemes, Fields);

        int Count(string ending, string source) => run.Cells.SingleOrDefault(c => c.Key[0] == ending && c.Key[1] == source)?.Records.Count ?? 0;
        Assert.Equal(1, Count("dropout", "school"));
        Assert.Equal(1, Count("planned", "self"));
        Assert.Equal(1, Count("planned", "school"));
        Assert.Equal(3, run.Total.Count);
    }

    [Fact]
    public void A_case_without_that_kind_of_record_or_with_two_that_differ_has_no_single_value()
    {
        Add("session", "a", "2026-03-02");                                       // a case begun without an intake
        Add("closing", "a", "2026-04-20", "ending", Code("ending", "planned"));

        var run = ReportRunner.RunContaining(BySource, new DateOnly(2026, 6, 1), Entities(), Schemes, Fields);

        var cell = Assert.Single(run.Cells);
        Assert.Equal(["planned", null], cell.Key);

        // Sessions of one case placed by a session field read across the case: two values that differ, no single one.
        var bySessionTopic = new ReportDefinition("closings-by-topic", 1, "C", "closing", new ReportPeriod("date", PeriodUnit.Year),
            [new ReportDimension("topic", OfKind: "session")]);
        Add("intake", "b", "2026-03-05");
        Add("session", "b", "2026-03-06", "topic", "sleep");
        Add("session", "b", "2026-03-13", "topic", "family");
        Add("closing", "b", "2026-04-02");
        Add("intake", "c", "2026-03-05");
        Add("session", "c", "2026-03-06", "topic", "sleep");
        Add("session", "c", "2026-03-13", "topic", "sleep");
        Add("closing", "c", "2026-04-02");
        var topics = ReportRunner.RunContaining(bySessionTopic, new DateOnly(2026, 6, 1), Entities(), Schemes, Fields);
        Assert.Single(topics.Cells.Single(c => c.Key[0] == "sleep").Records);                // c: both sessions agree
        Assert.Equal(2, topics.Cells.Single(c => c.Key[0] is null).Records.Count);        // a: no session field; b: they differ
    }

    [Fact]
    public void A_form_reading_another_record_of_the_case_needs_the_field_catalog()
    {
        Add("intake", "a", "2026-03-02", "source", Code("source", "school"));
        Add("closing", "a", "2026-04-20", "ending", Code("ending", "dropout"));
        Assert.Throws<ArgumentException>(() => ReportRunner.RunContaining(BySource, new DateOnly(2026, 6, 1), Entities(), Schemes));
    }

    [Fact]
    public void The_form_is_written_and_read_back_with_the_kind_it_reads()
    {
        var file = DefinitionWriter.Report(BySource);
        Assert.Contains("\"of\": \"intake\"", Encoding.UTF8.GetString(file.Content.Span), StringComparison.Ordinal);
        var read = Assert.Single(VaultReader.Read([file]).Reports);
        Assert.Equal(BySource, read);
        Assert.Equal("intake", read.Dimensions[1].Of);

        // "of" names a subject or a kind of record of the case — not a folder kept on its own.
        foreach (var of in new[] { "group", "practitioner", "device" })
        {
            var json = Encoding.UTF8.GetString(file.Content.Span).Replace("\"of\": \"intake\"", $"\"of\": \"{of}\"", StringComparison.Ordinal);
            Assert.Single(VaultReader.Read([File(file.Path, json)]).Unreadable);
        }
        Assert.NotNull(new ReportDefinition("x", 1, "X", "closing", new ReportPeriod("date"),
            [new ReportDimension("source", "source", 1, All: true, OfKind: "intake")]).Problem()); // every value: a field of the record only
    }
}
