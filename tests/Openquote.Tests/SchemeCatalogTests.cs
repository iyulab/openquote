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
}
