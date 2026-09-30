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

    [Fact]
    public void A_label_file_with_a_malformed_locale_in_its_path_is_reported_even_when_the_file_says_something_else()
    {
        Assert.Equal(UnreadableReason.NameMismatch, Assert.Single(VaultReader.Read([File("labels/region-a/v1.fr_CA.json", RegionLabels)]).Unreadable).Reason);
    }
}
