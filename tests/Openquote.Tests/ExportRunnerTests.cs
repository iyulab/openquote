using System.Text;
using System.Text.Json.Nodes;
using Openquote.Classification;
using Openquote.Exports;
using Openquote.Fields;
using Openquote.Records;
using Openquote.Vault;

namespace Openquote.Tests;

public class ExportRunnerTests
{
    private static readonly SchemeCatalog Catalog = new(
        [
            new Scheme("kind", 1, [new("a", "A", null, false), new("b", "B", null, false), new("b/x", "BX", "b", false)]),
            new Scheme("kind", 2, [new("a", "A2", null, false), new("p", "P", null, false), new("q", "Q", null, false)]),
        ],
        [new Crosswalk("kind", 1, 2, [("a", "a"), ("b", "p"), ("b", "q")])]);

    private static JsonObject Coded(int version, string code) => new() { ["scheme"] = "kind", ["version"] = version, ["code"] = code };

    private static Dictionary<string, JsonNode?> Fields(params (string Key, JsonNode? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private sealed record Vault(IReadOnlyList<Entity> Entities, string One, string Two, string Group);

    // Two subjects, a practitioner, one session each in March and February, and a group session.
    private static Vault Build()
    {
        var w = new VaultWriter("dev1", new StepClock()); // ids in creation order: one before two
        var files = new List<VaultFile>();
        string Add(VaultFile f)
        {
            files.Add(f);
            return VaultReader.Read([f]).Changes[0].Entity.Id;
        }
        var who = Add(w.CreatePractitioner(Fields(("name", "counsellor"))));
        var one = Add(w.CreateSubject(Fields(("name", "one"), ("grade", "2"))));
        var two = Add(w.CreateSubject(Fields(("name", "two"), ("grade", "3"))));
        var group = Add(w.CreateGroup(Fields(("name", "friends"))));
        Add(w.CreateInSubject(one, "session", Fields(("date", "2026-03-05"), ("kind", Coded(1, "b/x")), ("practitioner", who), ("title", "first"))));
        Add(w.CreateInSubject(two, "session", Fields(("date", "2026-02-20"), ("kind", Coded(1, "b")), ("practitioner", who))));
        Add(w.CreateInGroup(group, "session", Fields(("date", "2026-03-02"), ("kind", Coded(1, "a")), ("attendees", new JsonArray(one, two)))));
        return new Vault(EntityMerger.Merge(VaultReader.Read(files).Changes).Values.ToList(), one, two, group);
    }

    private static ExportDefinition Form(params ExportColumn[] columns) =>
        new("list", 1, "List", "session", "date", columns);

    [Fact]
    public void Lists_the_records_of_the_period_in_date_order_with_every_column()
    {
        var v = Build();
        var form = Form(
            new FieldColumn("date", "date"),
            new YearColumn("year", "date", 3),
            new CodedColumn("top", "kind", "kind", 1, Top: true),
            new CodedColumn("item", "kind", "kind", 1, Top: false),
            new PeopleCountColumn("people"),
            new PersonColumn("grade", "grade", All: false),
            new PersonColumn("names", "name", All: true),
            new ReferenceColumn("by", "practitioner", "name"),
            new FieldColumn("title", "title"));

        var table = ExportRunner.Run(form, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog);

        Assert.Collection(table.Rows,
            r => Assert.Equal(["2026-02-20", "2025", "B", "B", "1", "3", "two", "counsellor", ""], r.Cells),
            r => Assert.Equal(["2026-03-02", "2026", "A", "A", "2", "", "one, two", "", ""], r.Cells), // a group: one row, two people, no single grade
            r => Assert.Equal(["2026-03-05", "2026", "B", "BX", "1", "2", "one", "counsellor", "first"], r.Cells));
        Assert.Empty(table.Pending);
        Assert.Empty(table.Unmapped);
    }

    [Fact]
    public void A_value_that_waits_for_a_person_leaves_its_cell_empty_and_is_listed()
    {
        var v = Build();
        var form = Form(new FieldColumn("date", "date"), new CodedColumn("kind", "kind", "kind", 2, Top: false));

        var table = ExportRunner.Run(form, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog);

        Assert.Equal(["", "A2", ""], table.Rows.Select(r => r.Cells[1]));
        Assert.Single(table.Pending);   // b → p or q
        Assert.Single(table.Unmapped);  // b/x has no link
    }

    [Fact]
    public void Only_the_period_is_listed()
    {
        var v = Build();
        var table = ExportRunner.Run(Form(new FieldColumn("date", "date")), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog);

        Assert.Equal(["2026-03-02", "2026-03-05"], table.Rows.Select(r => r.Cells[0]));
    }

    [Fact]
    public void An_export_form_reads_from_its_file()
    {
        const string json = """
            {"format":"openquote.export/0","export":"list","version":2,"label":"List","rows":"session","period":{"field":"date"},
             "columns":[{"label":"d","field":"date"},{"label":"y","year":"date","startMonth":3},{"label":"k","field":"kind","scheme":"kind","version":1,"part":"top"},
                        {"label":"n","people":"count"},{"label":"g","person":"grade"},{"label":"all","person":"name","all":true},{"label":"by","field":"practitioner","ref":"name"}]}
            """;
        var content = VaultReader.Read([new VaultFile("exports/list/v2.json", Encoding.UTF8.GetBytes(json))]);

        Assert.Empty(content.Unreadable);
        var form = Assert.Single(content.Exports);
        Assert.Equal(("list", 2, "session", "date"), (form.Name, form.Version, form.Rows, form.PeriodField));
        Assert.Equal(
            [typeof(FieldColumn), typeof(YearColumn), typeof(CodedColumn), typeof(PeopleCountColumn), typeof(PersonColumn), typeof(PersonColumn), typeof(ReferenceColumn)],
            form.Columns.Select(c => c.GetType()));
        Assert.True(((CodedColumn)form.Columns[2]).Top);
        Assert.True(((PersonColumn)form.Columns[5]).All);
    }

    [Theory]
    [InlineData("""{"label":"x"}""")]
    [InlineData("""{"label":"x","people":"sum"}""")]
    [InlineData("""{"label":"x","year":"date","startMonth":13}""")]
    [InlineData("""{"label":"x","field":"kind","scheme":"kind","version":1,"part":"middle"}""")]
    public void A_column_that_does_not_say_where_its_cells_come_from_is_unreadable(string column)
    {
        var json = $$"""{"format":"openquote.export/0","export":"list","version":1,"label":"L","rows":"session","period":{"field":"date"},"columns":[{{column}}]}""";
        var content = VaultReader.Read([new VaultFile("exports/list/v1.json", Encoding.UTF8.GetBytes(json))]);

        Assert.Empty(content.Exports);
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(content.Unreadable).Reason);
    }

    private static FieldCatalog Narrative(params (string Type, string Field)[] fields) =>
        new(fields.GroupBy(f => f.Type).Select(g => new FieldSet("care", g.Key, 1,
                [.. g.Select(f => new FieldDefinition(f.Field, FieldKind.Text, null, null, false, false, FieldTier.Narrative, null, null, "care"))], [])),
            []);

    [Fact]
    public void Written_content_leaves_the_record_by_no_route_and_the_withheld_columns_are_named()
    {
        var v = Build();
        var form = Form(
            new FieldColumn("date", "date"),
            new FieldColumn("title", "title"),              // the record's own written field
            new PersonColumn("names", "name", All: true),   // the subject's written field
            new ReferenceColumn("by", "practitioner", "name"));

        var table = ExportRunner.Run(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog,
            Narrative(("session", "title"), ("subject", "name"), ("practitioner", "name")));

        Assert.All(table.Rows, r => Assert.Equal(["", "", ""], r.Cells.Skip(1)));
        Assert.Equal(["title", "names", "by"], table.Withheld);
    }

    [Fact]
    public void Without_field_definitions_nothing_is_withheld()
    {
        var v = Build();
        var table = ExportRunner.Run(Form(new FieldColumn("title", "title")), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog);

        Assert.Empty(table.Withheld);
        Assert.Contains(table.Rows, r => r.Cells[0] == "first");
    }
}
