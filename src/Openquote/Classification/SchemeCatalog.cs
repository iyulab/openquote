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
    /// The version of <paramref name="name"/> to offer for a value entered on <paramref name="date"/>: the
    /// highest version in force that day, where a version without dates is in force throughout. Null when
    /// the vault holds no version in force that day.
    /// </summary>
    public Scheme? InForce(string name, DateOnly date) =>
        _schemes.Values
            .Where(s => s.Name == name
                && (s.EffectiveFrom ?? DateOnly.MinValue) <= date
                && date <= (s.EffectiveTo ?? DateOnly.MaxValue))
            .MaxBy(s => s.Version);

    /// <summary>
    /// The days after <paramref name="from"/> up to <paramref name="to"/> on which the version of
    /// <paramref name="name"/> in force (see <see cref="InForce"/>) changes, in date order.
    /// </summary>
    public IReadOnlyList<SchemeBoundary> Boundaries(string name, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(name);
        var days = _schemes.Values
            .Where(s => s.Name == name)
            .SelectMany(s => new[] { s.EffectiveFrom, s.EffectiveTo?.AddDays(1) })
            .OfType<DateOnly>()
            .Where(d => d > from && d <= to)
            .Distinct()
            .Order();
        var boundaries = new List<SchemeBoundary>();
        foreach (var day in days)
        {
            var before = InForce(name, day.AddDays(-1))?.Version;
            var after = InForce(name, day)?.Version;
            if (before != after) boundaries.Add(new SchemeBoundary(day, before, after));
        }
        return boundaries;
    }

    /// <summary>
    /// Whether a value of <paramref name="value"/>'s scheme and version can be counted in
    /// <paramref name="targetVersion"/> of <paramref name="targetScheme"/>: the same scheme at that
    /// version or an earlier one, or a scheme that extends it — directly or through others — at such
    /// a version.
    /// </summary>
    public bool Reaches(CodedValue value, string targetScheme, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Fold(value, targetScheme) is { } folded && folded.Version <= targetVersion;
    }

    /// <summary>
    /// Carries <paramref name="value"/> to <paramref name="targetVersion"/> of
    /// <paramref name="targetScheme"/>: a value of a scheme that extends it first becomes the item its
    /// own item is anchored to, then is carried as a value of that scheme (see the other overload).
    /// </summary>
    public Resolution Resolve(CodedValue value, string targetScheme, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(targetScheme);
        return Fold(value, targetScheme) is { } folded ? Resolve(folded, targetVersion) : Unmapped([]);
    }

    // A value of a scheme that extends `target`, through as many extensions as it takes, as the
    // value of `target` its item is anchored to. Null when the value's scheme does not reach `target`
    // or an item on the way has no anchor that its extended version holds.
    private CodedValue? Fold(CodedValue value, string target)
    {
        var current = value;
        for (var steps = 0; current.Scheme != target; steps++)
        {
            if (steps > _schemes.Count || Find(current.Scheme, current.Version) is not { Extends: { } extended } scheme
                || scheme.Items.FirstOrDefault(i => i.Code == current.Code)?.Anchor is not { } anchor
                || Find(extended.Scheme, extended.Version) is not { } next || !next.Contains(anchor))
                return null;
            current = new CodedValue(extended.Scheme, extended.Version, anchor);
        }
        return current;
    }

    /// <summary>
    /// Carries <paramref name="value"/> to <paramref name="targetVersion"/> of its scheme. A value
    /// lands on a code only when every step leaves exactly one candidate: an old code with one link
    /// is assigned, with two or more it waits for a person, and with none it is unmapped. Where a
    /// crosswalk states how its links relate (see <see cref="Crosswalk.Relations"/>), a link from a
    /// broader item makes a person decide even when it is the only one, and a retired link carries
    /// nothing.
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
        var review = false;
        var applied = new List<string>();
        foreach (var crosswalk in path)
        {
            var carried = codes.Select(crosswalk.Carry).ToList();
            review |= carried.Any(c => c.Review);
            codes = carried.SelectMany(c => c.Codes).Distinct(StringComparer.Ordinal).ToArray();
            // A link to a code the next version does not have is a defect in the crosswalk, not a
            // place to put a record.
            if (Find(value.Scheme, crosswalk.To) is { } next) codes = codes.Where(next.Contains).ToArray();
            applied.Add($"{crosswalk.From}-{crosswalk.To}");
            if (codes.Length == 0) return Unmapped(applied);
        }

        return codes.Length == 1 && !review
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

/// <summary>
/// A day within a run's period on which the version of its row scheme in force changes, with the
/// version in force the day before and that day (null when none was). Counts on either side of it
/// were entered under different versions, so a series across it is not like for like.
/// </summary>
public sealed record SchemeBoundary(DateOnly Date, int? From, int? To);
