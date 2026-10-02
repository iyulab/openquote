using Openquote.Classification;
using Openquote.Packs;
using Openquote.Suggestions;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class SuggestionCatalogTests
{
    private const string SchoolSuggestions = """
        { "format": "openquote.suggestions/0", "pack": "school", "version": 2,
          "schemes": { "topic": { "1": { "crisis": "confirm", "stress": "off", "housing": "offer" } } } }
        """;

    private static readonly SchemeItem Stress = new("stress", "Stress", null, Suggest: true);
    private static readonly SchemeItem Crisis = new("crisis", "Crisis", null, Suggest: false);

    private static PackManifest Pack(string id, params string[] depends) =>
        new(id, 1, id, depends.ToDictionary(d => d, _ => 1), []);

    private static SuggestionSet Set(string pack, string code, Suggestion suggestion, int version = 1) =>
        new(pack, version, new Dictionary<SchemeItemKey, Suggestion> { [new("topic", 1, code)] = suggestion });

    [Fact]
    public void Reads_a_suggestion_file()
    {
        var content = VaultReader.Read([File("suggestions/school/v2.json", SchoolSuggestions)]);

        Assert.Empty(content.Unreadable);
        var set = Assert.Single(content.Suggestions);
        Assert.Equal(("school", 2), (set.Pack, set.Version));
        Assert.Equal(Suggestion.Confirm, set.Items[new("topic", 1, "crisis")]);
        Assert.Equal(Suggestion.Off, set.Items[new("topic", 1, "stress")]);
        Assert.Equal(Suggestion.Offer, set.Items[new("topic", 1, "housing")]);
        Assert.Equal(new VaultFileKind("suggestions", Name: "school", Version: 2), VaultFileKind.Of("suggestions/school/v2.json"));
    }

    [Theory]
    [InlineData("suggestions/school/v1.json", UnreadableReason.NameMismatch)]
    [InlineData("suggestions/other/v2.json", UnreadableReason.NameMismatch)]
    [InlineData("suggestions/school/v2.json-LAPTOP", UnreadableReason.NameMismatch)]
    public void A_suggestion_file_in_the_wrong_place_is_reported(string path, UnreadableReason reason)
    {
        Assert.Equal(reason, Assert.Single(VaultReader.Read([File(path, SchoolSuggestions)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("""{ "format": "openquote.suggestions/0", "pack": "school", "version": 1, "schemes": { "topic": { "1": { "crisis": "always" } } } }""")]
    [InlineData("""{ "format": "openquote.suggestions/0", "pack": "school", "version": 1, "schemes": { "topic": { "1": { "crisis": true } } } }""")]
    [InlineData("""{ "format": "openquote.suggestions/0", "pack": "school", "version": 1, "schemes": { "topic": { "01": { "crisis": "off" } } } }""")]
    [InlineData("""{ "format": "openquote.suggestions/0", "pack": "local", "version": 1 }""")]
    [InlineData("""{ "format": "openquote.suggestions/0", "pack": "school" }""")]
    public void A_suggestion_file_that_says_something_else_is_unreadable(string json)
    {
        var path = json.Contains("\"local\"", StringComparison.Ordinal) ? "suggestions/local/v1.json" : "suggestions/school/v1.json";
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File(path, json)]).Unreadable).Reason);
    }

    [Fact]
    public void Without_a_packs_word_the_scheme_decides()
    {
        var catalog = new SuggestionCatalog([], []);

        Assert.Equal(Suggestion.Offer, catalog.For("topic", 1, Stress));
        Assert.Equal(Suggestion.Off, catalog.For("topic", 1, Crisis));
    }

    [Fact]
    public void A_packs_word_holds_over_the_schemes_either_way()
    {
        var catalog = new SuggestionCatalog([Set("school", "crisis", Suggestion.Confirm), Set("school", "stress", Suggestion.Off, version: 2)], [Pack("school")]);

        // The highest version of a pack is the pack's word: version 2 no longer says anything about crisis.
        Assert.Equal(Suggestion.Off, catalog.For("topic", 1, Crisis));
        Assert.Equal(Suggestion.Off, catalog.For("topic", 1, Stress));
        Assert.Equal(Suggestion.Confirm, new SuggestionCatalog([Set("school", "crisis", Suggestion.Confirm)], [Pack("school")]).For("topic", 1, Crisis));
        Assert.Equal(Suggestion.Offer, catalog.For("topic", 2, Stress)); // another version of the scheme is another item
    }

    [Fact]
    public void A_pack_that_builds_on_another_has_the_last_word()
    {
        var catalog = new SuggestionCatalog(
            [Set("base", "crisis", Suggestion.Offer), Set("region", "crisis", Suggestion.Confirm)],
            [Pack("base"), Pack("region", "base")]);

        Assert.Equal(Suggestion.Confirm, catalog.For("topic", 1, Crisis));
        Assert.Empty(catalog.Conflicts);
    }

    [Fact]
    public void Unrelated_packs_that_disagree_leave_it_to_the_scheme_and_say_so()
    {
        var catalog = new SuggestionCatalog(
            [Set("east", "crisis", Suggestion.Confirm), Set("west", "crisis", Suggestion.Offer), Set("north", "stress", Suggestion.Off), Set("south", "stress", Suggestion.Off)],
            [Pack("east"), Pack("west"), Pack("north"), Pack("south")]);

        Assert.Equal(Suggestion.Off, catalog.For("topic", 1, Crisis));
        Assert.Equal(Suggestion.Off, catalog.For("topic", 1, Stress)); // they agree
        var conflict = Assert.Single(catalog.Conflicts);
        Assert.Equal(new SchemeItemKey("topic", 1, "crisis"), conflict.Item);
        Assert.Equal(["east", "west"], conflict.Packs);
    }

    private static string Manifest(params string[] provides) =>
        $$"""{ "format": "openquote.pack/0", "pack": "school", "version": 2, "label": "School", "provides": [{{string.Join(", ", provides.Select(p => $"\"{p}\""))}}] }""";

    [Fact]
    public void A_pack_provides_its_suggestion_file_and_the_pack_check_finds_it()
    {
        var held = VaultReader.Read([File("packs/school/v2.json", Manifest("suggestions/school/v2.json")), File("suggestions/school/v2.json", SchoolSuggestions)]);
        var missing = VaultReader.Read([File("packs/school/v2.json", Manifest("suggestions/school/v2.json"))]);

        Assert.Empty(held.Unreadable);
        Assert.Empty(held.CheckPacks());
        Assert.Equal(PackIssueKind.MissingFile, Assert.Single(missing.CheckPacks()).Kind);
    }

    [Fact]
    public void A_manifest_naming_a_kind_of_definition_this_engine_does_not_know_is_read_without_it()
    {
        // A later engine may add a kind of definition in a folder of its own, as suggestions were added; a vault
        // file in such a folder is ignored here, and so is a manifest's line naming one.
        var content = VaultReader.Read([File("packs/school/v2.json", Manifest("guidance/school/v2.json", "suggestions/school/v2.json")), File("suggestions/school/v2.json", SchoolSuggestions)]);

        Assert.Empty(content.Unreadable);
        Assert.Equal(["suggestions/school/v2.json"], Assert.Single(content.Packs).Provides);
        Assert.Empty(content.CheckPacks());
    }

    [Theory]
    [InlineData("schemes/topic.json")]
    [InlineData("packs/other/v1.json")]
    [InlineData("vault.json")]
    [InlineData("guidance/school/v2.txt")]
    public void A_manifest_naming_something_that_is_no_definition_is_unreadable(string provided)
    {
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("packs/school/v2.json", Manifest(provided))]).Unreadable).Reason);
    }
}
