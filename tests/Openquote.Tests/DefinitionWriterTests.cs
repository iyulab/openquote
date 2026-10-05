using System.Text.Json;
using Openquote.Classification;
using Openquote.Reports;
using Openquote.Vault;

namespace Openquote.Tests;

public class DefinitionWriterTests
{
    private static readonly Scheme Kind = new("kind", 1, [new("a", "A", null, true), new("b", "B", null, false), new("other", "Other", null, false)]);

    private static Scheme Local(int version, params (string Code, string Label, string Anchor)[] items) =>
        new("kind.local", version, items.Select(i => new SchemeItem(i.Code, i.Label, null, false) { Anchor = i.Anchor }).ToList())
        {
            Extends = new SchemeVersion("kind", 1),
        };

    private static VaultContent Read(params VaultFile[] files)
    {
        var content = VaultReader.Read(files);
        Assert.Empty(content.Unreadable);
        return content;
    }

    [Fact]
    public void A_scheme_written_reads_back_as_it_was()
    {
        var scheme = new Scheme("topic", 2,
            [new("school", "School life", null, true), new("school/attendance", "Attendance", "school", false)],
            new DateOnly(2026, 3, 1), new DateOnly(2027, 2, 28));

        var file = DefinitionWriter.Scheme(scheme);
        var read = Assert.Single(Read(file).Schemes);

        Assert.Equal("schemes/topic/v2.json", file.Path);
        Assert.Equal("openquote.scheme/0", JsonDocument.Parse(file.Content).RootElement.GetProperty("format").GetString());
        Assert.Equal(scheme.Name, read.Name);
        Assert.Equal(scheme.Version, read.Version);
        Assert.Equal(scheme.Items, read.Items);
        Assert.Equal(scheme.EffectiveFrom, read.EffectiveFrom);
        Assert.Equal(scheme.EffectiveTo, read.EffectiveTo);
        Assert.Null(read.Extends);
    }

    [Fact]
    public void A_scheme_extending_another_is_written_in_format_1_with_the_anchor_of_each_item()
    {
        var local = Local(1, ("wisc", "WISC", "other"));

        var file = DefinitionWriter.Scheme(local);
        var content = Read(file);
        var read = Assert.Single(content.Schemes);

        Assert.Equal("openquote.scheme/1", JsonDocument.Parse(file.Content).RootElement.GetProperty("format").GetString());
        Assert.Equal(new SchemeVersion("kind", 1), read.Extends);
        Assert.Equal("other", Assert.Single(read.Items).Anchor);
        Assert.Equal(1, content.RequiredVersion);
    }

    [Fact]
    public void A_crosswalk_written_reads_back_as_it_was()
    {
        var plain = new Crosswalk("kind.local", 1, 2, [("wisc", "wisc"), ("mmpi", "mmpi")]);
        var related = new Crosswalk("kind", 1, 1, [("a", "x")])
        {
            Into = "neis",
            Relations = new Dictionary<(string From, string To), LinkRelation> { [("a", "x")] = LinkRelation.Narrower },
        };

        var plainFile = DefinitionWriter.Crosswalk(plain);
        var relatedFile = DefinitionWriter.Crosswalk(related);
        var content = Read(plainFile, relatedFile);

        Assert.Equal("schemes/kind.local/v1-v2.json", plainFile.Path);
        Assert.Equal("schemes/kind/v1-neis.v1.json", relatedFile.Path);
        var readPlain = Assert.Single(content.Crosswalks, c => c.Into is null);
        Assert.Equal(plain.Links, readPlain.Links);
        Assert.Empty(readPlain.Relations);
        var readRelated = Assert.Single(content.Crosswalks, c => c.Into == "neis");
        Assert.Equal(related.Links, readRelated.Links);
        Assert.Equal(LinkRelation.Narrower, readRelated.Relations[("a", "x")]);
    }

    [Fact]
    public void The_extensions_of_a_scheme_are_the_newest_version_of_each_list_that_extends_it()
    {
        var other = new Scheme("kind.other", 1, [new("c", "C", null, false) { Anchor = "b" }]) { Extends = new SchemeVersion("kind", 1) };
        var catalog = new SchemeCatalog([Kind, Local(1, ("wisc", "WISC", "other")), Local(2, ("wisc", "WISC", "other"), ("mmpi", "MMPI", "other")), other], []);

        var extensions = catalog.ExtensionsOf("kind");

        Assert.Equal(["kind.local", "kind.other"], extensions.Select(s => s.Name));
        Assert.Equal(2, extensions[0].Version);
        Assert.Empty(catalog.ExtensionsOf("kind.local"));
    }

    [Fact]
    public void A_value_of_a_list_kept_beside_a_shared_scheme_is_counted_as_its_anchor_after_the_list_grows()
    {
        // Version 1 of the local list held one item; version 2 adds another, linked to version 1 by an identity crosswalk.
        var v1 = Local(1, ("wisc", "WISC", "other"));
        var v2 = Local(2, ("wisc", "WISC", "other"), ("mmpi", "MMPI", "a"));
        var identity = new Crosswalk("kind.local", 1, 2, [("wisc", "wisc")]);
        var catalog = Read(DefinitionWriter.Scheme(Kind), DefinitionWriter.Scheme(v1), DefinitionWriter.Scheme(v2), DefinitionWriter.Crosswalk(identity)).Catalog();

        Assert.Equal("other", catalog.Resolve(new CodedValue("kind.local", 1, "wisc"), "kind", 1).Code);
        Assert.Equal("a", catalog.Resolve(new CodedValue("kind.local", 2, "mmpi"), "kind", 1).Code);
        Assert.Equal("wisc", catalog.Resolve(new CodedValue("kind.local", 1, "wisc"), "kind.local", 2).Code);
    }
}
