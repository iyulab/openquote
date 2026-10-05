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
        TestReports.Form("monthly", version, "Monthly", "item", "day", "kind", "kind", version, columns);

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

    private static ReportDefinition InForce => Form(1).WithRowVersion(null);

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

    // A local list beside `kind` v1: two items counted as a, one anchored to an item v1 does not have.
    private static readonly SchemeCatalog Extended = new(
        [
            .. new[] { Catalog.Find("kind", 1)!, Catalog.Find("kind", 2)! },
            new Scheme("kind.local", 1, [new("a-group", "A in a group", null, false) { Anchor = "a" },
                new("a-call", "A by phone", null, false) { Anchor = "a" }, new("z", "Z", null, false) { Anchor = "zz" }])
            { Extends = new SchemeVersion("kind", 1) },
        ],
        [new Crosswalk("kind", 1, 2, [("a", "x"), ("b", "x"), ("c", "p"), ("c", "q")])]);

    private static List<JsonObject> Local(string id, string day, string code) =>
        [Json(Interlocked.Increment(ref _n), id, "create", entityType: "item", fields: new JsonObject
        {
            ["day"] = day,
            ["kind"] = new JsonObject { ["scheme"] = "kind.local", ["version"] = 1, ["code"] = code },
            ["owner"] = "o1",
        })];

    [Fact]
    public void A_value_of_a_scheme_extending_the_rows_counts_as_its_anchor_carried_like_any_value()
    {
        var items = Entities([.. Local("i1", "2026-03-02", "a-group"), .. Local("i2", "2026-03-03", "a-call"),
            .. Item("i3", "2026-03-04", "a"), .. Local("i4", "2026-03-05", "z")]).ToList();

        var v1 = ReportRunner.RunMonth(Form(1), 2026, 3, items, Extended);
        var v2 = ReportRunner.RunMonth(Form(2), 2026, 3, items, Extended);

        Assert.Equal("a", Assert.Single(v1.Cells).Row);
        Assert.Equal(["i1", "i2", "i3"], Assert.Single(v1.Cells).Records);
        Assert.Equal(["i4"], v1.Unmapped);
        Assert.Equal("x", Assert.Single(v2.Cells).Row);
        Assert.Equal(["i1", "i2", "i3"], Assert.Single(v2.Cells).Records);
    }

    [Fact]
    public void A_form_counting_the_extending_scheme_counts_its_own_items()
    {
        var local = TestReports.Form("local", 1, "Local", "item", "day", "kind", "kind.local", 1, null);
        var items = Entities([.. Local("i1", "2026-03-02", "a-group"), .. Local("i2", "2026-03-03", "a-call"), .. Item("i3", "2026-03-04", "a")]);

        var run = ReportRunner.RunMonth(local, 2026, 3, items, Extended);

        Assert.Equal(["a-call", "a-group"], run.Cells.Select(c => c.Row));
        Assert.Equal(["i3"], run.Unmapped);
    }

    private static ReportRun Run(int version, ReportCell[] cells, string[]? pending = null, string[]? unmapped = null) =>
        new(Form(version), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [new ReportScheme("kind", version, version == 1 ? [] : ["1-2"], [])],
            cells, pending ?? [], unmapped ?? [], [], []);

    [Fact]
    public void A_code_with_no_link_is_revised_into_unmapped()
    {
        var diff = ReportDiff.Compare(Run(1, [TestReports.Cell("h", "o1", ["i1"])]), Run(2, [], unmapped: ["i1"]), Catalog);

        Assert.Equal(["i1"], diff.Revised);
        Assert.Empty(diff.Moved);
    }

    [Fact]
    public void A_place_the_crosswalks_do_not_reach_is_a_move()
    {
        // a carries to x; the record now counts under p, so someone changed it.
        var diff = ReportDiff.Compare(Run(1, [TestReports.Cell("a", "o1", ["i1"])]), Run(2, [TestReports.Cell("p", "o1", ["i1"])]), Catalog);

        Assert.Empty(diff.Revised);
        Assert.Equal(["i1"], diff.Moved);
    }

    [Fact]
    public void Another_column_is_a_move_even_when_the_row_was_carried()
    {
        var diff = ReportDiff.Compare(Run(1, [TestReports.Cell("a", "o1", ["i1"])]), Run(2, [TestReports.Cell("x", "o2", ["i1"])]), Catalog);

        Assert.Empty(diff.Revised);
        Assert.Equal(["i1"], diff.Moved);
    }

    [Fact]
    public void A_pending_record_a_person_placed_is_a_move()
    {
        var diff = ReportDiff.Compare(Run(2, [], pending: ["i1"]), Run(2, [TestReports.Cell("q", "o1", ["i1"])]), Catalog);

        Assert.Empty(diff.Revised);
        Assert.Equal(["i1"], diff.Moved);
    }

    [Fact]
    public void Without_a_later_version_nothing_is_revised()
    {
        // Same version: no revision lies between the runs.
        var sameVersion = ReportDiff.Compare(Run(2, [TestReports.Cell("x", "o1", ["i1"])]), Run(2, [TestReports.Cell("p", "o1", ["i1"])]), Catalog);

        Assert.Empty(sameVersion.Revised);
        Assert.Equal(["i1"], sameVersion.Moved);
    }

    [Fact]
    public void Runs_in_the_reverse_order_are_refused()
    {
        var v1 = Run(1, [TestReports.Cell("a", "o1", ["i1"])]);
        var v2 = Run(2, [], unmapped: ["i1"]);

        var error = Assert.Throws<ArgumentException>(() => ReportDiff.Compare(v2, v1, Catalog));
        Assert.Equal("later", error.ParamName);
    }

    public static TheoryData<string> Incomparable => ["name", "counts", "period field", "row field", "row scheme", "columns", "from", "to"];

    [Theory]
    [MemberData(nameof(Incomparable))]
    public void Runs_that_do_not_count_the_same_thing_over_the_same_period_are_refused(string differs)
    {
        var earlier = Run(1, [TestReports.Cell("a", "o1", ["i1"])]);
        var form = earlier.Report;
        var later = differs switch
        {
            "name" => earlier with { Report = form with { Name = "other" } },
            "counts" => earlier with { Report = form with { Counts = "session" } },
            "period field" => earlier with { Report = form with { Period = new ReportPeriod("entered") } },
            "row field" => earlier with { Report = form with { Dimensions = [form.Dimensions[0] with { Field = "topic" }, form.Dimensions[1]] } },
            "row scheme" => earlier with { Report = form with { Dimensions = [form.Dimensions[0] with { Scheme = "topic" }, form.Dimensions[1]] } },
            "columns" => earlier with { Report = form with { Dimensions = [form.Dimensions[0]] } },
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
        var earlier = Run(1, [TestReports.Cell("a", "o1", ["i1"])]);
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

    // `kind` as above, and `level` v1 with two levels.
    private static readonly SchemeCatalog Levels = new(
        [
            Catalog.Find("kind", 1)!, Catalog.Find("kind", 2)!,
            new Scheme("level", 1, [new("mid", "Middle", null, false), new("high", "High", null, false)]),
        ],
        [new Crosswalk("kind", 1, 2, [("a", "x"), ("b", "x"), ("c", "p"), ("c", "q")])]);

    private static ReportDefinition ByKindLevelOwner(int kindVersion) => new("by-level", 1, "By level", "item", new ReportPeriod("day"),
        [new ReportDimension("kind", "kind", kindVersion), new ReportDimension("level", "level", 1), new ReportDimension("owner")]);

    private static List<JsonObject> Leveled(string id, string? code, string? level, string? owner = "o1")
    {
        var fields = new JsonObject { ["day"] = "2026-03-02" };
        if (code is not null) fields["kind"] = Coded(1, code);
        if (level is not null) fields["level"] = new JsonObject { ["scheme"] = "level", ["version"] = 1, ["code"] = level };
        if (owner is not null) fields["owner"] = owner;
        return [Json(Interlocked.Increment(ref _n), id, "create", entityType: "item", fields: fields)];
    }

    [Fact]
    public void Each_cell_is_keyed_by_every_dimension_in_order()
    {
        var items = Entities([.. Leveled("i1", "a", "mid"), .. Leveled("i2", "b", "mid"), .. Leveled("i3", "a", "high", owner: null),
            .. Leveled("i4", "a", "mid", owner: "o2")]);

        var run = ReportRunner.RunMonth(ByKindLevelOwner(2), 2026, 3, items, Levels);

        Assert.Collection(run.Cells,
            c => { Assert.Equal(["x", "high", null], c.Key); Assert.Equal(["i3"], c.Records); },
            c => { Assert.Equal(["x", "mid", "o1"], c.Key); Assert.Equal(["i1", "i2"], c.Records); },
            c => { Assert.Equal(["x", "mid", "o2"], c.Key); Assert.Equal(["i4"], c.Records); });
        Assert.Equal([("kind", 2), ("level", 1)], run.Schemes.Select(x => (x.Scheme, x.Version)));
        Assert.Equal(["1-2"], run.Schemes[0].Crosswalks);
        Assert.Empty(run.Schemes[1].Crosswalks);
    }

    [Fact]
    public void A_record_several_dimensions_leave_out_is_in_one_set_pending_before_unmapped_before_blank()
    {
        // c is split in v2 (pending); h has no link (unmapped); a missing value is blank; "low" is no level.
        var items = Entities([.. Leveled("i1", "c", "low"), .. Leveled("i2", null, "low"), .. Leveled("i3", null, "mid"),
            .. Leveled("i4", "h", null), .. Leveled("i5", "a", null)]);

        var run = ReportRunner.RunMonth(ByKindLevelOwner(2), 2026, 3, items, Levels);

        Assert.Empty(run.Cells);
        Assert.Equal(["i1"], run.Pending);
        Assert.Equal(["i2", "i4"], run.Unmapped);
        Assert.Equal(["i3", "i5"], run.Blank);
        Assert.Equal(["i1", "i2", "i3", "i4", "i5"], run.Total);
    }

    [Fact]
    public void A_conflict_in_any_dimension_leaves_the_record_conflicted()
    {
        var create = Json(900, "i1", "create", entityType: "item",
            fields: new JsonObject { ["day"] = "2026-03-02", ["kind"] = Coded(1, "a"), ["level"] = new JsonObject { ["scheme"] = "level", ["version"] = 1, ["code"] = "mid" } });
        var one = Json(901, "i1", "update", [900], new JsonObject { ["owner"] = "o1" }, entityType: "item");
        var two = Json(902, "i1", "update", [900], new JsonObject { ["owner"] = "o2" }, entityType: "item");

        var run = ReportRunner.RunMonth(ByKindLevelOwner(2), 2026, 3, Entities([create, one, two]), Levels);

        Assert.Equal(["i1"], run.Conflicted);
        Assert.Empty(run.Cells);
    }

    [Fact]
    public void A_form_counting_one_scheme_in_two_versions_cannot_be_run()
    {
        var form = new ReportDefinition("twice", 1, "Twice", "item", new ReportPeriod("day"),
            [new ReportDimension("kind", "kind", 1), new ReportDimension("kind", "kind", 2)]);

        Assert.NotNull(form.Problem());
        Assert.Throws<ArgumentException>(() => ReportRunner.RunMonth(form, 2026, 3, [], Levels));
        Assert.Null((form with { Dimensions = [new ReportDimension("kind", "kind", 2), new ReportDimension("other", "kind", 2)] }).Problem());
    }

    [Fact]
    public void A_revision_of_one_dimension_is_told_apart_from_a_move_in_another()
    {
        var before = Entities([.. Leveled("i1", "a", "mid"), .. Leveled("i2", "c", "mid"), .. Leveled("i3", "b", "high")]);
        var earlier = ReportRunner.RunMonth(ByKindLevelOwner(1), 2026, 3, before, Levels);
        var after = Entities([.. Leveled("i1", "a", "mid"), .. Leveled("i2", "c", "mid"), .. Leveled("i3", "b", "mid")]);
        var later = ReportRunner.RunMonth(ByKindLevelOwner(2), 2026, 3, after, Levels);

        var diff = ReportDiff.Compare(earlier, later, Levels);

        Assert.Equal(["i1", "i2"], diff.Revised); // a carried to x; c split into pending
        Assert.Equal(["i3"], diff.Moved); // its level changed, which no revision explains
    }

    [Fact]
    public void A_run_of_more_than_rows_and_a_column_is_written_with_keys()
    {
        var run = ReportRunner.RunMonth(ByKindLevelOwner(2), 2026, 3, Entities(Leveled("i1", "a", "mid", owner: null)), Levels);

        using var doc = JsonDocument.Parse(ReportRunJson.Write(run, Id(2), "dev1", new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero)));
        var root = doc.RootElement;

        Assert.Equal("openquote.run/1", root.GetProperty("format").GetString());
        var key = root.GetProperty("cells")[0].GetProperty("key");
        Assert.Equal(["x", "mid", null], key.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.Null ? null : k.GetString()));
        Assert.False(root.GetProperty("cells")[0].TryGetProperty("row", out _));
        Assert.Equal(1, root.GetProperty("schemes").GetProperty("level").GetProperty("version").GetInt32());
    }

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private static Dictionary<string, JsonNode?> Fields(params (string Key, JsonNode? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    private static JsonObject Level(string code) => new() { ["scheme"] = "level", ["version"] = 1, ["code"] = code };

    // Subjects one (grade 2, middle), two (grade 3, high) and three (grade 2, no level); a session of
    // each, and group sessions of one with two and of one with three.
    private static (IReadOnlyList<Entity> Entities, Dictionary<string, string> Sessions) Pupils()
    {
        var w = new VaultWriter("dev1", new StepClock());
        var files = new List<VaultFile>();
        string Add(VaultFile f)
        {
            files.Add(f);
            return VaultReader.Read([f]).Changes[0].Entity.Id;
        }
        var one = Add(w.CreateSubject(Fields(("grade", "2"), ("level", Level("mid")))));
        var two = Add(w.CreateSubject(Fields(("grade", "3"), ("level", Level("high")))));
        var three = Add(w.CreateSubject(Fields(("grade", "2"))));
        var group = Add(w.CreateGroup(Fields(("name", "g"))));
        JsonObject Kind() => Coded(1, "a");
        var sessions = new Dictionary<string, string>
        {
            ["one"] = Add(w.CreateInSubject(one, "session", Fields(("day", "2026-03-02"), ("kind", Kind())))),
            ["two"] = Add(w.CreateInSubject(two, "session", Fields(("day", "2026-03-03"), ("kind", Kind())))),
            ["one+two"] = Add(w.CreateInGroup(group, "session", Fields(("day", "2026-03-04"), ("kind", Kind()), ("attendees", new JsonArray(one, two))))),
            ["one+three"] = Add(w.CreateInGroup(group, "session", Fields(("day", "2026-03-05"), ("kind", Kind()), ("attendees", new JsonArray(one, three))))),
            ["three"] = Add(w.CreateInSubject(three, "session", Fields(("day", "2026-03-06"), ("kind", Kind())))),
        };
        return (EntityMerger.Merge(VaultReader.Read(files).Changes).Values.ToList(), sessions);
    }

    private static ReportDefinition Sessions(params ReportDimension[] dimensions) =>
        new("pupils", 1, "Pupils", "session", new ReportPeriod("day"), [new ReportDimension("kind", "kind", 1), .. dimensions]);

    private static string[] Ids(Dictionary<string, string> sessions, params string[] names) =>
        [.. names.Select(n => sessions[n]).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_dimension_of_the_subjects_reads_their_field_and_has_no_single_value_when_they_differ()
    {
        var (entities, s) = Pupils();

        var run = ReportRunner.RunMonth(Sessions(new ReportDimension("grade", OfSubject: true)), 2026, 3, entities, Levels);

        Assert.Collection(run.Cells,
            c => { Assert.Equal(["a", null], c.Key); Assert.Equal(Ids(s, "one+two"), c.Records); },
            c => { Assert.Equal(["a", "2"], c.Key); Assert.Equal(Ids(s, "one", "one+three", "three"), c.Records); },
            c => { Assert.Equal(["a", "3"], c.Key); Assert.Equal(Ids(s, "two"), c.Records); });
    }

    [Fact]
    public void A_classified_dimension_of_one_subject_leaves_a_missing_value_blank()
    {
        var (entities, s) = Pupils();

        var run = ReportRunner.RunMonth(Sessions(new ReportDimension("level", "level", 1, OfSubject: true)), 2026, 3, entities, Levels);

        Assert.Equal(Ids(s, "three"), run.Blank);
        Assert.Equal([["a", null], ["a", "high"], ["a", "mid"]], run.Cells.Select(c => c.Key));
        Assert.Equal(Ids(s, "one+two", "one+three"), run.Cells[0].Records);
        Assert.Equal(5, run.Total.Count);
    }

    [Fact]
    public void A_filter_leaves_out_records_whose_value_is_not_listed_and_keeps_those_it_cannot_place_yet()
    {
        var (entities, s) = Pupils();
        var byGrade = Sessions() with { Filters = [new ReportFilter(new ReportDimension("grade", OfSubject: true), ["2"])] };
        var byLevel = Sessions() with { Filters = [new ReportFilter(new ReportDimension("level", "level", 1, OfSubject: true), ["mid"])] };

        var grade = ReportRunner.RunMonth(byGrade, 2026, 3, entities, Levels);
        var level = ReportRunner.RunMonth(byLevel, 2026, 3, entities, Levels);

        Assert.Equal(Ids(s, "one", "one+three", "three"), grade.Total); // two is grade 3; one+two has no single grade
        Assert.Equal(Ids(s, "one"), Assert.Single(level.Cells).Records);
        Assert.Equal(Ids(s, "three"), level.Blank); // no level yet: listed, not dropped
        Assert.Equal(Ids(s, "one", "three"), level.Total);
        Assert.Equal(["level"], level.Schemes.Skip(1).Select(x => x.Scheme));
    }

    [Fact]
    public void Runs_filtered_differently_are_not_compared()
    {
        var (entities, _) = Pupils();
        var form = Sessions() with { Filters = [new ReportFilter(new ReportDimension("grade", OfSubject: true), ["2"])] };
        var earlier = ReportRunner.RunMonth(form, 2026, 3, entities, Levels);
        var later = ReportRunner.RunMonth(form with { Filters = [new ReportFilter(new ReportDimension("grade", OfSubject: true), ["3"])] }, 2026, 3, entities, Levels);

        Assert.Throws<ArgumentException>(() => ReportDiff.Compare(earlier, later, Levels));
        Assert.Empty(ReportDiff.Compare(earlier, ReportRunner.RunMonth(form, 2026, 3, entities, Levels), Levels).Moved);
    }

    [Fact]
    public void A_run_with_a_filter_or_a_dimension_of_the_subjects_is_written_with_keys()
    {
        var (entities, _) = Pupils();
        var run = ReportRunner.RunMonth(Sessions(new ReportDimension("grade", OfSubject: true)), 2026, 3, entities, Levels);

        var text = System.Text.Encoding.UTF8.GetString(ReportRunJson.Write(run, Id(3), "dev1", new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero)));

        Assert.Contains("\"openquote.run/1\"", text, StringComparison.Ordinal);
        Assert.False((Sessions() with { Filters = [new ReportFilter(new ReportDimension("grade"), ["2"])] }).RowsAndColumn);
    }

    [Theory]
    [InlineData(PeriodUnit.Day, 1, "2026-02-10", "2026-02-10", "2026-02-10")]
    [InlineData(PeriodUnit.Month, 1, "2026-02-10", "2026-02-01", "2026-02-28")]
    [InlineData(PeriodUnit.Year, 1, "2026-02-10", "2026-01-01", "2026-12-31")]
    [InlineData(PeriodUnit.Year, 3, "2026-02-10", "2025-03-01", "2026-02-28")] // a school year from March
    [InlineData(PeriodUnit.Year, 3, "2026-03-01", "2026-03-01", "2027-02-28")]
    public void A_period_of_a_unit_holds_the_day_it_is_asked_for(PeriodUnit unit, int startMonth, string day, string from, string to)
    {
        var period = new ReportPeriod("day", unit, startMonth);

        Assert.Equal((DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)), period.Containing(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_range_is_picked_by_a_person_and_only_a_year_has_a_start_month()
    {
        Assert.Null(new ReportPeriod("day", PeriodUnit.Range).Containing(new DateOnly(2026, 2, 10)));
        Assert.NotNull(new ReportPeriod("day", PeriodUnit.Month, 3).Problem());
        Assert.NotNull(new ReportPeriod("day", PeriodUnit.Year, 13).Problem());
        Assert.Throws<ArgumentException>(() => ReportRunner.RunContaining(
            Form(1) with { Period = new ReportPeriod("day", PeriodUnit.Range) }, new DateOnly(2026, 3, 1), [], Catalog));
    }

    [Fact]
    public void A_yearly_form_runs_over_the_year_from_its_start_month()
    {
        var items = Entities([.. Item("i1", "2026-02-27", "a"), .. Item("i2", "2025-03-01", "a"), .. Item("i3", "2026-03-01", "a")]);
        var schoolYear = Form(1) with { Period = new ReportPeriod("day", PeriodUnit.Year, 3) };

        var run = ReportRunner.RunContaining(schoolYear, new DateOnly(2025, 9, 1), items, Catalog);

        Assert.Equal((new DateOnly(2025, 3, 1), new DateOnly(2026, 2, 28)), (run.From, run.To));
        Assert.Equal(["i1", "i2"], run.Total);
    }

    [Fact]
    public void Visits_add_up_the_people_of_each_record_while_people_count_each_person_once()
    {
        var (entities, s) = Pupils();

        var run = ReportRunner.RunMonth(Sessions(), 2026, 3, entities, Levels);

        Assert.Equal(5, run.Total.Count);
        Assert.Equal(7, run.VisitsOf(run.Total)); // three sessions of one, two of two
        Assert.Equal(3, run.PeopleOf(run.Total)!.Count);
        Assert.Equal(2, run.VisitsOf(Ids(s, "one+two")));
        Assert.Null((run with { People = null }).VisitsOf(run.Total));
    }

    [Fact]
    public void A_form_may_count_subjects_by_a_date_of_their_own()
    {
        var w = new VaultWriter("dev1", new StepClock());
        var files = new[]
        {
            w.CreateSubject(Fields(("registered", "2026-03-04"), ("grade", "2"))),
            w.CreateSubject(Fields(("registered", "2026-03-20"), ("grade", "3"))),
            w.CreateSubject(Fields(("registered", "2026-04-01"), ("grade", "2"))),
        };
        var subjects = EntityMerger.Merge(VaultReader.Read(files).Changes).Values;
        var registrations = new ReportDefinition("new", 1, "New", "subject", new ReportPeriod("registered"), [new ReportDimension("grade")]);

        var run = ReportRunner.RunMonth(registrations, 2026, 3, subjects, Levels);

        Assert.Equal([["2"], ["3"]], run.Cells.Select(c => c.Key));
        Assert.Equal(2, run.PeopleOf(run.Total)!.Count); // a subject is about itself
    }

    [Fact]
    public void A_form_shows_each_measure_once()
    {
        Assert.Equal([ReportMeasure.Records, ReportMeasure.People], Form(1).Measures);
        Assert.NotNull((Form(1) with { Measures = [] }).Problem());
        Assert.NotNull((Form(1) with { Measures = [ReportMeasure.Visits, ReportMeasure.Visits] }).Problem());
        Assert.Null((Form(1) with { Measures = [ReportMeasure.Visits] }).Problem());
        Assert.Null((Form(1) with { Measures = [], Sums = ["minutes"] }).Problem()); // a field added up is a measure too
        Assert.NotNull((Form(1) with { Sums = ["minutes", "minutes"] }).Problem());
    }

    [Fact]
    public void A_form_adds_up_a_number_field_and_says_how_many_records_hold_none()
    {
        var w = new VaultWriter("dev1", new StepClock());
        var files = new List<VaultFile>();
        string Add(VaultFile f)
        {
            files.Add(f);
            return VaultReader.Read([f]).Changes[0].Entity.Id;
        }
        var one = Add(w.CreateSubject(Fields(("grade", "2"))));
        var fifty = Add(w.CreateInSubject(one, "session", Fields(("day", "2026-03-02"), ("kind", Coded(1, "a")), ("minutes", 50))));
        var thirty = Add(w.CreateInSubject(one, "session", Fields(("day", "2026-03-09"), ("kind", Coded(1, "a")), ("minutes", 30.5m))));
        var none = Add(w.CreateInSubject(one, "session", Fields(("day", "2026-03-16"), ("kind", Coded(1, "b")))));
        var words = Add(w.CreateInSubject(one, "session", Fields(("day", "2026-03-23"), ("kind", Coded(1, "b")), ("minutes", "an hour"))));
        var entities = EntityMerger.Merge(VaultReader.Read(files).Changes).Values;
        var form = Sessions() with { Sums = ["minutes"] };

        var run = ReportRunner.RunMonth(form, 2026, 3, entities, Levels);

        Assert.Equal((80.5m, 2), run.SumOf("minutes", run.Total)); // two records hold no number: said, not counted as 0
        Assert.Equal((80.5m, 0), run.SumOf("minutes", run.Cells.Single(c => c.Key[0] == "a").Records));
        Assert.Equal((0m, 2), run.SumOf("minutes", run.Cells.Single(c => c.Key[0] == "b").Records));
        Assert.Null(run.SumOf("hours", run.Total));
        Assert.Null(ReportRunner.RunMonth(Sessions(), 2026, 3, entities, Levels).SumOf("minutes", run.Total));
        Assert.Equal([fifty, thirty], run.Values!["minutes"].Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(none, run.Values["minutes"].Keys);
        Assert.DoesNotContain(words, run.Values["minutes"].Keys);
    }

    private static List<JsonObject> Mentions(string id, params string[] codes)
    {
        var n = Interlocked.Increment(ref _n);
        var values = new JsonArray();
        foreach (var code in codes) values.Add(Coded(1, code));
        return [Json(n, id, "create", entityType: "item", fields: new JsonObject { ["day"] = "2026-03-02", ["kind"] = values })];
    }

    private static ReportDefinition EveryValue(int version) =>
        new("mentions", 1, "Mentions", "item", new ReportPeriod("day"), [new ReportDimension("kind", "kind", version, All: true)]);

    private static IEnumerable<JsonObject> MentionItems() =>
        [.. Mentions("i1", "a", "b"), .. Mentions("i2", "a", "h"), .. Mentions("i3", "c", "a"), .. Mentions("i4"), .. Item("i5", "2026-03-02", "a")];

    [Fact]
    public void Counting_every_value_puts_a_record_in_each_cell_its_values_lead_to_and_in_the_set_of_one_that_cannot_be_placed()
    {
        var run = ReportRunner.RunMonth(EveryValue(2), 2026, 3, Entities(MentionItems()), Catalog);

        Assert.True(run.Multiple);
        Assert.Equal(["i1", "i2", "i3", "i5"], Assert.Single(run.Cells).Records); // a and b both lead to x: once
        Assert.Equal(["i3"], run.Pending);   // c also leads to p or q
        Assert.Equal(["i2"], run.Unmapped);  // h also has no link
        Assert.Equal(["i4"], run.Blank);     // an empty list is no value
        Assert.Equal(["i1", "i2", "i3", "i4", "i5"], run.Total);
    }

    [Fact]
    public void In_the_version_entered_every_value_is_its_own_cell_and_the_cells_add_up_to_more_than_the_records()
    {
        var run = ReportRunner.RunMonth(EveryValue(1), 2026, 3, Entities(MentionItems()), Catalog);

        Assert.Equal([["a"], ["b"], ["c"], ["h"]], run.Cells.Select(c => c.Key));
        Assert.Equal(["i1", "i2", "i3", "i5"], run.Cells[0].Records);
        Assert.Equal(7, run.Cells.Sum(c => c.Count));
        Assert.Equal(5, run.Total.Count);
    }

    [Fact]
    public void Counting_by_the_primary_value_places_each_record_once()
    {
        var form = EveryValue(2) with { Dimensions = [new ReportDimension("kind", "kind", 2)] };

        var run = ReportRunner.RunMonth(form, 2026, 3, Entities(MentionItems()), Catalog);

        Assert.False(run.Multiple);
        Assert.Equal(["i1", "i5"], Assert.Single(run.Cells).Records); // a and b, unmarked, both lead to x
        Assert.Equal(["i2", "i3"], run.Pending); // which comes first is a person's to say
        Assert.Equal(["i4"], run.Blank);
    }

    [Fact]
    public void A_revision_explains_a_record_counted_by_every_value_when_all_its_places_carry()
    {
        var entities = Entities(MentionItems());
        var earlier = ReportRunner.RunMonth(EveryValue(1), 2026, 3, entities, Catalog);
        var later = ReportRunner.RunMonth(EveryValue(2), 2026, 3, entities, Catalog);

        var diff = ReportDiff.Compare(earlier, later, Catalog);

        Assert.Equal(["i1", "i2", "i3", "i5"], diff.Revised); // {a, b} → {x}; {a, h} → {x, unmapped}; {c, a} → {pending, x}
        Assert.Equal(["i4"], diff.Unchanged);
        Assert.Throws<ArgumentException>(() => ReportDiff.Compare(earlier, later with { Report = EveryValue(2) with { Dimensions = [new ReportDimension("kind", "kind", 2)] } }, Catalog));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("subject")]
    [InlineData("filter")]
    public void Only_a_classified_dimension_of_the_record_counts_every_value(string where)
    {
        var form = where switch
        {
            "string" => EveryValue(1) with { Dimensions = [new ReportDimension("kind", "kind", 1), new ReportDimension("owner", All: true)] },
            "subject" => EveryValue(1) with { Dimensions = [new ReportDimension("kind", "kind", 1, OfSubject: true, All: true)] },
            _ => EveryValue(1) with { Filters = [new ReportFilter(new ReportDimension("kind", "kind", 1, All: true), ["a"])] },
        };

        Assert.NotNull(form.Problem());
    }
}
