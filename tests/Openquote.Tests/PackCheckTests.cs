using Openquote.Packs;

namespace Openquote.Tests;

public class PackCheckTests
{
    private static PackManifest Pack(string id, int version, Dictionary<string, int>? depends = null, params string[] provides) =>
        new(id, version, id, depends ?? new Dictionary<string, int>(), provides);

    [Fact]
    public void Orders_packs_after_the_packs_they_build_on()
    {
        var graph = new PackGraph([
            Pack("care.school.kr", 1, new() { ["care.school"] = 1, ["kr"] = 1 }),
            Pack("kr", 1, new() { ["care"] = 1 }),
            Pack("care.school", 1, new() { ["care"] = 1 }),
            Pack("care", 1),
        ]);

        Assert.Equal(["care", "care.school", "kr", "care.school.kr"], graph.Order());
        Assert.True(graph.DependsOn("care.school.kr", "care"));   // through care.school
        Assert.False(graph.DependsOn("kr", "care.school"));
    }

    [Fact]
    public void Uses_the_highest_version_of_each_pack()
    {
        var graph = new PackGraph([Pack("care", 1), Pack("care", 3), Pack("care", 2)]);
        Assert.Equal(3, graph.Find("care")!.Version);
    }

    [Fact]
    public void A_dependency_cycle_ends_and_is_reported()
    {
        PackManifest[] packs = [Pack("a", 1, new() { ["b"] = 1 }), Pack("b", 1, new() { ["a"] = 1 })];
        var graph = new PackGraph(packs);

        Assert.Equal(["a", "b"], graph.Order());
        Assert.True(graph.DependsOn("a", "a"));
        Assert.Equal([PackIssueKind.DependencyCycle, PackIssueKind.DependencyCycle],
            PackCheck.Check(packs, []).Select(i => i.Kind));
    }

    [Fact]
    public void Reports_missing_and_older_dependencies_missing_files_and_shared_files()
    {
        PackManifest[] packs =
        [
            Pack("care", 1, null, "schemes/care.method/v1.json"),
            Pack("care.school", 1, new() { ["care"] = 2, ["kr"] = 1 }, "schemes/care.method/v1.json", "schemes/school-level/v1.json"),
        ];

        var issues = PackCheck.Check(packs, ["schemes/care.method/v1.json"]);

        Assert.Equal(
            [
                (PackIssueKind.OlderDependency, "care.school"),
                (PackIssueKind.MissingDependency, "care.school"),
                (PackIssueKind.MissingFile, "care.school"),
                (PackIssueKind.SharedFile, "care, care.school"),
            ],
            issues.Select(i => (i.Kind, i.Pack)));
    }

    [Fact]
    public void A_vault_checks_its_packs_against_the_definitions_it_could_read()
    {
        var content = Openquote.Vault.VaultReader.Read([
            TestChanges.File("packs/care/v1.json", """
                { "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "Care",
                  "provides": [ "schemes/care.method/v1.json" ] }
                """),
            TestChanges.File("schemes/care.method/v1.json", """
                { "format": "openquote.scheme/0", "scheme": "care.method", "version": 1, "items": [ { "code": "a", "label": "A" } ] }
                """),
        ]);

        Assert.Empty(content.CheckPacks());
    }
}
