using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class PackManifestTests
{
    private const string SchoolV1 = """
        { "format": "openquote.pack/0", "pack": "care.school", "version": 1, "label": "School counseling",
          "depends": { "care": 1 }, "provides": [ "schemes/school-level/v1.json" ] }
        """;

    [Fact]
    public void Reads_a_pack_manifest()
    {
        var content = VaultReader.Read([File("packs/care.school/v1.json", SchoolV1)]);

        Assert.Empty(content.Unreadable);
        var pack = Assert.Single(content.Packs);
        Assert.Equal(("care.school", 1, "School counseling"), (pack.Id, pack.Version, pack.Label));
        Assert.Equal(1, pack.Depends["care"]);
        Assert.Equal(["schemes/school-level/v1.json"], pack.Provides);
    }

    [Theory]
    [InlineData("packs/care.school/v2.json")]
    [InlineData("packs/care/v1.json")]
    [InlineData("packs/care.school/v1.json-LAPTOP")] // a sync client's copy is listed, not ignored
    public void A_manifest_in_the_wrong_place_is_reported(string path)
    {
        Assert.Equal(UnreadableReason.NameMismatch, Assert.Single(VaultReader.Read([File(path, SchoolV1)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("""{ "format": "openquote.pack/0", "pack": "local", "version": 1, "label": "L", "provides": [] }""", "packs/local/v1.json")]
    [InlineData("""{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "C", "depends": { "care": 1 }, "provides": [] }""", "packs/care/v1.json")]
    [InlineData("""{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "C", "depends": { "kr": 0 }, "provides": [] }""", "packs/care/v1.json")]
    [InlineData("""{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "C", "provides": [ "subjects/s1/x.json" ] }""", "packs/care/v1.json")]
    [InlineData("""{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "C" }""", "packs/care/v1.json")]
    public void An_invalid_manifest_is_reported(string json, string path)
    {
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File(path, json)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("care", true)]
    [InlineData("care.school.kr", true)]
    [InlineData("org-x.y2", true)]
    [InlineData("Care", false)]
    [InlineData("care..school", false)]
    [InlineData(".care", false)]
    public void Pack_ids_are_lowercase_dotted_words(string id, bool valid) =>
        Assert.Equal(valid, Openquote.Packs.PackManifest.IsId(id));
}
