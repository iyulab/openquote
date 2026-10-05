using Openquote.Fields;
using Openquote.Packs;
using Openquote.Reports;
using Openquote.Records;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class FieldCatalogTests
{
    private const string CareSession = """
        { "format": "openquote.fields/0", "pack": "care", "type": "session", "version": 1,
          "fields": [
            { "name": "date", "kind": "date", "required": true, "label": "Date" },
            { "name": "method", "kind": "coded", "scheme": "care.method" },
            { "name": "practitioner", "kind": "reference", "type": "practitioner" },
            { "name": "note", "kind": "text", "tier": "narrative" } ] }
        """;

    private const string SchoolSession = """
        { "format": "openquote.fields/0", "pack": "care.school", "type": "session", "version": 1,
          "fields": [ { "name": "grade", "kind": "text", "default": { "subject": "grade" } } ],
          "constrain": [ { "name": "method", "required": true } ] }
        """;

    private static PackManifest Pack(string id, params string[] depends) =>
        new(id, 1, id, depends.ToDictionary(d => d, _ => 1), []);

    [Fact]
    public void Reads_field_definitions()
    {
        var content = VaultReader.Read([File("fields/care/session/v1.json", CareSession)]);

        Assert.Empty(content.Unreadable);
        var set = Assert.Single(content.Fields);
        Assert.Equal(("care", "session", 1), (set.Pack, set.Type, set.Version));
        Assert.Equal(
            [
                ("date", FieldKind.Date, (string?)null, (string?)null, true, FieldTier.Structured),
                ("method", FieldKind.Coded, "care.method", null, false, FieldTier.Structured),
                ("practitioner", FieldKind.Reference, null, "practitioner", false, FieldTier.Structured),
                ("note", FieldKind.Text, null, null, false, FieldTier.Narrative),
            ],
            set.Fields.Select(f => (f.Name, f.Kind, f.Scheme, f.RefType, f.Required, f.Tier)));
    }

    [Theory]
    [InlineData("fields/care/session/v2.json")]
    [InlineData("fields/care/subject/v1.json")]
    [InlineData("fields/region-a/session/v1.json")]
    [InlineData("fields/care/session/v1.json-LAPTOP")]
    public void A_field_file_in_the_wrong_place_is_reported(string path)
    {
        Assert.Equal(UnreadableReason.NameMismatch, Assert.Single(VaultReader.Read([File(path, CareSession)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("""{ "name": "m", "kind": "coded" }""")]                              // coded without a scheme
    [InlineData("""{ "name": "p", "kind": "reference" }""")]                          // reference without a type
    [InlineData("""{ "name": "d", "kind": "date", "scheme": "x" }""")]                // a scheme on a date
    [InlineData("""{ "name": "n", "kind": "text", "tier": "secret" }""")]
    [InlineData("""{ "name": "n", "kind": "text", "required": "yes" }""")]
    [InlineData("""{ "name": "n", "kind": "list" }""")]
    public void An_invalid_field_is_reported(string field)
    {
        var json = $$"""{ "format": "openquote.fields/0", "pack": "care", "type": "session", "version": 1, "fields": [ {{field}} ] }""";
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("fields/care/session/v1.json", json)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("openquote.fields/0", """{ "name": "m", "kind": "coded", "scheme": "k", "many": true }""")] // several values need format 1
    [InlineData("openquote.fields/1", """{ "name": "n", "kind": "text", "many": true }""")]                // only a coded field takes several
    [InlineData("openquote.fields/1", """{ "name": "m", "kind": "coded", "scheme": "k", "many": "yes" }""")]
    public void A_field_that_cannot_take_several_values_is_reported(string format, string field)
    {
        var json = $$"""{ "format": "{{format}}", "pack": "care", "type": "session", "version": 1, "fields": [ {{field}} ] }""";
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("fields/care/session/v1.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void A_coded_field_that_takes_several_values_needs_vault_format_1()
    {
        var many = """{ "format": "openquote.fields/1", "pack": "care", "type": "session", "version": 1, "fields": [ { "name": "topic", "kind": "coded", "scheme": "topic", "many": true }, { "name": "date", "kind": "date" } ] }""";

        var content = VaultReader.Read([File("fields/care/session/v1.json", many)]);
        var set = Assert.Single(content.Fields);

        Assert.True(set.Fields[0].Many);
        Assert.False(set.Fields[1].Many);
        Assert.Equal(1, content.RequiredVersion);
        Assert.Equal(0, VaultReader.Read([File("fields/care/session/v1.json", CareSession)]).RequiredVersion);
    }

    [Fact]
    public void A_field_in_format_1_may_give_a_fixed_first_value()
    {
        var json = """
            { "format": "openquote.fields/1", "pack": "care", "type": "session", "version": 1, "fields": [
              { "name": "with", "kind": "coded", "scheme": "with", "default": { "value": "client" } },
              { "name": "minutes", "kind": "number", "default": { "value": 50 } },
              { "name": "room", "kind": "text", "default": { "value": "A" } },
              { "name": "grade", "kind": "text", "default": { "subject": "grade" } },
              { "name": "date", "kind": "date" } ] }
            """;

        var fields = Assert.Single(VaultReader.Read([File("fields/care/session/v1.json", json)]).Fields).Fields;

        Assert.Equal(["client", "50", "A", null, null], fields.Select(f => f.DefaultValue));
        Assert.Equal([null, null, null, "grade", null], fields.Select(f => f.DefaultFromSubject));
    }

    [Theory]
    [InlineData("openquote.fields/0", """{ "name": "n", "kind": "text", "default": { "value": "A" } }""")]                    // a fixed value needs format 1
    [InlineData("openquote.fields/1", """{ "name": "n", "kind": "text", "default": { "value": "A", "subject": "n" } }""")]     // one source
    [InlineData("openquote.fields/1", """{ "name": "n", "kind": "text", "default": { "value": "" } }""")]
    [InlineData("openquote.fields/1", """{ "name": "n", "kind": "text", "default": { "value": 5 } }""")]
    [InlineData("openquote.fields/1", """{ "name": "n", "kind": "number", "default": { "value": "50" } }""")]
    [InlineData("openquote.fields/1", """{ "name": "m", "kind": "coded", "scheme": "k", "default": { "value": { "code": "a" } } }""")]
    [InlineData("openquote.fields/1", """{ "name": "d", "kind": "date", "default": { "value": "2026-03-02" } }""")]             // no fixed date
    [InlineData("openquote.fields/1", """{ "name": "p", "kind": "reference", "type": "practitioner", "default": { "value": "x" } }""")]
    [InlineData("openquote.fields/1", """{ "name": "n", "kind": "text", "default": {} }""")]
    public void A_default_without_one_usable_source_is_reported(string format, string field)
    {
        var json = $$"""{ "format": "{{format}}", "pack": "care", "type": "session", "version": 1, "fields": [ {{field}} ] }""";
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("fields/care/session/v1.json", json)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("""{ "name": "m", "required": false }""")]   // constraints only narrow
    [InlineData("""{ "name": "m" }""")]                       // a constraint narrows something
    [InlineData("""{ "name": "date", "required": true }""")]  // a pack does not constrain its own fields
    public void An_invalid_constraint_is_reported(string constraint)
    {
        var json = $$"""
            { "format": "openquote.fields/0", "pack": "care", "type": "session", "version": 1,
              "fields": [ { "name": "date", "kind": "date" } ], "constrain": [ {{constraint}} ] }
            """;
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("fields/care/session/v1.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void Merges_packs_in_dependency_order_and_applies_constraints()
    {
        var content = VaultReader.Read([File("fields/care/session/v1.json", CareSession), File("fields/care.school/session/v1.json", SchoolSession)]);
        var catalog = new FieldCatalog(content.Fields, [Pack("care.school", "care"), Pack("care")]);

        Assert.Equal(["date", "method", "practitioner", "note", "grade"], catalog.For("session").Select(f => f.Name));
        Assert.True(catalog.Find("session", "method")!.Required);
        Assert.Equal("grade", catalog.Find("session", "grade")!.DefaultFromSubject);
        Assert.True(catalog.IsNarrative("session", "note"));
        Assert.False(catalog.IsNarrative("session", "date"));
        Assert.Empty(catalog.Issues);
    }

    [Fact]
    public void A_field_declared_twice_or_constrained_by_an_unrelated_pack_is_an_issue()
    {
        var content = VaultReader.Read([
            File("fields/care/session/v1.json", CareSession),
            File("fields/org.x/session/v1.json", """
                { "format": "openquote.fields/0", "pack": "org.x", "type": "session", "version": 1,
                  "fields": [ { "name": "date", "kind": "text" } ], "constrain": [ { "name": "note", "hidden": true } ] }
                """),
        ]);
        var catalog = new FieldCatalog(content.Fields, [Pack("care"), Pack("org.x")]);

        Assert.Equal(
            [(FieldIssueKind.DuplicateField, "date"), (FieldIssueKind.ConstraintFromUnrelatedPack, "note")],
            catalog.Issues.Select(i => (i.Kind, i.Field)));
        Assert.Equal(FieldKind.Date, catalog.Find("session", "date")!.Kind); // the earlier pack's declaration stands
        Assert.False(catalog.Find("session", "note")!.Hidden);
    }

    [Fact]
    public void Hiding_a_required_field_is_an_issue()
    {
        var content = VaultReader.Read([
            File("fields/care/session/v1.json", CareSession),
            File("fields/care.school/session/v1.json", """
                { "format": "openquote.fields/0", "pack": "care.school", "type": "session", "version": 1,
                  "constrain": [ { "name": "date", "hidden": true } ] }
                """),
        ]);

        var issue = Assert.Single(new FieldCatalog(content.Fields, [Pack("care"), Pack("care.school", "care")]).Issues);
        Assert.Equal((FieldIssueKind.HiddenRequired, "date"), (issue.Kind, issue.Field));
    }

    private const string CareReferral = """
        { "format": "openquote.fields/1", "pack": "care", "type": "referral", "version": 1,
          "label": "Referral", "under": [ "subject" ],
          "fields": [ { "name": "date", "kind": "date", "required": true } ] }
        """;

    [Fact]
    public void A_field_file_may_name_its_entity_type_and_where_entities_of_it_are_kept()
    {
        var content = VaultReader.Read([File("fields/care/referral/v1.json", CareReferral)]);

        Assert.Empty(content.Unreadable);
        var set = Assert.Single(content.Fields);
        Assert.Equal("Referral", set.Label);
        Assert.Equal(["subject"], set.Under);
    }

    [Theory]
    [InlineData("referral", "\"label\": \"\"")]
    [InlineData("referral", "\"label\": 3")]
    [InlineData("referral", "\"under\": []")]
    [InlineData("referral", "\"under\": \"subject\"")]
    [InlineData("referral", "\"under\": [ \"case\" ]")]
    [InlineData("referral", "\"under\": [ \"subject\", \"subject\" ]")]
    [InlineData("subject", "\"under\": [ \"group\" ]")]
    [InlineData("practitioner", "\"under\": [ \"subject\" ]")]
    public void An_invalid_type_label_or_place_is_reported(string type, string key)
    {
        var json = $$"""{ "format": "openquote.fields/1", "pack": "care", "type": "{{type}}", "version": 1, {{key}} }""";

        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File($"fields/care/{type}/v1.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void Lists_the_entity_types_packs_declare_with_their_labels_and_where_they_are_kept()
    {
        var content = VaultReader.Read(
        [
            File("fields/care/session/v1.json", CareSession),
            File("fields/care/referral/v1.json", CareReferral),
            File("fields/care.school/session/v1.json", SchoolSession),
            File("fields/care.school/subject/v1.json", """{ "format": "openquote.fields/0", "pack": "care.school", "type": "subject", "version": 1, "label": "Student" }"""),
        ]);
        var catalog = new FieldCatalog(content.Fields, [Pack("care.school", "care"), Pack("care")]);

        Assert.Equal(["referral", "session", "subject"], catalog.Types);
        Assert.Equal("Referral", catalog.TypeLabel("referral"));
        Assert.Equal("Student", catalog.TypeLabel("subject"));
        Assert.Null(catalog.TypeLabel("session")); // the host names it
        Assert.Equal(["subject"], catalog.KeptUnder("referral"));
        Assert.Equal(["subject", "group"], catalog.KeptUnder("session")); // no pack says: either
        Assert.Equal(["subject", "group"], catalog.KeptUnder("note"));    // a type no pack declares, as the engine writes it
        Assert.Empty(catalog.KeptUnder("subject"));
        Assert.Empty(catalog.KeptUnder("practitioner"));
        Assert.Empty(catalog.Issues);
    }

    [Fact]
    public void A_pack_that_builds_on_another_may_narrow_where_a_type_is_kept_but_not_to_nowhere()
    {
        string Under(string pack, string under) =>
            $$"""{ "format": "openquote.fields/1", "pack": "{{pack}}", "type": "visit", "version": 1, "under": {{under}} }""";
        var narrowed = VaultReader.Read(
        [
            File("fields/care/visit/v1.json", Under("care", """[ "subject", "group" ]""")),
            File("fields/care.school/visit/v1.json", Under("care.school", """[ "group" ]""")),
        ]);
        var apart = VaultReader.Read(
        [
            File("fields/care/visit/v1.json", Under("care", """[ "subject" ]""")),
            File("fields/care.school/visit/v1.json", Under("care.school", """[ "group" ]""")),
        ]);
        var packs = new[] { Pack("care"), Pack("care.school", "care") };

        Assert.Equal(["group"], new FieldCatalog(narrowed.Fields, packs).KeptUnder("visit"));
        var catalog = new FieldCatalog(apart.Fields, packs);
        Assert.Equal(["subject"], catalog.KeptUnder("visit")); // the earlier pack's stands
        var issue = Assert.Single(catalog.Issues);
        Assert.Equal((FieldIssueKind.KeptNowhere, "visit", ""), (issue.Kind, issue.Type, issue.Field));
        Assert.Contains("care.school", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_label_and_field_files_leave_what_is_counted_unchanged()
    {
        VaultFile[] records =
        [
            File("schemes/kind/v1.json", """{ "format": "openquote.scheme/0", "scheme": "kind", "version": 1, "items": [ { "code": "a", "label": "A" } ] }"""),
            File("reports/monthly/v1.json", """
                { "format": "openquote.report/0", "report": "monthly", "version": 1, "label": "Monthly", "counts": "session",
                  "period": { "unit": "month", "field": "date" }, "rows": { "field": "kind", "scheme": "kind", "version": 1 } }
                """),
            File(Json(1, Id(1), "create", fields: new() { ["date"] = "2026-03-02", ["kind"] = new System.Text.Json.Nodes.JsonObject { ["scheme"] = "kind", ["version"] = 1, ["code"] = "a" } })),
        ];
        VaultFile[] layers =
        [
            File("packs/care/v1.json", """{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "Care", "provides": [] }"""),
            File("labels/care/v1.fr.json", """{ "format": "openquote.labels/0", "pack": "care", "version": 1, "locale": "fr", "schemes": { "kind": { "1": { "a": "un" } } } }"""),
            File("fields/care/session/v1.json", CareSession),
        ];

        var without = VaultReader.Read(records);
        var with = VaultReader.Read([.. records, .. layers]);

        Assert.Empty(with.Unreadable);
        Assert.Equal(without.Changes.Select(c => c.Id), with.Changes.Select(c => c.Id));
        Assert.Equal(without.Schemes.Count, with.Schemes.Count);
        Assert.Equal(without.Reports.Count, with.Reports.Count);
        var run = (VaultContent c) => ReportRunner.RunMonth(c.Reports[0], 2026, 3, EntityMerger.Merge(c.Changes).Values, c.Catalog());
        Assert.Equal([("a", 1)], run(with).Cells.Where(x => x.Records.Count > 0).Select(x => (x.Row, x.Records.Count)));
        Assert.Equal(run(without).Cells.Select(x => (x.Row, x.Records.Count)), run(with).Cells.Select(x => (x.Row, x.Records.Count)));
    }

    [Theory]
    [InlineData("fields/Care/session/v1.json", "Care")]     // not a pack id: reported, not ignored
    [InlineData("fields/local/session/v1.json", "local")]   // kept for the vault itself
    [InlineData("fields/oq/session/v1.json", "oq")]
    public void A_field_file_of_a_pack_that_cannot_exist_is_reported(string path, string pack)
    {
        var json = $$"""{ "format": "openquote.fields/0", "pack": "{{pack}}", "type": "session", "version": 1, "fields": [ { "name": "date", "kind": "date" } ] }""";
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File(path, json)]).Unreadable).Reason);
    }
}
