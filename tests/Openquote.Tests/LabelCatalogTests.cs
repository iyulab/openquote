using Openquote.Labels;
using Openquote.Packs;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class LabelCatalogTests
{
    private const string KrLabels = """
        { "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko",
          "schemes": { "care.method": { "1": { "interview": "면담", "phone": "전화" } } },
          "fields": { "session": { "date": "날짜" } } }
        """;

    private static PackManifest Pack(string id, params string[] depends) =>
        new(id, 1, id, depends.ToDictionary(d => d, _ => 1), []);

    private static LabelSet Set(string pack, string locale, string code, string text) =>
        new(pack, 1, locale, new Dictionary<SchemeLabelKey, string> { [new("care.method", 1, code)] = text }, new Dictionary<FieldLabelKey, string>());

    [Fact]
    public void Reads_a_label_file()
    {
        var content = VaultReader.Read([File("labels/kr/v1.ko.json", KrLabels)]);

        Assert.Empty(content.Unreadable);
        var set = Assert.Single(content.Labels);
        Assert.Equal(("kr", 1, "ko"), (set.Pack, set.Version, set.Locale));
        Assert.Equal("면담", set.Schemes[new("care.method", 1, "interview")]);
        Assert.Equal("날짜", set.Fields[new("session", "date")]);
    }

    [Theory]
    [InlineData("labels/kr/v2.ko.json", UnreadableReason.NameMismatch)]
    [InlineData("labels/kr/v1.en.json", UnreadableReason.NameMismatch)]
    [InlineData("labels/kr/v1.ko.json-LAPTOP", UnreadableReason.NameMismatch)]
    public void A_label_file_in_the_wrong_place_is_reported(string path, UnreadableReason reason)
    {
        Assert.Equal(reason, Assert.Single(VaultReader.Read([File(path, KrLabels)]).Unreadable).Reason);
    }

    [Theory]
    [InlineData("""{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "schemes": { "care.method": { "01": { "a": "A" } } } }""")]
    [InlineData("""{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "schemes": { "care.method": { "1": { "a": "" } } } }""")]
    [InlineData("""{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "fields": { "session": { "date": 3 } } }""")]
    public void An_invalid_label_file_is_reported(string json)
    {
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("labels/kr/v1.ko.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void Falls_back_from_a_region_to_its_language_then_to_the_next_locale_asked_for()
    {
        var catalog = new LabelCatalog([Set("kr", "ko", "interview", "면담"), Set("en", "en", "phone", "Phone")], [Pack("kr"), Pack("en")]);

        Assert.Equal("면담", catalog.SchemeLabel("care.method", 1, "interview", ["ko-KR"]));
        Assert.Equal("면담", catalog.SchemeLabel("care.method", 1, "interview", ["KO"]));
        Assert.Equal("Phone", catalog.SchemeLabel("care.method", 1, "phone", ["ko-KR", "en"]));
        Assert.Null(catalog.SchemeLabel("care.method", 1, "crisis", ["ko", "en"])); // the host shows the scheme's own label
    }

    [Fact]
    public void The_pack_that_builds_on_the_others_wins()
    {
        var catalog = new LabelCatalog(
            [Set("kr", "ko", "interview", "면담"), Set("care.school.kr", "ko", "interview", "개인면담")],
            [Pack("kr"), Pack("care.school.kr", "kr")]);

        Assert.Equal("개인면담", catalog.SchemeLabel("care.method", 1, "interview", ["ko"]));
        Assert.Empty(catalog.Conflicts);
    }

    [Fact]
    public void Two_unrelated_packs_that_disagree_are_a_conflict_not_a_silent_choice()
    {
        var catalog = new LabelCatalog(
            [Set("org.a", "ko", "interview", "상담"), Set("org.b", "ko", "interview", "면접")],
            [Pack("org.a"), Pack("org.b")]);

        Assert.Null(catalog.SchemeLabel("care.method", 1, "interview", ["ko"]));
        var conflict = Assert.Single(catalog.Conflicts);
        Assert.Equal(("ko", "care.method v1 interview"), (conflict.Locale, conflict.Target));
        Assert.Equal(["org.a", "org.b"], conflict.Packs);
    }

    [Fact]
    public void Only_the_highest_version_of_a_packs_labels_counts()
    {
        var older = Set("kr", "ko", "interview", "면담");
        var newer = older with { Version = 2, Schemes = new Dictionary<SchemeLabelKey, string> { [new("care.method", 1, "interview")] = "대면 상담" } };

        Assert.Equal("대면 상담", new LabelCatalog([older, newer], [Pack("kr")]).SchemeLabel("care.method", 1, "interview", ["ko"]));
    }
}
