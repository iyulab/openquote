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
}
