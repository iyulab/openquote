using Openquote.Classification;
using Openquote.Packs;

namespace Openquote.Suggestions;

/// <summary>Two packs that do not build on each other say different things about suggesting one item.</summary>
public sealed record SuggestionConflict(SchemeItemKey Item, IReadOnlyList<string> Packs);

/// <summary>
/// Whether each scheme item may be suggested, as the scheme says it (<see cref="SchemeItem.Suggest"/>) and as a
/// vault's packs say it over the scheme. Resolved like labels: among packs that say something about an item, a
/// pack that another of them builds on is set aside; when the packs left say the same it holds, and when they
/// disagree it is a <see cref="SuggestionConflict"/> and the scheme's own word holds.
/// </summary>
public sealed class SuggestionCatalog
{
    private readonly List<SuggestionSet> _sets;
    private readonly PackGraph _graph;

    /// <summary>Builds the catalog from a vault's suggestion files and pack manifests.</summary>
    public SuggestionCatalog(IEnumerable<SuggestionSet> sets, IEnumerable<PackManifest> packs)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(packs);
        _graph = new PackGraph(packs);
        _sets = [.. sets.GroupBy(s => s.Pack, StringComparer.Ordinal).Select(g => g.MaxBy(s => s.Version)!)];
        Conflicts = FindConflicts();
    }

    /// <summary>Items two unrelated packs say different things about.</summary>
    public IReadOnlyList<SuggestionConflict> Conflicts { get; }

    /// <summary>
    /// Whether <paramref name="item"/> of version <paramref name="version"/> of <paramref name="scheme"/> may be
    /// suggested: what the vault's packs say, else what the scheme says (<see cref="Suggestion.Offer"/> for an
    /// item marked <c>suggest</c>, <see cref="Suggestion.Off"/> otherwise).
    /// </summary>
    public Suggestion For(string scheme, int version, SchemeItem item)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(item);
        var said = Said(new SchemeItemKey(scheme, version, item.Code));
        return said.Count > 0 && Winner(said) is { } suggestion ? suggestion
            : item.Suggest ? Suggestion.Offer
            : Suggestion.Off;
    }

    private List<(string Pack, Suggestion Suggestion)> Said(SchemeItemKey key) =>
        [.. _sets.Where(s => s.Items.ContainsKey(key)).Select(s => (s.Pack, s.Items[key]))];

    // Packs that build on another that says something take its place, so only the maximal ones decide.
    private Suggestion? Winner(List<(string Pack, Suggestion Suggestion)> said)
    {
        var maximal = said.Where(s => !said.Any(o => o.Pack != s.Pack && _graph.DependsOn(o.Pack, s.Pack))).ToList();
        if (maximal.Count == 0) maximal = said; // packs caught in a cycle: the pack check reports it
        var distinct = maximal.Select(m => m.Suggestion).Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private List<SuggestionConflict> FindConflicts() =>
        [.. _sets.SelectMany(s => s.Items.Keys).Distinct()
            .OrderBy(k => k.Scheme, StringComparer.Ordinal).ThenBy(k => k.Version).ThenBy(k => k.Code, StringComparer.Ordinal)
            .Select(key => (Key: key, Said: Said(key)))
            .Where(x => Winner(x.Said) is null)
            .Select(x => new SuggestionConflict(x.Key, [.. x.Said.Select(s => s.Pack).Order(StringComparer.Ordinal)]))];
}
