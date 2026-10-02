using Openquote.Classification;

namespace Openquote.Tests;

public class SchemeCatalogTests
{
    private static Scheme Scheme(int version, params string[] codes) =>
        new("kind", version, codes.Select(c => new SchemeItem(c, c.ToUpperInvariant(), null, false)).ToArray());

    private static Crosswalk Links(int from, int to, params (string, string)[] links) => new("kind", from, to, links);

    // v1: a b c d e f g   →   v2: a x y p q m n
    //   a→a (1:1 same code)   b→x, c→x (N:1)   d→y (1:1 new code)
    //   e→p, e→q (1:N)        f→m, g→m, g→n (N:M: f has one link, g has two)
    //   h has no link
    private static SchemeCatalog Catalog() => new(
        [Scheme(1, "a", "b", "c", "d", "e", "f", "g", "h"), Scheme(2, "a", "x", "y", "p", "q", "m", "n")],
        [Links(1, 2, ("a", "a"), ("b", "x"), ("c", "x"), ("d", "y"), ("e", "p"), ("e", "q"), ("f", "m"), ("g", "m"), ("g", "n"))]);

    [Theory]
    [InlineData("a", "a")]
    [InlineData("b", "x")]
    [InlineData("c", "x")]
    [InlineData("d", "y")]
    [InlineData("f", "m")]
    public void An_old_code_with_one_link_is_assigned(string from, string to)
    {
        var r = Catalog().Resolve(new CodedValue("kind", 1, from), 2);

        Assert.Equal(ResolutionKind.Assigned, r.Kind);
        Assert.Equal(to, r.Code);
        Assert.Equal(["1-2"], r.Crosswalks);
    }

    [Theory]
    [InlineData("e", new[] { "p", "q" })]
    [InlineData("g", new[] { "m", "n" })]
    public void An_old_code_with_several_links_waits_for_a_person(string from, string[] candidates)
    {
        var r = Catalog().Resolve(new CodedValue("kind", 1, from), 2);

        Assert.Equal(ResolutionKind.Pending, r.Kind);
        Assert.Null(r.Code);
        Assert.Equal(candidates, r.Candidates);
    }

    [Fact]
    public void An_old_code_with_no_link_is_unmapped()
    {
        var r = Catalog().Resolve(new CodedValue("kind", 1, "h"), 2);

        Assert.Equal(ResolutionKind.Unmapped, r.Kind);
        Assert.Equal(["1-2"], r.Crosswalks);
    }

    [Fact]
    public void A_value_already_in_the_target_version_stays_where_it_is()
    {
        var r = Catalog().Resolve(new CodedValue("kind", 2, "q"), 2);

        Assert.Equal(ResolutionKind.Assigned, r.Kind);
        Assert.Equal("q", r.Code);
        Assert.Empty(r.Crosswalks);
    }

    [Theory]
    [InlineData("kind", 1, "zzz", 2)] // not an item of its own version
    [InlineData("kind", 2, "q", 1)]   // crosswalks never go back
    [InlineData("kind", 1, "a", 3)]   // no such target version
    [InlineData("other", 1, "a", 2)]  // no such scheme
    public void A_value_that_cannot_be_carried_is_unmapped(string scheme, int version, string code, int target)
    {
        Assert.Equal(ResolutionKind.Unmapped, Catalog().Resolve(new CodedValue(scheme, version, code), target).Kind);
    }

    [Fact]
    public void Revisions_compose_and_the_composition_decides()
    {
        // v1 a → v2 a → v3 a1, a2 (split later); v1 b → v2 b → v3 z; v1 c → v2 b (merged) → v3 z
        var catalog = new SchemeCatalog(
            [Scheme(1, "a", "b", "c"), Scheme(2, "a", "b"), Scheme(3, "a1", "a2", "z")],
            [Links(1, 2, ("a", "a"), ("b", "b"), ("c", "b")), Links(2, 3, ("a", "a1"), ("a", "a2"), ("b", "z"))]);

        var a = catalog.Resolve(new CodedValue("kind", 1, "a"), 3);
        var c = catalog.Resolve(new CodedValue("kind", 1, "c"), 3);

        Assert.Equal(ResolutionKind.Pending, a.Kind);
        Assert.Equal(["1-2", "2-3"], a.Crosswalks);
        Assert.Equal(ResolutionKind.Assigned, c.Kind);
        Assert.Equal("z", c.Code);
    }

    [Fact]
    public void A_link_to_a_code_the_next_version_lacks_is_not_a_place()
    {
        var catalog = new SchemeCatalog([Scheme(1, "a", "b"), Scheme(2, "x")], [Links(1, 2, ("a", "x"), ("a", "gone"), ("b", "gone"))]);

        Assert.Equal("x", catalog.Resolve(new CodedValue("kind", 1, "a"), 2).Code);
        Assert.Equal(ResolutionKind.Unmapped, catalog.Resolve(new CodedValue("kind", 1, "b"), 2).Kind);
    }

    [Fact]
    public void The_version_in_force_is_the_highest_one_whose_dates_hold_that_day()
    {
        var catalog = new SchemeCatalog(
            [
                new Scheme("kind", 1, []),                                                   // no dates: in force throughout
                new Scheme("kind", 2, [], new DateOnly(2026, 3, 1)),
                new Scheme("kind", 3, [], new DateOnly(2027, 3, 1), new DateOnly(2027, 12, 31)),
            ],
            []);

        Assert.Equal(1, catalog.InForce("kind", new DateOnly(2025, 12, 1))!.Version);
        Assert.Equal(2, catalog.InForce("kind", new DateOnly(2026, 4, 1))!.Version);
        Assert.Equal(3, catalog.InForce("kind", new DateOnly(2027, 6, 1))!.Version);
        Assert.Equal(2, catalog.InForce("kind", new DateOnly(2028, 1, 1))!.Version);
        Assert.Null(catalog.InForce("other", new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void No_version_in_force_when_every_version_is_dated_elsewhere()
    {
        var catalog = new SchemeCatalog([new Scheme("kind", 1, [], new DateOnly(2026, 3, 1))], []);
        Assert.Null(catalog.InForce("kind", new DateOnly(2026, 2, 28)));
    }

    // v1 → v2 with stated relations: a is the same item, b fits inside x, c is wider than y (y holds
    // only part of it), d was retired (z is only where it went nearest), e has an unstated link.
    private static SchemeCatalog Related() => new(
        [Scheme(1, "a", "b", "c", "d", "e"), Scheme(2, "a", "x", "y", "z", "w")],
        [Links(1, 2, ("a", "a"), ("b", "x"), ("c", "y"), ("d", "z"), ("e", "w")) with
        {
            Relations = new Dictionary<(string From, string To), LinkRelation>
            {
                [("a", "a")] = LinkRelation.Equivalent,
                [("b", "x")] = LinkRelation.Narrower,
                [("c", "y")] = LinkRelation.Broader,
                [("d", "z")] = LinkRelation.Retired,
            },
        }]);

    [Theory]
    [InlineData("a", "a")]
    [InlineData("b", "x")]
    [InlineData("e", "w")]
    public void An_equivalent_a_narrower_or_an_unstated_single_link_is_assigned(string from, string to)
    {
        var r = Related().Resolve(new CodedValue("kind", 1, from), 2);

        Assert.Equal((ResolutionKind.Assigned, to), (r.Kind, r.Code));
    }

    [Fact]
    public void A_link_from_a_broader_item_waits_for_a_person_even_alone()
    {
        var r = Related().Resolve(new CodedValue("kind", 1, "c"), 2);

        Assert.Equal(ResolutionKind.Pending, r.Kind);
        Assert.Equal(["y"], r.Candidates);
    }

    [Fact]
    public void A_retired_link_carries_nothing()
    {
        var r = Related().Resolve(new CodedValue("kind", 1, "d"), 2);

        Assert.Equal(ResolutionKind.Unmapped, r.Kind);
        Assert.Equal(["1-2"], r.Crosswalks);
    }

    // kind v1 → kind v2 as above; kind v2 → "neis" v1 across schemes: a and x are one neis item,
    // y is broader than n2. A local list extends kind v1.
    private static SchemeCatalog Across() => new(
        [
            Scheme(1, "a", "b", "c", "d", "e", "f", "g", "h"), Scheme(2, "a", "x", "y", "p", "q", "m", "n"),
            new Scheme("neis", 1, [new("n1", "N1", null, false), new("n2", "N2", null, false)]),
            new Scheme("kind.local", 1, [new("b-call", "B by phone", null, false) { Anchor = "b" }]) { Extends = new SchemeVersion("kind", 1) },
        ],
        [
            Links(1, 2, ("a", "a"), ("b", "x"), ("c", "x"), ("d", "y")),
            new Crosswalk("kind", 2, 1, [("a", "n1"), ("x", "n1"), ("y", "n2")])
            {
                Into = "neis",
                Relations = new Dictionary<(string From, string To), LinkRelation> { [("y", "n2")] = LinkRelation.Broader },
            },
        ]);

    [Theory]
    [InlineData("kind", 1, "b", "n1")]
    [InlineData("kind", 2, "x", "n1")]
    [InlineData("kind.local", 1, "b-call", "n1")]
    public void A_value_is_carried_into_another_scheme_through_its_own_crosswalks_first(string scheme, int version, string code, string to)
    {
        var catalog = Across();
        var value = new CodedValue(scheme, version, code);

        var r = catalog.Resolve(value, "neis", 1);

        Assert.True(catalog.Reaches(value, "neis", 1));
        Assert.Equal((ResolutionKind.Assigned, to), (r.Kind, r.Code));
        Assert.Contains("kind/2-neis/1", r.Crosswalks);
    }

    [Fact]
    public void Across_schemes_a_broader_item_waits_and_a_scheme_with_no_crosswalks_there_does_not_reach()
    {
        var catalog = Across();

        Assert.Equal(ResolutionKind.Pending, catalog.Resolve(new CodedValue("kind", 1, "d"), "neis", 1).Kind);
        Assert.False(catalog.Reaches(new CodedValue("neis", 1, "n1"), "kind", 2));
        Assert.Equal(ResolutionKind.Unmapped, catalog.Resolve(new CodedValue("neis", 1, "n1"), "kind", 2).Kind);
        Assert.Equal(["1-2", "kind/2-neis/1"], catalog.Resolve(new CodedValue("kind", 1, "a"), "neis", 1).Crosswalks);
    }
}
