using Openquote.Packs;

namespace Openquote.Labels;

/// <summary>Two packs that do not build on each other label the same thing differently in one locale.</summary>
public sealed record LabelConflict(string Locale, string Target, IReadOnlyList<string> Packs);

/// <summary>
/// The labels a vault's packs give, resolved per locale. Among packs labelling the same thing, a pack that
/// another of them builds on is set aside; when the packs left give one text it wins, and when they
/// disagree it is a <see cref="LabelConflict"/> and none of their labels is used for that locale.
/// </summary>
public sealed class LabelCatalog
{
    private readonly List<LabelSet> _sets;
    private readonly PackGraph _graph;

    /// <summary>Builds the catalog from a vault's label files and pack manifests.</summary>
    public LabelCatalog(IEnumerable<LabelSet> sets, IEnumerable<PackManifest> packs)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(packs);
        _graph = new PackGraph(packs);
        _sets = sets
            .GroupBy(s => (s.Pack, Locale: s.Locale.ToLowerInvariant()))
            .Select(g => g.MaxBy(s => s.Version)!)
            .ToList();
        Conflicts = FindConflicts();
    }

    /// <summary>Things two unrelated packs label differently, per locale.</summary>
    public IReadOnlyList<LabelConflict> Conflicts { get; }

    /// <summary>
    /// The label of <paramref name="code"/> in version <paramref name="version"/> of <paramref name="scheme"/>
    /// for the first of <paramref name="locales"/> that has one (<c>ko-KR</c> falls back to <c>ko</c>), or
    /// null — the host then shows the scheme item's own label.
    /// </summary>
    public string? SchemeLabel(string scheme, int version, string code, IReadOnlyList<string> locales) =>
        Pick(locales, s => s.Schemes.GetValueOrDefault(new SchemeLabelKey(scheme, version, code)));

    /// <summary>The label of <paramref name="field"/> of <paramref name="type"/>, resolved like <see cref="SchemeLabel"/>.</summary>
    public string? FieldLabel(string type, string field, IReadOnlyList<string> locales) =>
        Pick(locales, s => s.Fields.GetValueOrDefault(new FieldLabelKey(type, field)));

    private string? Pick(IReadOnlyList<string> locales, Func<LabelSet, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(locales);
        foreach (var locale in Expand(locales))
        {
            var found = _sets
                .Where(s => string.Equals(s.Locale, locale, StringComparison.OrdinalIgnoreCase))
                .Select(s => (s.Pack, Text: lookup(s)))
                .Where(x => x.Text is not null)
                .Select(x => (x.Pack, Text: x.Text!))
                .ToList();
            if (found.Count > 0 && Winner(found) is { } text) return text;
        }
        return null;
    }

    // Packs that build on an offerer take its place, so only the maximal offerers — those no other offerer builds on —
    // decide; when they all give the same text, that text wins, otherwise there is no winner.
    private string? Winner(List<(string Pack, string Text)> found)
    {
        var maximal = found.Where(f => !found.Any(o => o.Pack != f.Pack && _graph.DependsOn(o.Pack, f.Pack))).ToList();
        if (maximal.Count == 0) maximal = found; // packs caught in a cycle: PackCheck reports it, nothing builds on nothing
        var texts = maximal.Select(m => m.Text).Distinct(StringComparer.Ordinal).ToList();
        return texts.Count == 1 ? texts[0] : null;
    }

    // Each locale asked for, then its language alone: ko-KR, ko, en-US, en.
    private static IEnumerable<string> Expand(IReadOnlyList<string> locales)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var locale in locales)
        {
            if (seen.Add(locale)) yield return locale;
            var dash = locale.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 && seen.Add(locale[..dash])) yield return locale[..dash];
        }
    }

    private List<LabelConflict> FindConflicts()
    {
        var conflicts = new List<LabelConflict>();
        foreach (var group in _sets.GroupBy(s => s.Locale.ToLowerInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var targets = group
                .SelectMany(s => s.Schemes.Select(p => (Target: new LabelTarget(p.Key, null), s.Pack, Text: p.Value))
                    .Concat(s.Fields.Select(p => (Target: new LabelTarget(null, p.Key), s.Pack, Text: p.Value))))
                .GroupBy(t => t.Target)
                .OrderBy(g => g.Key.Render(), StringComparer.Ordinal);
            foreach (var target in targets)
            {
                var found = target.Select(t => (t.Pack, t.Text)).ToList();
                if (found.Count > 1 && Winner(found) is null)
                    conflicts.Add(new(group.First().Locale, target.Key.Render(), [.. found.Select(f => f.Pack).Order(StringComparer.Ordinal)]));
            }
        }
        return conflicts;
    }
}

// What a label names: a scheme item or a field, compared by its parts and written out only for a report.
internal readonly record struct LabelTarget(SchemeLabelKey? Scheme, FieldLabelKey? Field)
{
    public string Render() => Scheme is { } s ? $"{s.Scheme} v{s.Version} {s.Code}" : $"{Field!.Value.Type}.{Field.Value.Field}";
}
