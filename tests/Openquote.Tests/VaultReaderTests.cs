using System.Text.Json.Nodes;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class VaultReaderTests
{
    [Fact]
    public void Reads_a_change_with_every_key()
    {
        var json = Json(1, "e1", "create", fields: new JsonObject { ["title"] = "first", ["note"] = null });
        json["source"] = new JsonObject { ["title"] = "suggestion" };

        var content = VaultReader.Read([File(json)]);

        var change = Assert.Single(content.Changes);
        Assert.Empty(content.Unreadable);
        Assert.Equal(Id(1), change.Id);
        Assert.Equal("dev1", change.Device);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 1, 0, TimeSpan.FromHours(9)), change.At);
        Assert.Equal(new EntityRef("session", "e1"), change.Entity);
        Assert.Equal(ChangeOp.Create, change.Op);
        Assert.Equal("first", change.Fields["title"].GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, change.Fields["note"].ValueKind);
        Assert.Equal("suggestion", change.Source["title"]);
    }

    [Fact]
    public void Reads_changes_under_practitioners_and_subject_folders_only()
    {
        var content = VaultReader.Read(
        [
            File(Json(1, "p1", "create", entityType: "practitioner"), "practitioners"),
            File(Json(2, "e1", "create"), "subjects/s1"),
            File(Json(3, "e2", "create"), "elsewhere"),
            File(Json(4, "e3", "create"), "subjects/s1/nested"),
            File("subjects/s1/readme.txt", "not a change"),
        ]);

        Assert.Equal([Id(1), Id(2)], content.Changes.Select(c => c.Id));
        Assert.Empty(content.Unreadable);
    }

    [Fact]
    public void A_truncated_file_is_reported_and_the_rest_is_still_read()
    {
        var whole = Json(1, "e1", "create").ToJsonString();
        var content = VaultReader.Read(
        [
            File($"subjects/s1/{Id(1)}.dev1.json", whole[..(whole.Length / 2)]),
            File(Json(2, "e1", "update", [1])),
        ]);

        Assert.Equal([Id(2)], content.Changes.Select(c => c.Id));
        var bad = Assert.Single(content.Unreadable);
        Assert.Equal(UnreadableReason.Malformed, bad.Reason);
        Assert.Equal($"subjects/s1/{Id(1)}.dev1.json", bad.Path);
    }

    [Theory]
    [InlineData("openquote.change/1")]
    [InlineData("something-else")]
    public void An_unknown_format_is_reported(string format)
    {
        var json = Json(1, "e1", "create");
        json["format"] = format;

        var bad = Assert.Single(VaultReader.Read([File(json)]).Unreadable);

        Assert.Equal(UnreadableReason.UnknownFormat, bad.Reason);
    }

    [Theory]
    [InlineData("id", "not-a-uuid")]
    [InlineData("id", "01900000-0000-7000-8000-00000000000A")]
    [InlineData("device", "D1")]
    [InlineData("at", "2026-01-01T09:00:00")]
    [InlineData("op", "rename")]
    public void An_invalid_key_is_reported(string key, string value)
    {
        var json = Json(1, "e1", "create");
        json[key] = value;

        var bad = Assert.Single(VaultReader.Read([File(json, name: "whatever")]).Unreadable);

        Assert.Equal(UnreadableReason.Invalid, bad.Reason);
    }

    [Fact]
    public void A_name_that_disagrees_with_the_content_is_reported()
    {
        var json = Json(1, "e1", "create", device: "dev1");

        var bad = Assert.Single(VaultReader.Read([File(json, name: $"{Id(1)}.dev2")]).Unreadable);

        Assert.Equal(UnreadableReason.NameMismatch, bad.Reason);
    }

    [Fact]
    public void A_conflicted_copy_with_the_same_content_is_one_change()
    {
        var json = Json(1, "e1", "create");

        var content = VaultReader.Read(
        [
            File(json),
            File(json, name: $"{Id(1)}.dev1 (conflicted copy 2026-01-02)"),
        ]);

        Assert.Single(content.Changes);
        Assert.Empty(content.Unreadable);
    }

    [Fact]
    public void A_suffix_that_extends_the_device_name_is_a_mismatch()
    {
        var bad = Assert.Single(VaultReader.Read([File(Json(1, "e1", "create"), name: $"{Id(1)}.dev1x")]).Unreadable);

        Assert.Equal(UnreadableReason.NameMismatch, bad.Reason);
    }

    [Fact]
    public void Two_files_with_one_id_and_different_content_are_both_reported()
    {
        var a = Json(1, "e1", "create", fields: new JsonObject { ["title"] = "a" });
        var b = Json(1, "e1", "create", fields: new JsonObject { ["title"] = "b" });

        var content = VaultReader.Read([File(a), File(b, name: $"{Id(1)}.dev1 (2)")]);

        Assert.Empty(content.Changes);
        Assert.Equal(2, content.Unreadable.Count);
        Assert.All(content.Unreadable, u => Assert.Equal(UnreadableReason.DuplicateId, u.Reason));
    }

    [Fact]
    public void Reads_a_vault_folder_on_disk()
    {
        var root = Directory.CreateTempSubdirectory("openquote-test-").FullName;
        try
        {
            var dir = Path.Combine(root, "subjects", "s1");
            Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(Path.Combine(dir, $"{Id(1)}.dev1.json"), Json(1, "e1", "create").ToJsonString());

            var content = VaultReader.Read(VaultFiles.FromDirectory(root));

            Assert.Equal($"subjects/s1/{Id(1)}.dev1.json", Assert.Single(content.Changes).Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reads_a_vault_declared_in_the_format_it_knows()
    {
        var content = VaultReader.Read(
        [
            File("vault.json", """{ "format": "openquote.vault/0", "encryption": "age" }"""),
            File(Json(1, "e1", "create")),
        ]);

        Assert.Single(content.Changes);
        Assert.Empty(content.Unreadable);
    }

    [Fact]
    public void Reads_a_vault_whose_host_left_the_declaration_out()
    {
        Assert.Single(VaultReader.Read([File(Json(1, "e1", "create"))]).Changes);
    }

    [Fact]
    public void Refuses_a_vault_declared_in_a_newer_format()
    {
        var e = Assert.Throws<VaultFormatException>(() => VaultReader.Read(
        [
            File("vault.json", """{"format":"openquote.vault/1","encryption":"age"}"""),
            File(Json(1, "e1", "create")),
        ]));

        Assert.True(e.IsNewer);
        Assert.Equal("openquote.vault/1", e.Declared);
    }

    [Theory]
    [InlineData("""{"format":"something-else/0"}""", "something-else/0")]
    [InlineData("""{"encryption":"age"}""", null)]
    [InlineData("""not json""", null)]
    [InlineData("""{"format":"openquote.vault/x"}""", "openquote.vault/x")]
    public void Refuses_a_declaration_that_names_no_known_format(string declaration, string? declared)
    {
        var e = Assert.Throws<VaultFormatException>(() => VaultReader.Read([File("vault.json", declaration)]));

        Assert.False(e.IsNewer);
        Assert.Equal(declared, e.Declared);
    }
}
