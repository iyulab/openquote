namespace Openquote.Classification;

/// <summary>
/// Every version of every scheme in a vault, with the crosswalks between them. Carries a value
/// from the version it was entered in to any later version.
/// </summary>
public sealed class SchemeCatalog
{
    private readonly Dictionary<(string Name, int Version), Scheme> _schemes;
    private readonly Dictionary<(string Name, int From), List<Crosswalk>> _crosswalks;

    /// <summary>Builds a catalog from the schemes and crosswalks a vault holds.</summary>
    public SchemeCatalog(IEnumerable<Scheme> schemes, IEnumerable<Crosswalk> crosswalks)
    {
        ArgumentNullException.ThrowIfNull(schemes);
        ArgumentNullException.ThrowIfNull(crosswalks);
        _schemes = schemes.ToDictionary(s => (s.Name, s.Version));
        _crosswalks = crosswalks
            .GroupBy(c => (c.Scheme, c.From))
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.To).ToList());
    }

    /// <summary>The scheme version, or null if the vault does not hold it.</summary>
    public Scheme? Find(string name, int version) => _schemes.GetValueOrDefault((name, version));

    /// <summary>
    /// Carries <paramref name="value"/> to <paramref name="targetVersion"/> of its scheme. A value
    /// lands on a code only when every step leaves exactly one candidate: an old code with one link
    /// is assigned, with two or more it waits for a person, and with none it is unmapped.
    /// </summary>
    public Resolution Resolve(CodedValue value, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (Find(value.Scheme, value.Version) is not { } own || !own.Contains(value.Code)
            || Find(value.Scheme, targetVersion) is null
            || value.Version > targetVersion
            || Path(value.Scheme, value.Version, targetVersion) is not { } path)
            return Unmapped([]);

        string[] codes = [value.Code];
        var applied = new List<string>();
        foreach (var crosswalk in path)
        {
            codes = codes.SelectMany(crosswalk.Targets).Distinct(StringComparer.Ordinal).ToArray();
            // A link to a code the next version does not have is a defect in the crosswalk, not a
            // place to put a record.
            if (Find(value.Scheme, crosswalk.To) is { } next) codes = codes.Where(next.Contains).ToArray();
            applied.Add($"{crosswalk.From}-{crosswalk.To}");
            if (codes.Length == 0) return Unmapped(applied);
        }

        return codes.Length == 1
            ? new Resolution(ResolutionKind.Assigned, codes[0], [], applied)
            : new Resolution(ResolutionKind.Pending, null, codes.Order(StringComparer.Ordinal).ToArray(), applied);
    }

    private static Resolution Unmapped(List<string> applied) => new(ResolutionKind.Unmapped, null, [], applied);

    // The shortest chain of crosswalks from one version to another (breadth-first, ties to the
    // lower intermediate version so the chain is deterministic).
    private List<Crosswalk>? Path(string scheme, int from, int to)
    {
        if (from == to) return [];
        var previous = new Dictionary<int, Crosswalk>();
        var queue = new Queue<int>([from]);
        while (queue.Count > 0)
        {
            var version = queue.Dequeue();
            foreach (var crosswalk in _crosswalks.GetValueOrDefault((scheme, version)) ?? [])
            {
                if (crosswalk.To == from || previous.ContainsKey(crosswalk.To)) continue;
                previous[crosswalk.To] = crosswalk;
                if (crosswalk.To == to)
                {
                    var path = new List<Crosswalk>();
                    for (var v = to; v != from; v = previous[v].From) path.Add(previous[v]);
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(crosswalk.To);
            }
        }
        return null;
    }
}
