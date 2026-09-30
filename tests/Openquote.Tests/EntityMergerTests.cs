using System.Text.Json;
using System.Text.Json.Nodes;
using Openquote.Records;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class EntityMergerTests
{
    private static Entity MergeOne(params JsonObject[] changes)
    {
        var content = VaultReader.Read(changes.Select(c => File(c)));
        Assert.Empty(content.Unreadable);
        return Assert.Single(EntityMerger.Merge(content.Changes)).Value;
    }

    private static JsonObject Set(string field, string value) => new() { [field] = value };

    [Fact]
    public void A_created_entity_has_its_fields()
    {
        var entity = MergeOne(Json(1, "e1", "create", fields: new JsonObject { ["a"] = "1", ["b"] = "2" }));

        Assert.Equal("1", entity.Fields["a"].GetString());
        Assert.Equal("2", entity.Fields["b"].GetString());
        Assert.Empty(entity.Conflicts);
        Assert.False(entity.Destroyed);
    }

    [Fact]
    public void A_change_that_saw_the_earlier_one_replaces_it()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "old")),
            Json(2, "e1", "update", [1], Set("a", "new")));

        Assert.Equal("new", entity.Fields["a"].GetString());
        Assert.Empty(entity.Conflicts);
    }

    [Fact]
    public void Seeing_is_transitive()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "old")),
            Json(2, "e1", "update", [1], Set("b", "x")),
            Json(3, "e1", "update", [2], Set("a", "new")));

        Assert.Equal("new", entity.Fields["a"].GetString());
        Assert.Empty(entity.Conflicts);
    }

    [Fact]
    public void An_older_id_that_saw_a_newer_one_still_wins()
    {
        // Ids order by clock; a device whose clock runs behind can write a smaller id after seeing a larger one.
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "first")),
            Json(5, "e1", "update", [1], Set("a", "second")),
            Json(3, "e1", "update", [5], Set("a", "third")));

        Assert.Equal("third", entity.Fields["a"].GetString());
        Assert.Empty(entity.Conflicts);
    }

    [Fact]
    public void Two_changes_to_one_field_that_did_not_see_each_other_are_both_kept()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "old")),
            Json(2, "e1", "update", [1], Set("a", "left"), device: "dev1"),
            Json(3, "e1", "update", [1], Set("a", "right"), device: "dev2"));

        Assert.Equal("right", entity.Fields["a"].GetString());
        var heads = entity.Conflicts["a"];
        Assert.Equal([Id(2), Id(3)], heads.Select(h => h.ChangeId));
        Assert.Equal(["left", "right"], heads.Select(h => h.Value.GetString()));
        Assert.Equal(["dev1", "dev2"], heads.Select(h => h.Device));
    }

    [Fact]
    public void Concurrent_changes_to_different_fields_do_not_conflict()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: new JsonObject { ["a"] = "0", ["b"] = "0" }),
            Json(2, "e1", "update", [1], Set("a", "1")),
            Json(3, "e1", "update", [1], Set("b", "1")));

        Assert.Equal("1", entity.Fields["a"].GetString());
        Assert.Equal("1", entity.Fields["b"].GetString());
        Assert.Empty(entity.Conflicts);
    }

    [Fact]
    public void A_change_that_saw_both_heads_resolves_the_conflict()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "old")),
            Json(2, "e1", "update", [1], Set("a", "left")),
            Json(3, "e1", "update", [1], Set("a", "right")),
            Json(4, "e1", "update", [2, 3], Set("a", "left")));

        Assert.Equal("left", entity.Fields["a"].GetString());
        Assert.Empty(entity.Conflicts);
    }

    [Fact]
    public void A_base_that_has_not_arrived_yet_is_ignored()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "old")),
            Json(3, "e1", "update", [1, 2], Set("a", "new")));

        Assert.Equal("new", entity.Fields["a"].GetString());
        Assert.Empty(entity.Conflicts);
    }

    [Fact]
    public void Null_clears_a_field_on_purpose()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "old")),
            Json(2, "e1", "update", [1], new JsonObject { ["a"] = null }));

        Assert.Equal(JsonValueKind.Null, entity.Fields["a"].ValueKind);
    }

    [Fact]
    public void A_destroyed_entity_keeps_no_fields()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "old")),
            Json(2, "e1", "destroy", [1]));

        Assert.True(entity.Destroyed);
        Assert.Empty(entity.Fields);
    }

    [Fact]
    public void Entities_are_merged_separately()
    {
        var content = VaultReader.Read(
        [
            File(Json(1, "e1", "create", fields: Set("a", "1"))),
            File(Json(2, "e2", "create", fields: Set("a", "2"))),
            File(Json(3, "e1", "update", [1], Set("a", "3"))),
        ]);

        var merged = EntityMerger.Merge(content.Changes);

        Assert.Equal("3", merged[new EntityRef("session", "e1")].Fields["a"].GetString());
        Assert.Equal("2", merged[new EntityRef("session", "e2")].Fields["a"].GetString());
        Assert.Equal([Id(1), Id(3)], merged[new EntityRef("session", "e1")].Changes.Select(c => c.Id));
    }
    [Fact]
    public void An_entity_with_every_change_names_no_missing_base()
    {
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "1")),
            Json(2, "e1", "update", [1], Set("a", "2")));

        Assert.Empty(entity.MissingBase);
    }

    [Fact]
    public void A_change_whose_base_has_not_arrived_names_it_so_a_conflict_can_be_told_from_a_gap()
    {
        // 1 <- 2 <- 3, and 2 has not reached this device: 3 cannot be seen to have seen 1.
        var entity = MergeOne(
            Json(1, "e1", "create", fields: Set("a", "old")),
            Json(3, "e1", "update", [2], Set("a", "new")));

        Assert.Equal([Id(2)], entity.MissingBase);
        Assert.Equal(2, entity.Conflicts["a"].Count);
    }
}
