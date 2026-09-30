using Openquote.Labels;
using Openquote.Packs;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class LabelCatalogTests
{
    private const string RegionLabels = """
        { "format": "openquote.labels/0", "pack": "region-a", "version": 1, "locale": "fr",
          "schemes": { "care.topic": { "1": { "sleep": "sommeil", "school": "ecole" } } },
          "fields": { "session": { "date": "jour" } } }
        """;

    private static PackManifest Pack(string id, params string[] depends) =>
        new(id, 1, id, depends.ToDictionary(d => d, _ => 1), []);

    private static LabelSet Set(string pack, string locale, string code, string text) =>
        new(pack, 1, locale, new Dictionary<SchemeLabelKey, string> { [new("care.topic", 1, code)] = text }, new Dictionary<FieldLabelKey, string>());

    [Fact]
    public void Reads_a_label_file()
    {
        var content = VaultReader.Read([File("labels/region-a/v1.fr.json", RegionLabels)]);

        Assert.Empty(content.Unreadable);
        var set = Assert.Single(content.Labels);
        Assert.Equal(("region-a", 1, "fr"), (set.Pack, set.Version, set.Locale));
        Assert.Equal("sommeil", set.Schemes[new("care.topic", 1, "sleep")]);
        Assert.Equal("jour", set.Fields[new("session", "date")]);
    }

    [Theory]
    [InlineData("labels/region-a/v2.fr.json", UnreadableReason.NameMismatch)]
    [InlineData("labels/region-a/v1.en.json", UnreadableReason.NameMismatch)]
    [InlineData("labels/region-a/v1.fr.json-LAPTOP", UnreadableReason.NameMismatch)]
    public void A_label_file_in_the_wrong_place_is_reported(string path, UnreadableReason reason)
    {
        Assert.Equal(reason, Assert.Single(VaultReader.Read([File(path, RegionLabels)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("""{ "format": "openquote.labels/0", "pack": "region-a", "version": 1, "locale": "fr", "schemes": { "care.topic": { "01": { "a": "A" } } } }""")]
    [InlineData("""{ "format": "openquote.labels/0", "pack": "region-a", "version": 1, "locale": "fr", "schemes": { "care.topic": { "1": { "a": "" } } } }""")]
    [InlineData("""{ "format": "openquote.labels/0", "pack": "region-a", "version": 1, "locale": "fr", "fields": { "session": { "date": 3 } } }""")]
    public void An_invalid_label_file_is_reported(string json)
    {
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("labels/region-a/v1.fr.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void Falls_back_from_a_region_to_its_language_then_to_the_next_locale_asked_for()
    {
        var catalog = new LabelCatalog([Set("region-a", "fr", "sleep", "sommeil"), Set("region-b", "en", "school", "School")], [Pack("region-a"), Pack("region-b")]);

        Assert.Equal("sommeil", catalog.SchemeLabel("care.topic", 1, "sleep", ["fr-CA"]));
        Assert.Equal("sommeil", catalog.SchemeLabel("care.topic", 1, "sleep", ["FR"]));
        Assert.Equal("School", catalog.SchemeLabel("care.topic", 1, "school", ["fr-CA", "en"]));
        Assert.Null(catalog.SchemeLabel("care.topic", 1, "exam", ["fr", "en"])); // the host shows the scheme's own label
    }

    [Fact]
    public void The_pack_that_builds_on_the_others_wins()
    {
        var catalog = new LabelCatalog(
            [Set("region-a", "fr", "sleep", "sommeil"), Set("care.school.region-a", "fr", "sleep", "sommeil de l'eleve")],
            [Pack("region-a"), Pack("care.school.region-a", "region-a")]);

        Assert.Equal("sommeil de l'eleve", catalog.SchemeLabel("care.topic", 1, "sleep", ["fr"]));
        Assert.Empty(catalog.Conflicts);
    }

    [Fact]
    public void Two_unrelated_packs_that_disagree_are_a_conflict_not_a_silent_choice()
    {
        var catalog = new LabelCatalog(
            [Set("org.a", "fr", "sleep", "repos"), Set("org.b", "fr", "sleep", "nuit")],
            [Pack("org.a"), Pack("org.b")]);

        Assert.Null(catalog.SchemeLabel("care.topic", 1, "sleep", ["fr"]));
        var conflict = Assert.Single(catalog.Conflicts);
        Assert.Equal(("fr", "care.topic v1 sleep"), (conflict.Locale, conflict.Target));
        Assert.Equal(["org.a", "org.b"], conflict.Packs);
    }

    [Fact]
    public void Only_the_highest_version_of_a_packs_labels_counts()
    {
        var older = Set("region-a", "fr", "sleep", "sommeil");
        var newer = older with { Version = 2, Schemes = new Dictionary<SchemeLabelKey, string> { [new("care.topic", 1, "sleep")] = "sommeil profond" } };

        Assert.Equal("sommeil profond", new LabelCatalog([older, newer], [Pack("region-a")]).SchemeLabel("care.topic", 1, "sleep", ["fr"]));
    }

    [Fact]
    public void Packs_that_build_on_the_same_pack_and_agree_settle_it_even_though_neither_builds_on_the_other()
    {
        var catalog = new LabelCatalog(
            [Set("base", "fr", "sleep", "x"), Set("org.a", "fr", "sleep", "y"), Set("org.b", "fr", "sleep", "y")],
            [Pack("base"), Pack("org.a", "base"), Pack("org.b", "base")]);

        Assert.Equal("y", catalog.SchemeLabel("care.topic", 1, "sleep", ["fr"]));
        Assert.Empty(catalog.Conflicts);
    }

    [Fact]
    public void Packs_that_build_on_the_same_pack_and_disagree_are_a_conflict_even_though_the_base_differs_from_both()
    {
        var catalog = new LabelCatalog(
            [Set("base", "fr", "sleep", "x"), Set("org.a", "fr", "sleep", "y"), Set("org.b", "fr", "sleep", "z")],
            [Pack("base"), Pack("org.a", "base"), Pack("org.b", "base")]);

        Assert.Null(catalog.SchemeLabel("care.topic", 1, "sleep", ["fr"]));
        Assert.Equal("care.topic v1 sleep", Assert.Single(catalog.Conflicts).Target);
    }

    [Fact]
    public void Precedence_follows_a_chain_of_packs_that_build_on_each_other()
    {
        var catalog = new LabelCatalog(
            [Set("a", "fr", "sleep", "one"), Set("b", "fr", "sleep", "two"), Set("c", "fr", "sleep", "three")],
            [Pack("a"), Pack("b", "a"), Pack("c", "b")]);

        Assert.Equal("three", catalog.SchemeLabel("care.topic", 1, "sleep", ["fr"]));
        Assert.Empty(catalog.Conflicts);
    }

    [Fact]
    public void Names_that_render_alike_are_still_different_things()
    {
        LabelSet Field(string pack, string type, string field, string text) =>
            new(pack, 1, "fr", new Dictionary<SchemeLabelKey, string>(), new Dictionary<FieldLabelKey, string> { [new(type, field)] = text });

        var catalog = new LabelCatalog(
            [Field("org.a", "a.b", "c", "one"), Field("org.b", "a", "b.c", "two")],
            [Pack("org.a"), Pack("org.b")]);

        Assert.Empty(catalog.Conflicts);
        Assert.Equal("one", catalog.FieldLabel("a.b", "c", ["fr"]));
        Assert.Equal("two", catalog.FieldLabel("a", "b.c", ["fr"]));
    }

    [Fact]
    public void A_field_label_conflict_names_the_type_and_the_field()
    {
        LabelSet Field(string pack, string text) =>
            new(pack, 1, "fr", new Dictionary<SchemeLabelKey, string>(), new Dictionary<FieldLabelKey, string> { [new("session", "date")] = text });

        var catalog = new LabelCatalog([Field("org.a", "jour"), Field("org.b", "date")], [Pack("org.a"), Pack("org.b")]);

        Assert.Equal("session.date", Assert.Single(catalog.Conflicts).Target);
    }

    [Theory]
    [InlineData("labels/region-a/v1.fr_CA.json", "region-a", "fr_CA")]
    [InlineData("labels/region-a/v1.f.json", "region-a", "f")]
    [InlineData("labels/Region-A/v1.fr.json", "Region-A", "fr")]
    [InlineData("labels/local/v1.fr.json", "local", "fr")]   // kept for the vault itself
    [InlineData("labels/oq/v1.fr.json", "oq", "fr")]
    public void A_label_file_with_a_pack_or_locale_that_is_not_valid_is_reported(string path, string pack, string locale)
    {
        var json = $$"""{ "format": "openquote.labels/0", "pack": "{{pack}}", "version": 1, "locale": "{{locale}}", "schemes": { "care.topic": { "1": { "a": "A" } } } }""";
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File(path, json)]).Unreadable).Reason);
    }

    private const string FormLabels = """
        { "format": "openquote.labels/0", "pack": "region-a", "version": 1, "locale": "fr",
          "fields":  { "subject": { "name": "nom" } },
          "aliases": { "subject": { "name": [ "nom de famille", "eleve" ] } },
          "reports": { "care.monthly-concern": { "1": "Seances par sujet" } },
          "exports": { "care.session-list": { "1": { "label": "Liste des seances", "columns": { "0": "Jour", "3": "Sujet" } } } } }
        """;

    private static LabelSet Forms(string pack, string report, string[] aliases, string? column = null) =>
        new(pack, 1, "fr", new Dictionary<SchemeLabelKey, string>(), new Dictionary<FieldLabelKey, string>())
        {
            Reports = new Dictionary<FormLabelKey, string> { [new("care.monthly-concern", 1)] = report },
            Exports = column is null
                ? new Dictionary<FormLabelKey, ExportLabels>()
                : new Dictionary<FormLabelKey, ExportLabels> { [new("care.session-list", 1)] = new(null, new Dictionary<int, string> { [0] = column }) },
            Aliases = new Dictionary<FieldLabelKey, IReadOnlyList<string>> { [new("subject", "name")] = aliases },
        };

    [Fact]
    public void Reads_form_labels_and_field_aliases()
    {
        var content = VaultReader.Read([File("labels/region-a/v1.fr.json", FormLabels)]);

        Assert.Empty(content.Unreadable);
        var catalog = new LabelCatalog(content.Labels, [Pack("region-a")]);
        Assert.Equal("Seances par sujet", catalog.ReportLabel("care.monthly-concern", 1, ["fr"]));
        Assert.Equal("Liste des seances", catalog.ExportLabel("care.session-list", 1, ["fr"]));
        Assert.Equal("Jour", catalog.ExportColumnLabel("care.session-list", 1, 0, ["fr"]));
        Assert.Equal("Sujet", catalog.ExportColumnLabel("care.session-list", 1, 3, ["fr"]));
        Assert.Null(catalog.ExportColumnLabel("care.session-list", 1, 1, ["fr"])); // the form's own heading stands
        Assert.Null(catalog.ReportLabel("care.monthly-concern", 2, ["fr"]));
        Assert.Equal(["nom de famille", "eleve"], catalog.FieldAliases("subject", "name", ["fr"]));
        Assert.Empty(catalog.FieldAliases("subject", "name", ["de"]));
    }

    [Fact]
    public void A_labels_file_without_the_new_sections_reads_as_before()
    {
        var set = Assert.Single(VaultReader.Read([File("labels/region-a/v1.fr.json", RegionLabels)]).Labels);

        Assert.Empty(set.Reports);
        Assert.Empty(set.Exports);
        Assert.Empty(set.Aliases);
        Assert.Empty(new LabelCatalog([set], [Pack("region-a")]).FieldAliases("session", "date", ["fr"]));
    }

    [Fact]
    public void Aliases_from_packs_that_do_not_build_on_each_other_are_combined()
    {
        var catalog = new LabelCatalog(
            [Forms("org.a", "x", ["nom", "eleve"]), Forms("org.b", "x", ["eleve", "prenom"])],
            [Pack("org.a"), Pack("org.b")]);

        Assert.Equal(["nom", "eleve", "prenom"], catalog.FieldAliases("subject", "name", ["fr"]));
        Assert.Empty(catalog.Conflicts); // aliases add up; they never disagree
    }

    [Fact]
    public void Aliases_come_from_the_first_locale_that_has_any()
    {
        var french = Forms("region-a", "x", ["nom"]);
        var english = french with { Pack = "region-b", Locale = "en", Aliases = new Dictionary<FieldLabelKey, IReadOnlyList<string>> { [new("subject", "name")] = ["surname"] } };
        var catalog = new LabelCatalog([french, english], [Pack("region-a"), Pack("region-b")]);

        Assert.Equal(["surname"], catalog.FieldAliases("subject", "name", ["en-GB", "fr"]));
    }

    [Fact]
    public void A_report_label_follows_the_pack_that_builds_on_the_others()
    {
        var catalog = new LabelCatalog(
            [Forms("region-a", "Seances", []), Forms("care.school.region-a", "Seances des eleves", [])],
            [Pack("region-a"), Pack("care.school.region-a", "region-a")]);

        Assert.Equal("Seances des eleves", catalog.ReportLabel("care.monthly-concern", 1, ["fr"]));
        Assert.Empty(catalog.Conflicts);
    }

    [Fact]
    public void Unrelated_packs_that_name_a_form_or_a_column_differently_are_conflicts()
    {
        var catalog = new LabelCatalog(
            [Forms("org.a", "Seances", [], "Jour"), Forms("org.b", "Rencontres", [], "Date")],
            [Pack("org.a"), Pack("org.b")]);

        Assert.Null(catalog.ReportLabel("care.monthly-concern", 1, ["fr"]));
        Assert.Null(catalog.ExportColumnLabel("care.session-list", 1, 0, ["fr"]));
        Assert.Equal(
            ["export care.session-list v1 column 0", "report care.monthly-concern v1"],
            catalog.Conflicts.Select(c => c.Target).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_export_column_label_names_a_column_by_its_index()
    {
        var catalog = new LabelCatalog([Forms("region-a", "x", [], "Jour")], [Pack("region-a")]);

        Assert.Equal("Jour", catalog.ExportColumnLabel("care.session-list", 1, 0, ["fr"]));
        Assert.Null(catalog.ExportColumnLabel("care.session-list", 1, 3, ["fr"]));
        Assert.Null(catalog.ExportLabel("care.session-list", 1, ["fr"])); // a column label alone leaves the form's name as it is
    }

    [Theory]
    [InlineData("""{ "aliases": { "subject": { "name": [] } } }""")]
    [InlineData("""{ "aliases": { "subject": { "name": [ "" ] } } }""")]
    [InlineData("""{ "aliases": { "subject": { "name": "nom" } } }""")]
    [InlineData("""{ "reports": { "monthly": { "01": "M" } } }""")]
    [InlineData("""{ "reports": { "monthly": { "1": "" } } }""")]
    [InlineData("""{ "exports": { "list": { "1": {} } } }""")]
    [InlineData("""{ "exports": { "list": { "1": { "columns": {} } } } }""")]
    [InlineData("""{ "exports": { "list": { "1": { "label": "" } } } }""")]
    [InlineData("""{ "exports": { "list": { "1": { "columns": { "-1": "A" } } } } }""")]
    [InlineData("""{ "exports": { "list": { "1": { "columns": { "01": "A" } } } } }""")]
    [InlineData("""{ "exports": { "list": { "1": { "columns": { "0": "" } } } } }""")]
    public void An_invalid_form_label_or_alias_list_is_reported(string sections)
    {
        var json = """{ "format": "openquote.labels/0", "pack": "region-a", "version": 1, "locale": "fr", """ + sections[1..];
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("labels/region-a/v1.fr.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void A_label_file_with_a_malformed_locale_in_its_path_is_reported_even_when_the_file_says_something_else()
    {
        Assert.Equal(UnreadableReason.NameMismatch, Assert.Single(VaultReader.Read([File("labels/region-a/v1.fr_CA.json", RegionLabels)]).Unreadable).Reason);
    }
}
