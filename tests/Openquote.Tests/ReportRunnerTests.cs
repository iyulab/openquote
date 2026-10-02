using System.Text.Json;
using System.Text.Json.Nodes;
using Openquote.Classification;
using Openquote.Records;
using Openquote.Reports;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class ReportRunnerTests
{
    private static readonly SchemeCatalog Catalog = new(
        [
            new Scheme("kind", 1, [new("a", "A", null, false), new("b", "B", null, false), new("c", "C", null, false), new("h", "H", null, false)]),
            new Scheme("kind", 2, [new("x", "X", null, false), new("p", "P", null, false), new("q", "Q", null, false)]),
        ],
        [new Crosswalk("kind", 1, 2, [("a", "x"), ("b", "x"), ("c", "p"), ("c", "q")])]);

    private static ReportDefinition Form(int version, string? columns = "owner") =>
        new("monthly", version, "Monthly", "item", "day", "kind", "kind", version, columns);

    private static JsonObject Coded(int version, string code) => new() { ["scheme"] = "kind", ["version"] = version, ["code"] = code };

    private static int _n;

    private static List<JsonObject> Item(string id, string day, string code, string owner = "o1", int version = 1)
    {
        var n = Interlocked.Increment(ref _n);
        return [Json(n, id, "create", entityType: "item", fields: new JsonObject { ["day"] = day, ["kind"] = Coded(version, code), ["owner"] = owner })];
    }

    private static IEnumerable<Entity> Entities(IEnumerable<JsonObject> changes)
    {
        var content = VaultReader.Read(changes.Select(c => File(c)));
        Assert.Empty(content.Unreadable);
        return EntityMerger.Merge(content.Changes).Values;
    }

    [Fact]
    public void Counts_each_record_once_in_its_month_row_and_column()
    {
        var items = Entities([.. Item("i1", "2026-03-01", "a"), .. Item("i2", "2026-03-31", "a", "o2"),
            .. Item("i3", "2026-03-15", "a"), .. Item("i4", "2026-02-28", "a"), .. Item("i5", "2026-04-01", "b")]);

        var run = ReportRunner.RunMonth(Form(1), 2026, 3, items, Catalog);

        Assert.Equal(new DateOnly(2026, 3, 1), run.From);
        Assert.Equal(new DateOnly(2026, 3, 31), run.To);
        Assert.Collection(run.Cells,
            c => { Assert.Equal(("a", "o1"), (c.Row, c.Column)); Assert.Equal(["i1", "i3"], c.Records); },
            c => { Assert.Equal(("a", "o2"), (c.Row, c.Column)); Assert.Equal(["i2"], c.Records); });
        Assert.Equal(["i1", "i2", "i3"], run.Total);
        Assert.Empty(run.Crosswalks);
    }

    [Fact]
    public void A_revision_assigns_merges_and_holds_back_without_estimating()
    {
        var items = Entities([.. Item("i1", "2026-03-02", "a"), .. Item("i2", "2026-03-03", "b"),
            .. Item("i3", "2026-03-04", "c"), .. Item("i4", "2026-03-05", "h")]);

        var run = ReportRunner.RunMonth(Form(2), 2026, 3, items, Catalog);

        var cell = Assert.Single(run.Cells);
        Assert.Equal("x", cell.Row);
        Assert.Equal(["i1", "i2"], cell.Records);
        Assert.Equal(["i3"], run.Pending);
        Assert.Equal(["i4"], run.Unmapped);
        Assert.Equal(4, run.Total.Count);
        Assert.Equal(["1-2"], run.Crosswalks);
    }

    [Fact]
    public void A_reclassified_record_counts_in_the_new_version_and_keeps_its_old_value_for_the_old_one()
    {
        var create = Json(800, "i1", "create", entityType: "item",
            fields: new JsonObject { ["day"] = "2026-03-02", ["kind"] = Coded(1, "c"), ["owner"] = "o1" });
        var reclassify = Json(801, "i1", "reclassify", [800], new JsonObject { ["kind"] = Coded(2, "q") }, entityType: "item");
        var items = Entities([create, reclassify]);

        var v2 = ReportRunner.RunMonth(Form(2), 2026, 3, items, Catalog);
        var v1 = ReportRunner.RunMonth(Form(1), 2026, 3, items, Catalog);

        Assert.Equal("q", Assert.Single(v2.Cells).Row);
        Assert.Empty(v2.Pending);
        Assert.Empty(v2.Crosswalks);
        Assert.Equal("c", Assert.Single(v1.Cells).Row);
    }

    [Fact]
    public void A_month_with_no_records_still_has_a_run()
    {
        var run = ReportRunner.RunMonth(Form(1), 2026, 2, Entities(Item("i1", "2026-03-02", "a")), Catalog);

        Assert.Empty(run.Cells);
        Assert.Empty(run.Total);
    }

    [Fact]
    public void Destroyed_records_other_entity_types_and_undated_records_are_not_counted()
    {
        var doomed = Item("i1", "2026-03-02", "a")[0];
        var destroy = Json(901, "i1", "destroy", entityType: "item");
        var other = Json(902, "o1", "create", entityType: "other", fields: new JsonObject { ["day"] = "2026-03-02", ["kind"] = Coded(1, "a") });
        var undated = Json(903, "i9", "create", entityType: "item", fields: new JsonObject { ["day"] = "March", ["kind"] = Coded(1, "a") });

        var run = ReportRunner.RunMonth(Form(1), 2026, 3, Entities([doomed, destroy, other, undated]), Catalog);

        Assert.Empty(run.Total);
    }

    [Fact]
    public void A_record_without_a_value_for_the_row_is_blank_apart_from_unmapped_not_dropped()
    {
        var empty = Json(904, "i1", "create", entityType: "item", fields: new JsonObject { ["day"] = "2026-03-02", ["kind"] = null });
        var absent = Json(905, "i2", "create", entityType: "item", fields: new JsonObject { ["day"] = "2026-03-03" });

        var run = ReportRunner.RunMonth(Form(1), 2026, 3, Entities([empty, absent]), Catalog);

        Assert.Empty(run.Unmapped);
        Assert.Equal(["i1", "i2"], run.Blank);
        Assert.Equal(["i1", "i2"], run.Total);
    }

    // An item created with `created`, then edited twice by changes that did not see each other.
    private static List<JsonObject> Concurrent(string id, int n, JsonObject created, JsonObject one, JsonObject other) =>
    [
        Json(n, id, "create", entityType: "item", fields: created),
        Json(n + 1, id, "update", [n], one, entityType: "item"),
        Json(n + 2, id, "update", [n], other, entityType: "item"),
    ];

    private static JsonObject Fields(string day, string code, string owner = "o1") =>
        new() { ["day"] = day, ["kind"] = Coded(1, code), ["owner"] = owner };

    [Fact]
    public void A_record_whose_row_holds_concurrent_values_is_conflicted_and_in_no_cell()
    {
        var changes = Concurrent("i1", 910, Fields("2026-03-02", "a"),
            new JsonObject { ["kind"] = Coded(1, "b") }, new JsonObject { ["kind"] = Coded(1, "c") });

        var run = ReportRunner.RunMonth(Form(1), 2026, 3, Entities([.. changes, .. Item("i2", "2026-03-03", "a")]), Catalog);

        Assert.Equal(["i1"], run.Conflicted);
        Assert.Equal(["i2"], Assert.Single(run.Cells).Records);
        Assert.Equal(["i1", "i2"], run.Total);
    }

    [Fact]
    public void Concurrent_columns_conflict_the_record_too_while_a_field_the_form_does_not_place_by_does_not()
    {
        var owner = Concurrent("i1", 920, Fields("2026-03-02", "a"),
            new JsonObject { ["owner"] = "o2" }, new JsonObject { ["owner"] = "o3" });
        var note = Concurrent("i2", 930, Fields("2026-03-02", "a"),
            new JsonObject { ["note"] = "one" }, new JsonObject { ["note"] = "two" });

        var run = ReportRunner.RunMonth(Form(1), 2026, 3, Entities([.. owner, .. note]), Catalog);

        Assert.Equal(["i1"], run.Conflicted);
        Assert.Equal(["i2"], Assert.Single(run.Cells).Records);
        Assert.Empty(ReportRunner.RunMonth(Form(1, columns: null), 2026, 3, Entities([.. owner]), Catalog).Conflicted);
    }

    [Fact]
    public void A_disputed_date_is_conflicted_in_every_period_one_of_its_dates_falls_in()
    {
        var changes = Concurrent("i1", 940, Fields("2026-03-31", "a"),
            new JsonObject { ["day"] = "2026-04-01" }, new JsonObject { ["day"] = "2026-03-30" });
        var items = Entities(changes).ToList();

        Assert.Equal(["i1"], ReportRunner.RunMonth(Form(1), 2026, 3, items, Catalog).Conflicted);
        Assert.Equal(["i1"], ReportRunner.RunMonth(Form(1), 2026, 4, items, Catalog).Conflicted);
        Assert.Empty(ReportRunner.RunMonth(Form(1), 2026, 5, items, Catalog).Total);
    }

    [Fact]
    public void A_person_settling_the_conflict_puts_the_record_in_its_cell_and_the_diff_says_so()
    {
        var changes = Concurrent("i1", 950, Fields("2026-03-02", "a"),
            new JsonObject { ["kind"] = Coded(1, "a") }, new JsonObject { ["kind"] = Coded(1, "b") });
        var settle = Json(953, "i1", "update", [951, 952], new JsonObject { ["kind"] = Coded(1, "b") }, entityType: "item");

        var before = ReportRunner.RunMonth(Form(1), 2026, 3, Entities(changes), Catalog);
        var after = ReportRunner.RunMonth(Form(1), 2026, 3, Entities([.. changes, settle]), Catalog);
        var diff = ReportDiff.Compare(before, after, Catalog);

        Assert.Equal(["i1"], before.Conflicted);
        var cell = Assert.Single(after.Cells);
        Assert.Equal("b", cell.Row);
        Assert.Equal(["i1"], cell.Records);
        Assert.Equal(["i1"], diff.Settled);
        Assert.Empty(diff.Moved);
        Assert.Empty(diff.Revised);
    }

    [Fact]
    public void A_value_the_crosswalks_do_not_carry_or_of_another_scheme_is_unmapped_but_not_blank()
    {
        var other = Json(906, "i2", "create", entityType: "item",
            fields: new JsonObject { ["day"] = "2026-03-03", ["kind"] = new JsonObject { ["scheme"] = "other", ["version"] = 1, ["code"] = "a" } });
        var items = Entities([.. Item("i1", "2026-03-02", "h"), other]);

        var run = ReportRunner.RunMonth(Form(2), 2026, 3, items, Catalog);

        Assert.Equal(["i1", "i2"], run.Unmapped);
        Assert.Empty(run.Blank);
    }

    [Fact]
    public void A_cleared_value_still_counts_where_its_earlier_value_places_it()
    {
        var create = Json(907, "i1", "create", entityType: "item",
            fields: new JsonObject { ["day"] = "2026-03-02", ["kind"] = Coded(1, "a") });
        var clear = Json(908, "i1", "update", [907], new JsonObject { ["kind"] = null }, entityType: "item");

        var run = ReportRunner.RunMonth(Form(1), 2026, 3, Entities([create, clear]), Catalog);

        Assert.Equal("a", Assert.Single(run.Cells).Row);
        Assert.Empty(run.Unmapped);
        Assert.Empty(run.Blank);
    }

    [Fact]
    public void Without_columns_every_cell_has_a_null_column()
    {
        var run = ReportRunner.RunMonth(Form(1, columns: null), 2026, 3, Entities(Item("i1", "2026-03-02", "a")), Catalog);

        Assert.Null(Assert.Single(run.Cells).Column);
    }

    [Fact]
    public void The_diff_separates_late_records_from_those_the_revision_moved()
    {
        var early = Entities([.. Item("i1", "2026-03-02", "a"), .. Item("i2", "2026-03-03", "c")]).ToList();
        var later = early.Concat(Entities(Item("i3", "2026-03-04", "a"))).ToList();

        var before = ReportRunner.RunMonth(Form(1), 2026, 3, early, Catalog);
        var after = ReportRunner.RunMonth(Form(2), 2026, 3, later, Catalog);
        var diff = ReportDiff.Compare(before, after, Catalog);

        Assert.Equal(["i3"], diff.Late);
        Assert.Empty(diff.Removed);
        Assert.Equal(["i1", "i2"], diff.Revised); // a → x, and c → pending
        Assert.Empty(diff.Moved);
        Assert.Empty(diff.Unchanged);
        Assert.Empty(ReportDiff.Compare(after, after, Catalog).Revised);
    }

    // Version 1 of `kind` is in force up to the end of March 2026 and version 2 from April 15.
    private static readonly SchemeCatalog Dated = new(
        [
            new Scheme("kind", 1, Catalog.Find("kind", 1)!.Items, null, new DateOnly(2026, 3, 31)),
            new Scheme("kind", 2, Catalog.Find("kind", 2)!.Items, new DateOnly(2026, 4, 15)),
        ],
        [new Crosswalk("kind", 1, 2, [("a", "x"), ("b", "x"), ("c", "p"), ("c", "q")])]);

    private static ReportDefinition InForce => Form(1) with { RowVersion = null };

    [Fact]
    public void A_form_without_a_version_counts_in_the_version_in_force_on_the_last_day()
    {
        var items = Entities([.. Item("i1", "2026-03-02", "a"), .. Item("i2", "2026-05-03", "a")]).ToList();

        var march = ReportRunner.RunMonth(InForce, 2026, 3, items, Dated);
        var may = ReportRunner.RunMonth(InForce, 2026, 5, items, Dated);

        Assert.Equal((1, "a"), (march.Report.RowVersion!.Value, Assert.Single(march.Cells).Row));
        Assert.Empty(march.Boundaries);
        Assert.Equal((2, "x"), (may.Report.RowVersion!.Value, Assert.Single(may.Cells).Row));
        Assert.Equal(["1-2"], may.Crosswalks);
    }

    [Fact]
    public void A_period_across_a_change_of_version_says_on_which_days_it_changed()
    {
        var run = ReportRunner.Run(InForce, new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30),
            Entities(Item("i1", "2026-03-02", "a")), Dated);

        Assert.Equal(2, run.Report.RowVersion);
        Assert.Equal([new SchemeBoundary(new DateOnly(2026, 4, 1), 1, null), new SchemeBoundary(new DateOnly(2026, 4, 15), null, 2)], run.Boundaries);
        Assert.Empty(ReportRunner.Run(Form(2), new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30), [], Dated).Boundaries);
    }

    [Fact]
    public void A_form_without_a_version_cannot_run_over_a_period_ending_when_none_is_in_force()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ReportRunner.Run(InForce, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 10), [], Dated));

        Assert.Contains("in force on 2026-04-10", error.Message, StringComparison.Ordinal);
    }

    private static ReportRun Run(int version, ReportCell[] cells, string[]? pending = null, string[]? unmapped = null) =>
        new(Form(version), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), version == 1 ? [] : ["1-2"],
            cells, pending ?? [], unmapped ?? [], [], []);

    [Fact]
    public void A_code_with_no_link_is_revised_into_unmapped()
    {
        var diff = ReportDiff.Compare(Run(1, [new("h", "o1", ["i1"])]), Run(2, [], unmapped: ["i1"]), Catalog);

        Assert.Equal(["i1"], diff.Revised);
        Assert.Empty(diff.Moved);
    }

    [Fact]
    public void A_place_the_crosswalks_do_not_reach_is_a_move()
    {
        // a carries to x; the record now counts under p, so someone changed it.
        var diff = ReportDiff.Compare(Run(1, [new("a", "o1", ["i1"])]), Run(2, [new("p", "o1", ["i1"])]), Catalog);

        Assert.Empty(diff.Revised);
        Assert.Equal(["i1"], diff.Moved);
    }

    [Fact]
    public void Another_column_is_a_move_even_when_the_row_was_carried()
    {
        var diff = ReportDiff.Compare(Run(1, [new("a", "o1", ["i1"])]), Run(2, [new("x", "o2", ["i1"])]), Catalog);

        Assert.Empty(diff.Revised);
        Assert.Equal(["i1"], diff.Moved);
    }

    [Fact]
    public void A_pending_record_a_person_placed_is_a_move()
    {
        var diff = ReportDiff.Compare(Run(2, [], pending: ["i1"]), Run(2, [new("q", "o1", ["i1"])]), Catalog);

        Assert.Empty(diff.Revised);
        Assert.Equal(["i1"], diff.Moved);
    }

    [Fact]
    public void Without_a_later_version_nothing_is_revised()
    {
        // Same version: no revision lies between the runs.
        var sameVersion = ReportDiff.Compare(Run(2, [new("x", "o1", ["i1"])]), Run(2, [new("p", "o1", ["i1"])]), Catalog);

        Assert.Empty(sameVersion.Revised);
        Assert.Equal(["i1"], sameVersion.Moved);
    }

    [Fact]
    public void Runs_in_the_reverse_order_are_refused()
    {
        var v1 = Run(1, [new("a", "o1", ["i1"])]);
        var v2 = Run(2, [], unmapped: ["i1"]);

        var error = Assert.Throws<ArgumentException>(() => ReportDiff.Compare(v2, v1, Catalog));
        Assert.Equal("later", error.ParamName);
    }

    public static TheoryData<string> Incomparable => ["name", "counts", "period field", "row field", "row scheme", "columns", "from", "to"];

    [Theory]
    [MemberData(nameof(Incomparable))]
    public void Runs_that_do_not_count_the_same_thing_over_the_same_period_are_refused(string differs)
    {
        var earlier = Run(1, [new("a", "o1", ["i1"])]);
        var form = earlier.Report;
        var later = differs switch
        {
            "name" => earlier with { Report = form with { Name = "other" } },
            "counts" => earlier with { Report = form with { Counts = "session" } },
            "period field" => earlier with { Report = form with { PeriodField = "entered" } },
            "row field" => earlier with { Report = form with { RowField = "topic" } },
            "row scheme" => earlier with { Report = form with { RowScheme = "topic" } },
            "columns" => earlier with { Report = form with { ColumnField = null } },
            "from" => earlier with { From = new DateOnly(2026, 2, 1) },
            "to" => earlier with { To = new DateOnly(2026, 4, 30) },
            _ => throw new ArgumentOutOfRangeException(nameof(differs)),
        };

        var error = Assert.Throws<ArgumentException>(() => ReportDiff.Compare(earlier, later, Catalog));
        Assert.Equal("later", error.ParamName);
    }

    [Fact]
    public void A_new_form_version_and_label_still_compare()
    {
        var earlier = Run(1, [new("a", "o1", ["i1"])]);
        var later = earlier with { Report = earlier.Report with { Version = 2, Label = "Renamed" } };

        Assert.Equal(["i1"], ReportDiff.Compare(earlier, later, Catalog).Unchanged);
    }

    [Fact]
    public void The_run_record_lists_every_number_with_its_records()
    {
        var run = ReportRunner.RunMonth(Form(2), 2026, 3,
            Entities([.. Item("i1", "2026-03-02", "a"), .. Item("i2", "2026-03-03", "c")]), Catalog);

        using var doc = JsonDocument.Parse(ReportRunJson.Write(run, Id(1), "dev1", new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.FromHours(9))));
        var root = doc.RootElement;

        Assert.Equal("openquote.run/0", root.GetProperty("format").GetString());
        Assert.Equal("2026-04-01T09:00:00+09:00", root.GetProperty("at").GetString());
        Assert.Equal(2, root.GetProperty("schemes").GetProperty("kind").GetProperty("version").GetInt32());
        Assert.Equal("1-2", root.GetProperty("schemes").GetProperty("kind").GetProperty("crosswalks")[0].GetString());
        Assert.Equal("2026-03-31", root.GetProperty("period").GetProperty("to").GetString());
        var cell = root.GetProperty("cells")[0];
        Assert.Equal(1, cell.GetProperty("count").GetInt32());
        Assert.Equal("i1", cell.GetProperty("records")[0].GetString());
        Assert.Equal(1, root.GetProperty("pending").GetProperty("count").GetInt32());
        Assert.Equal(2, root.GetProperty("total").GetProperty("count").GetInt32());
    }
}
