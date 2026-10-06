using Openquote.Fields;
using Openquote.Packs;

namespace Openquote.Scales;

/// <summary>What does not fit in a vault's scales.</summary>
public enum ScaleIssueKind
{
    /// <summary>Two packs give a scale of the same code; the pack first by id stands.</summary>
    DuplicateScale,

    /// <summary>The scale field is not a coded field of the response type, so no response names a scale.</summary>
    ScaleFieldNotCoded,

    /// <summary>The score field is not a number field of the response type, so no response holds a score.</summary>
    ScoreFieldNotNumber,
}

/// <summary>A scale, or a pack's response type, that does not fit.</summary>
/// <param name="Kind">What does not fit.</param>
/// <param name="Pack">The pack whose scales file says it.</param>
/// <param name="Detail">The scale code or the field, in words.</param>
public sealed record ScaleIssue(ScaleIssueKind Kind, string Pack, string Detail);

/// <summary>
/// The scales of a vault's packs, each pack at its highest version. A scale is found by the response type and the
/// code its scale field holds.
/// </summary>
public sealed class ScaleCatalog
{
    private readonly Dictionary<string, (Scale Scale, ScaleSet Set)> _byCode = new(StringComparer.Ordinal);

    /// <summary>A catalog with no scales.</summary>
    public static ScaleCatalog Empty { get; } = new([], []);

    /// <summary>Builds the catalog from a vault's scales files and pack manifests.</summary>
    public ScaleCatalog(IEnumerable<ScaleSet> sets, IEnumerable<PackManifest> packs)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(packs);
        Sets = [.. sets.GroupBy(s => s.Pack, StringComparer.Ordinal).Select(g => g.MaxBy(s => s.Version)!).OrderBy(s => s.Pack, StringComparer.Ordinal)];
        var issues = new List<ScaleIssue>();
        foreach (var set in Sets)
        {
            foreach (var scale in set.Scales.Values.OrderBy(m => m.Code, StringComparer.Ordinal))
            {
                if (_byCode.TryGetValue(scale.Code, out var first))
                    issues.Add(new(ScaleIssueKind.DuplicateScale, set.Pack, $"{scale.Code} is given by {first.Set.Pack} too"));
                else
                    _byCode[scale.Code] = (scale, set);
            }
        }

        Issues = issues;
    }

    /// <summary>Each pack's scales file at its highest version, packs by id.</summary>
    public IReadOnlyList<ScaleSet> Sets { get; }

    /// <summary>The scales, by code, as the first pack to give each says it.</summary>
    public IReadOnlyList<Scale> Scales => [.. _byCode.Values.Select(m => m.Scale).OrderBy(m => m.Code, StringComparer.Ordinal)];

    /// <summary>Scales two packs give under one code.</summary>
    public IReadOnlyList<ScaleIssue> Issues { get; }

    /// <summary>The scales file that makes records of <paramref name="type"/> responses, or null when none does.</summary>
    public ScaleSet? ResponsesOf(string type) => Sets.FirstOrDefault(s => s.Type == type);

    /// <summary>The scale of <paramref name="code"/> that a response of <paramref name="type"/> names, or null.</summary>
    public Scale? Find(string type, string code) =>
        _byCode.TryGetValue(code, out var found) && found.Set.Type == type ? found.Scale : null;

    /// <summary>
    /// What does not fit between the scales and the fields the vault's packs declare: a scale field that is not
    /// coded, or a score field that is not a number, on the response type. The issues of the catalog itself come first.
    /// </summary>
    public IReadOnlyList<ScaleIssue> Check(FieldCatalog fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var issues = new List<ScaleIssue>(Issues);
        foreach (var set in Sets)
        {
            if (fields.Find(set.Type, set.ScaleField)?.Kind != FieldKind.Coded)
                issues.Add(new(ScaleIssueKind.ScaleFieldNotCoded, set.Pack, $"{set.Type}.{set.ScaleField}"));
            if (fields.Find(set.Type, set.ScoreField)?.Kind != FieldKind.Number)
                issues.Add(new(ScaleIssueKind.ScoreFieldNotNumber, set.Pack, $"{set.Type}.{set.ScoreField}"));
        }

        return issues;
    }
}
