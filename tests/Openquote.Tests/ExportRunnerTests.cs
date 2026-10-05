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
            new CodedColumn("top", "kind", "kind", 1, Level: 1),
            new CodedColumn("item", "kind", "kind", 1, Level: null),
            new PeopleCountColumn("people"),
            new PersonColumn("grade", "grade", All: false),
            new PersonColumn("names", "name", All: true),
            new ReferenceColumn("by", "practitioner", "name"),
            new FieldColumn("title", "title"));

        var table = ExportRunner.Run(form, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog, FieldCatalog.Empty);

        Assert.Collection(table.Rows,
            r => Assert.Equal(["2026-02-20", "2025", "B", "B", "1", "3", "two", "counsellor", ""], r.Cells),
            r => Assert.Equal(["2026-03-02", "2026", "A", "A", "2", "", "one, two", "", ""], r.Cells), // a group: one row, two people, no single grade
            r => Assert.Equal(["2026-03-05", "2026", "B", "BX", "1", "2", "one", "counsellor", "first"], r.Cells));
        Assert.Empty(table.Pending);
        Assert.Empty(table.Unmapped);
    }

    [Fact]
    public void Format_1_columns_fill_the_cells_an_outside_form_asks_for()
    {
        var w = new VaultWriter("dev1", new StepClock());
        var files = new List<VaultFile>();
        string Add(VaultFile f)
        {
            files.Add(f);
            return VaultReader.Read([f]).Changes[0].Entity.Id;
        }
        var one = Add(w.CreateSubject(Fields(("name", "one"), ("grade", "2"))));
        var two = Add(w.CreateSubject(Fields(("name", "two"), ("grade", "3"))));
        var three = Add(w.CreateSubject(Fields(("name", "three"), ("grade", "3"))));
        var mixed = Add(w.CreateGroup(Fields(("name", "mixed"))));
        var alike = Add(w.CreateGroup(Fields(("name", "alike"))));
        Add(w.CreateInSubject(one, "session", Fields(("date", "2026-03-05"), ("kind", Coded(1, "b/x")), ("minutes", 70))));
        Add(w.CreateInGroup(mixed, "session", Fields(("date", "2026-03-06"), ("kind", Coded(1, "a")), ("minutes", 50), ("attendees", new JsonArray(one, two)))));
        Add(w.CreateInGroup(alike, "session", Fields(("date", "2026-03-07"), ("kind", Coded(1, "b")), ("minutes", 12.5), ("attendees", new JsonArray(two, three)))));
        var entities = EntityMerger.Merge(VaultReader.Read(files).Changes).Values.ToList();
        var form = Form(
            new ValueColumn("fixed", "Fixed"),
            new CodedColumn("top", "kind", "kind", 1, Level: 1),
            new CodedColumn("middle", "kind", "kind", 1, Level: 2),
            new CodedColumn("deeper", "kind", "kind", 1, Level: 3),
            new DateColumn("day", "date", DateStyle.Basic),
            new DivisionColumn("hours", "minutes", 60, Remainder: false),
            new DivisionColumn("minutes", "minutes", 60, Remainder: true),
            new PersonColumn("grade", "grade", All: false) { Mixed = "mixed" });

        var table = ExportRunner.Run(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), entities, Catalog, FieldCatalog.Empty);

        Assert.Collection(table.Rows,
            r => Assert.Equal(["Fixed", "B", "BX", "BX", "20260305", "1", "10", "2"], r.Cells),
            r => Assert.Equal(["Fixed", "A", "A", "A", "20260306", "0", "50", "mixed"], r.Cells), // grades 2 and 3
            r => Assert.Equal(["Fixed", "B", "B", "B", "20260307", "", "", "3"], r.Cells));      // not a whole number; both in grade 3
    }

    [Theory]
    [InlineData(-61, "-2", "59")]
    [InlineData(0, "0", "0")]
    [InlineData(120, "2", "0")]
    public void A_division_rounds_down_and_leaves_a_remainder_that_is_never_negative(int value, string quotient, string remainder)
    {
        var w = new VaultWriter("dev1", new StepClock());
        var subject = w.CreateSubject(Fields(("name", "one")));
        var id = VaultReader.Read([subject]).Changes[0].Entity.Id;
        var session = w.CreateInSubject(id, "session", Fields(("date", "2026-03-05"), ("n", value)));
        var entities = EntityMerger.Merge(VaultReader.Read([subject, session]).Changes).Values.ToList();
        var form = Form(new DivisionColumn("q", "n", 60, Remainder: false), new DivisionColumn("r", "n", 60, Remainder: true));

        var row = Assert.Single(ExportRunner.Run(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), entities, Catalog, FieldCatalog.Empty).Rows);

        Assert.Equal([quotient, remainder], row.Cells);
    }

    [Fact]
    public void A_value_that_waits_for_a_person_leaves_its_cell_empty_and_is_listed()
    {
        var v = Build();
        var form = Form(new FieldColumn("date", "date"), new CodedColumn("kind", "kind", "kind", 2, Level: null));

        var table = ExportRunner.Run(form, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog, FieldCatalog.Empty);

        Assert.Equal(["", "A2", ""], table.Rows.Select(r => r.Cells[1]));
        Assert.Single(table.Pending);   // b → p or q
        Assert.Single(table.Unmapped);  // b/x has no link
    }

    [Fact]
    public void A_coded_column_carries_values_as_a_report_does()
    {
        // A value of a scheme extending `kind` v1 is counted as its anchor; several values by the primary one.
        var catalog = new SchemeCatalog(
            [
                .. new[] { Catalog.Find("kind", 1)!, Catalog.Find("kind", 2)! },
                new Scheme("kind.local", 1, [new("a-call", "A by phone", null, false) { Anchor = "a" }]) { Extends = new SchemeVersion("kind", 1) },
            ],
            [new Crosswalk("kind", 1, 2, [("a", "a"), ("b", "p"), ("b", "q")])]);
        var w = new VaultWriter("dev1", new StepClock());
        var subject = w.CreateSubject(Fields(("name", "one")));
        var one = VaultReader.Read([subject]).Changes[0].Entity.Id;
        JsonObject Local(string code) => new() { ["scheme"] = "kind.local", ["version"] = 1, ["code"] = code };
        JsonObject Primary(JsonObject o) { o["primary"] = true; return o; }
        var files = new[]
        {
            subject,
            w.CreateInSubject(one, "session", Fields(("date", "2026-03-02"), ("kind", Local("a-call")))),
            w.CreateInSubject(one, "session", Fields(("date", "2026-03-03"), ("kind", new JsonArray(Coded(1, "b"), Primary(Coded(1, "a")))))),
            w.CreateInSubject(one, "session", Fields(("date", "2026-03-04"), ("kind", new JsonArray(Coded(1, "b"), Coded(1, "a"))))),
        };
        var entities = EntityMerger.Merge(VaultReader.Read(files).Changes).Values.ToList();
        var form = Form(new FieldColumn("date", "date"), new CodedColumn("kind", "kind", "kind", 2, Level: null));

        var table = ExportRunner.Run(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), entities, catalog, FieldCatalog.Empty);

        Assert.Equal(["A2", "A2", ""], table.Rows.Select(r => r.Cells[1]));
        Assert.Equal([table.Rows[2].Record], table.Pending); // two values, none primary: a person says which comes first
        Assert.Empty(table.Unmapped);
    }

    [Fact]
    public void A_column_over_every_value_lists_each_label_once_the_primary_first()
    {
        var w = new VaultWriter("dev1", new StepClock());
        var subject = w.CreateSubject(Fields(("name", "one")));
        var one = VaultReader.Read([subject]).Changes[0].Entity.Id;
        JsonObject Primary(JsonObject o) { o["primary"] = true; return o; }
        var files = new[]
        {
            subject,
            w.CreateInSubject(one, "session", Fields(("date", "2026-03-02"), ("kind", Coded(1, "a")))),
            w.CreateInSubject(one, "session", Fields(("date", "2026-03-03"), ("kind", new JsonArray(Coded(1, "b/x"), Primary(Coded(1, "a")))))),
            // No primary: every value still shows, as none needs choosing to be listed.
            w.CreateInSubject(one, "session", Fields(("date", "2026-03-04"), ("kind", new JsonArray(Coded(1, "a"), Coded(1, "b/x"))))),
            w.CreateInSubject(one, "session", Fields(("date", "2026-03-05"))),
        };
        var entities = EntityMerger.Merge(VaultReader.Read(files).Changes).Values.ToList();
        var form = Form(new FieldColumn("date", "date"), new CodedColumn("kind", "kind", "kind", 1, Level: null) { All = true },
            new CodedColumn("top", "kind", "kind", 1, Level: 1) { All = true });

        var table = ExportRunner.Run(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), entities, Catalog, FieldCatalog.Empty);

        Assert.Equal(["A", "A, BX", "A, BX", ""], table.Rows.Select(r => r.Cells[1]));
        Assert.Equal(["A", "A, B", "A, B", ""], table.Rows.Select(r => r.Cells[2]));
        Assert.Empty(table.Pending.Concat(table.Unmapped));

        // Carried to version 2, `b/x` has no code there: that value is left out and the record listed, the other still shows.
        var later = ExportRunner.Run(Form(new FieldColumn("date", "date"), new CodedColumn("kind", "kind", "kind", 2, Level: null) { All = true }),
            new DateOnly(2026, 3, 3), new DateOnly(2026, 3, 3), entities, Catalog, FieldCatalog.Empty);
        Assert.Equal("A2", later.Rows.Single().Cells[1]);
        Assert.Equal([later.Rows.Single().Record], later.Unmapped);
    }

    [Fact]
    public void A_cell_over_concurrent_values_stays_empty_and_its_record_is_listed()
    {
        var w = new VaultWriter("dev1", new StepClock());
        var subject = w.CreateSubject(Fields(("name", "one")));
        var one = VaultReader.Read([subject]).Changes[0].Entity.Id;
        var titled = w.CreateInSubject(one, "session", Fields(("date", "2026-03-02"), ("title", "first")));
        var dated = w.CreateInSubject(one, "session", Fields(("date", "2026-03-20"), ("title", "second")));
        Entity Session(VaultFile f) => EntityMerger.Merge(VaultReader.Read([subject, titled, dated]).Changes).Values.Single(e => e.Changes[0].Path == f.Path);
        var files = new[]
        {
            subject, titled, dated,
            w.Update(Session(titled), Fields(("title", "one way"))),
            w.Update(Session(titled), Fields(("title", "another"))),     // the same base: concurrent
            w.Update(Session(dated), Fields(("date", "2026-03-05"))),
            w.Update(Session(dated), Fields(("date", "2026-04-02"))),     // a disputed date, one of them in March
        };
        var entities = EntityMerger.Merge(VaultReader.Read(files).Changes).Values.ToList();
        var form = Form(new FieldColumn("date", "date"), new FieldColumn("title", "title"));

        var table = ExportRunner.Run(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), entities, Catalog, FieldCatalog.Empty);

        Assert.Collection(table.Rows,
            r => Assert.Equal(["2026-03-02", ""], r.Cells),
            r => Assert.Equal(["", "second"], r.Cells)); // listed at the March date of the two
        Assert.Equal(table.Rows.Select(r => r.Record).Order(StringComparer.Ordinal), table.Conflicted);
        Assert.Empty(ExportRunner.Run(form, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), entities, Catalog, FieldCatalog.Empty).Rows);
    }

    [Fact]
    public void Only_the_period_is_listed()
    {
        var v = Build();
        var table = ExportRunner.Run(Form(new FieldColumn("date", "date")), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog, FieldCatalog.Empty);

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
        Assert.Equal(1, ((CodedColumn)form.Columns[2]).Level);
        Assert.True(((PersonColumn)form.Columns[5]).All);
    }

    [Fact]
    public void A_format_1_export_form_reads_its_columns_from_its_file()
    {
        const string json = """
            {"format":"openquote.export/1","export":"list","version":1,"label":"List","rows":"session","period":{"field":"date"},
             "columns":[{"label":"f","value":""},{"label":"m","field":"kind","scheme":"kind","version":1,"level":2},
                        {"label":"t","field":"kind","scheme":"kind","version":1,"part":"top"},
                        {"label":"d","field":"date","date":"basic"},{"label":"e","field":"date","date":"extended"},
                        {"label":"h","field":"minutes","quotient":60},{"label":"r","field":"minutes","remainder":60},
                        {"label":"g","person":"gender","mixed":"mixed"},{"label":"n","person":"name","all":true}]}
            """;
        var content = VaultReader.Read([new VaultFile("exports/list/v1.json", Encoding.UTF8.GetBytes(json))]);

        Assert.Empty(content.Unreadable);
        Assert.Equal(
            [
                new ValueColumn("f", ""), new CodedColumn("m", "kind", "kind", 1, 2), new CodedColumn("t", "kind", "kind", 1, 1),
                new DateColumn("d", "date", DateStyle.Basic), new DateColumn("e", "date", DateStyle.Extended),
                new DivisionColumn("h", "minutes", 60, false), new DivisionColumn("r", "minutes", 60, true),
                new PersonColumn("g", "gender", false) { Mixed = "mixed" }, new PersonColumn("n", "name", true),
            ],
            Assert.Single(content.Exports).Columns);
    }

    [Theory]
    [InlineData("""{"label":"x","value":"Fixed"}""")]
    [InlineData("""{"label":"x","field":"kind","scheme":"kind","version":1,"level":2}""")]
    [InlineData("""{"label":"x","field":"date","date":"basic"}""")]
    [InlineData("""{"label":"x","field":"minutes","quotient":60}""")]
    [InlineData("""{"label":"x","person":"gender","mixed":"mixed"}""")]
    public void A_format_0_form_with_a_format_1_column_is_unreadable_rather_than_read_without_it(string column)
    {
        var json = $$"""{"format":"openquote.export/0","export":"list","version":1,"label":"L","rows":"session","period":{"field":"date"},"columns":[{{column}}]}""";
        var unreadable = Assert.Single(VaultReader.Read([new VaultFile("exports/list/v1.json", Encoding.UTF8.GetBytes(json))]).Unreadable);

        Assert.Equal(UnreadableReason.Invalid, unreadable.Reason);
        Assert.Contains("needs format 1", unreadable.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"label":"x","value":3}""")]
    [InlineData("""{"label":"x","field":"kind","scheme":"kind","version":1,"level":0}""")]
    [InlineData("""{"label":"x","field":"kind","scheme":"kind","version":1,"level":2,"part":"top"}""")]
    [InlineData("""{"label":"x","field":"date","date":"short"}""")]
    [InlineData("""{"label":"x","field":"minutes","quotient":0}""")]
    [InlineData("""{"label":"x","field":"minutes","quotient":60,"remainder":60}""")]
    [InlineData("""{"label":"x","person":"gender","all":true,"mixed":"mixed"}""")]
    [InlineData("""{"label":"x","field":"title","level":2}""")]
    [InlineData("""{"label":"x","field":"kind","scheme":"kind","version":1,"date":"basic"}""")]
    public void A_format_1_column_whose_keys_do_not_fit_its_source_is_unreadable(string column)
    {
        var json = $$"""{"format":"openquote.export/1","export":"list","version":1,"label":"L","rows":"session","period":{"field":"date"},"columns":[{{column}}]}""";
        var content = VaultReader.Read([new VaultFile("exports/list/v1.json", Encoding.UTF8.GetBytes(json))]);

        Assert.Empty(content.Exports);
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(content.Unreadable).Reason);
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
        var table = ExportRunner.Run(Form(new FieldColumn("title", "title")), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog, FieldCatalog.Empty);

        Assert.Empty(table.Withheld);
        Assert.Contains(table.Rows, r => r.Cells[0] == "first");
    }

    [Fact]
    public void A_date_or_coded_value_declared_as_written_content_does_not_leak_through_a_year_or_a_code()
    {
        var v = Build();
        var form = Form(
            new YearColumn("year", "date", 3),                       // a narrative date would show its year
            new CodedColumn("item", "kind", "kind", 1, Level: null)); // a narrative coded field would show its label

        var table = ExportRunner.Run(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog,
            Narrative(("session", "date"), ("session", "kind")));

        Assert.NotEmpty(table.Rows);
        Assert.All(table.Rows, r => Assert.Equal(["", ""], r.Cells));
        Assert.Equal(["year", "item"], table.Withheld);
    }

    [Fact]
    public void A_column_over_a_field_that_is_not_written_content_keeps_its_value_when_a_catalog_is_given()
    {
        var v = Build();
        var form = Form(
            new FieldColumn("date", "date"),
            new YearColumn("year", "date", 3),
            new CodedColumn("item", "kind", "kind", 1, Level: null),
            new FieldColumn("title", "title"));

        var table = ExportRunner.Run(form, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog, Narrative(("session", "title")));

        Assert.Equal(["2026-03-02", "2026-03-05"], table.Rows.Select(r => r.Cells[0]));
        Assert.All(table.Rows, r => Assert.NotEmpty(r.Cells[1]));
        Assert.All(table.Rows, r => Assert.NotEmpty(r.Cells[2]));
        Assert.Equal(["title"], table.Withheld);
    }

    [Fact]
    public void The_field_definitions_are_required_so_the_guard_cannot_be_forgotten()
    {
        var v = Build();
        Assert.Throws<ArgumentNullException>(() =>
            ExportRunner.Run(Form(new FieldColumn("date", "date")), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), v.Entities, Catalog, null!));
    }
}
