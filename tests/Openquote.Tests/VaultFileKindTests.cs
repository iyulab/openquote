using Openquote.Vault;

namespace Openquote.Tests;

public class VaultFileKindTests
{
    public static TheoryData<string, VaultFileKind> Paths => new()
    {
        { "subjects/s1/0190.dev1.json", new("subject", Id: "s1") },
        { "subjects/s1/0190.dev1.json (conflicted copy)", new("subject", Id: "s1") },
        { "groups/g1/0190.dev1.json", new("group", Id: "g1") },
        { "practitioners/0190.dev1.json", new("practitioners") },
        { "devices/0190.dev1.json", new("devices") },
        { "schemes/topic/v2.json", new("scheme", Name: "topic", Version: 2) },
        { "schemes/topic/v2 (conflicted copy).json", new("scheme", Name: "topic", Version: 2) },
        { "schemes/topic/v1-v2.json", new("crosswalk", Name: "topic", From: 1, To: 2) },
        { "schemes/topic/v1-v2.json.sync-conflict-20260101", new("crosswalk", Name: "topic", From: 1, To: 2) },
        { "reports/monthly/v1.json", new("report", Name: "monthly", Version: 1) },
        { "exports/session-list/v3.json", new("export", Name: "session-list", Version: 3) },
        { "runs/2026/0190.dev1.json", new("run", Year: 2026) },
        { "packs/care.school/v2.json", new("pack", Name: "care.school", Version: 2) },
        { "vault.json", new("other") },
        { "packs/care.school/latest.json", new("other") },
        { "schemes/topic/latest.json", new("other") },
        { "schemes/topic/v2-draft.json", new("other") },
        { "reports/monthly/v1-v2.json", new("other") },
        { "runs/26/0190.dev1.json", new("other") },
        { "subjects/s1/nested/0190.dev1.json", new("other") },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public void Reads_what_a_path_holds_from_the_layout(string path, VaultFileKind expected) =>
        Assert.Equal(expected, VaultFileKind.Of(path));

    [Fact]
    public void An_unreadable_file_carries_what_it_was_for()
    {
        var content = VaultReader.Read([new VaultFile("schemes/topic/v2.json", "{ cut off"u8.ToArray())]);

        var unreadable = Assert.Single(content.Unreadable);
        Assert.Equal(new VaultFileKind("scheme", Name: "topic", Version: 2), unreadable.Kind);
    }
}
