using System.Text.Json.Nodes;
using Openquote.Classification;
using Openquote.Records;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class ClassifyTests
{
    // v1 c splits into v2 p and q; v1 a becomes v2 x; v2 r is new.
    private static readonly SchemeCatalog Catalog = new(
        [
            new Scheme("kind", 1, [new("a", "A", null, false), new("c", "C", null, false)]),
            new Scheme("kind", 2, [new("x", "X", null, false), new("p", "P", null, false), new("q", "Q", null, false), new("r", "R", null, false)]),
            new Scheme("other", 2, [new("p", "P", null, false)]),
        ],
        [new Crosswalk("kind", 1, 2, [("a", "x"), ("c", "p"), ("c", "q")])]);

    private static JsonObject Coded(int version, string code, string scheme = "kind") =>
        new() { ["scheme"] = scheme, ["version"] = version, ["code"] = code };

    private static Entity Merge(params JsonObject[] changes) =>
        Assert.Single(EntityMerger.Merge(VaultReader.Read(changes.Select(c => File(c))).Changes)).Value;

    [Fact]
    public void A_split_value_waits_with_the_codes_a_person_chooses_from()
    {
        var entity = Merge(Json(1, "e1", "create", fields: new JsonObject { ["kind"] = Coded(1, "c") }));

        var resolution = entity.Classify("kind", "kind", 2, Catalog);

        Assert.Equal(ResolutionKind.Pending, resolution.Kind);
        Assert.Equal(["p", "q"], resolution.Candidates);
        Assert.Equal(["1-2"], resolution.Crosswalks);
    }

    [Fact]
    public void A_value_with_one_link_is_placed_and_the_version_it_was_entered_in_still_answers()
    {
        var entity = Merge(Json(1, "e1", "create", fields: new JsonObject { ["kind"] = Coded(1, "a") }));

        Assert.Equal("x", entity.Classify("kind", "kind", 2, Catalog).Code);
        Assert.Equal("a", entity.Classify("kind", "kind", 1, Catalog).Code);
    }

    [Fact]
    public void A_choice_in_the_newer_version_places_the_record_there_without_hiding_the_older_value()
    {
        var entity = Merge(
            Json(1, "e1", "create", fields: new JsonObject { ["kind"] = Coded(1, "c") }),
            Json(2, "e1", "reclassify", [1], new JsonObject { ["kind"] = Coded(2, "q") }));

        Assert.Equal("q", entity.Classify("kind", "kind", 2, Catalog).Code);
        Assert.Equal("c", entity.Classify("kind", "kind", 1, Catalog).Code);
    }

    [Fact]
    public void A_missing_field_a_value_of_another_scheme_or_a_destroyed_record_is_unmapped()
    {
        var plain = Merge(Json(1, "e1", "create", fields: new JsonObject { ["kind"] = "c" }));
        var other = Merge(Json(1, "e1", "create", fields: new JsonObject { ["kind"] = Coded(2, "p", "other") }));
        var destroyed = Merge(
            Json(1, "e1", "create", fields: new JsonObject { ["kind"] = Coded(1, "c") }),
            Json(2, "e1", "destroy", [1]));

        Assert.Equal(ResolutionKind.Unmapped, plain.Classify("kind", "kind", 2, Catalog).Kind);
        Assert.Equal(ResolutionKind.Unmapped, other.Classify("kind", "kind", 2, Catalog).Kind);
        Assert.Equal(ResolutionKind.Unmapped, destroyed.Classify("kind", "kind", 2, Catalog).Kind);
        Assert.Equal(ResolutionKind.Unmapped, plain.Classify("absent", "kind", 2, Catalog).Kind);
    }
}
