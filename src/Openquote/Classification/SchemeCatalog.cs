namespace Openquote.Classification;

/// <summary>
/// Every version of every scheme in a vault, with the crosswalks between them. Carries a value
/// from the version it was entered in to any later version, and across to another scheme where
/// crosswalks lead there.
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
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.TargetScheme == c.Scheme ? 0 : 1)
                .ThenBy(c => c.TargetScheme, StringComparer.Ordinal).ThenBy(c => c.To).ToList());
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
    /// The schemes that extend a version of <paramref name="name"/> (see <see cref="Scheme.Extends"/>) —
    /// lists a body keeps beside a shared one — each at its highest version that does, ordered by name.
    /// A host offers their items beside the shared scheme's wherever a field takes it: a value of an
    /// extending scheme counts as its anchor wherever the shared scheme is counted.
    /// </summary>
    public IReadOnlyList<Scheme> ExtensionsOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _schemes.Values
            .Where(s => s.Extends?.Scheme == name)
            .GroupBy(s => s.Name)
            .Select(g => g.MaxBy(s => s.Version)!)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

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
    /// version or an earlier one; a scheme that extends it — directly or through others — at such a
    /// version; or a scheme from which crosswalks lead to it.
    /// </summary>
    public bool Reaches(CodedValue value, string targetScheme, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(targetScheme);
        return Route(value, targetScheme, targetVersion) is not null;
    }

    /// <summary>
    /// Carries <paramref name="value"/> to <paramref name="targetVersion"/> of
    /// <paramref name="targetScheme"/>. A value of a scheme that extends it first becomes the item its
    /// own item is anchored to; a value of another scheme follows the crosswalks that lead from its
    /// scheme to the target — from the value's own scheme when they do, otherwise from the nearest
    /// scheme it extends. Every step is carried as in the other overload.
    /// </summary>
    public Resolution Resolve(CodedValue value, string targetScheme, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(targetScheme);
        return Route(value, targetScheme, targetVersion) is { } route ? Carry(route.Start, route.Path) : Unmapped([]);
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
        return value.Version <= targetVersion && Find(value.Scheme, targetVersion) is not null
            ? Carry(value, Path((value.Scheme, value.Version), (value.Scheme, targetVersion), sameScheme: true))
            : Unmapped([]);
    }

    private sealed record Routed(CodedValue Start, List<Crosswalk>? Path);

    // Where a value starts and the crosswalks it follows to the target. The value itself, then each
    // item it is anchored to through the schemes it extends, is tried in turn: in the target scheme at
    // the target version or an earlier one, it follows that scheme's crosswalks (a path may be missing:
    // the value is then counted as unmapped, as it always was); in another scheme, it needs crosswalks
    // that lead from it to the target. Null when no step reaches the target.
    private Routed? Route(CodedValue value, string targetScheme, int targetVersion)
    {
        var current = value;
        for (var steps = 0; steps <= _schemes.Count; steps++)
        {
            if (current.Scheme == targetScheme)
                return current.Version <= targetVersion
                    ? new Routed(current, Find(targetScheme, targetVersion) is null ? null
                        : Path((current.Scheme, current.Version), (targetScheme, targetVersion), sameScheme: true))
                    : null;
            if (Path((current.Scheme, current.Version), (targetScheme, targetVersion), sameScheme: false) is { } across)
                return new Routed(current, across);
            if (Find(current.Scheme, current.Version) is not { Extends: { } extended } scheme
                || scheme.Items.FirstOrDefault(i => i.Code == current.Code)?.Anchor is not { } anchor
                || Find(extended.Scheme, extended.Version) is not { } next || !next.Contains(anchor))
                return null;
            current = new CodedValue(extended.Scheme, extended.Version, anchor);
        }
        return null;
    }

    private Resolution Carry(CodedValue value, List<Crosswalk>? path)
    {
        if (path is null || Find(value.Scheme, value.Version) is not { } own || !own.Contains(value.Code))
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
            if (Find(crosswalk.TargetScheme, crosswalk.To) is { } next) codes = codes.Where(next.Contains).ToArray();
            applied.Add(crosswalk.Name);
            if (codes.Length == 0) return Unmapped(applied);
        }

        return codes.Length == 1 && !review
            ? new Resolution(ResolutionKind.Assigned, codes[0], [], applied)
            : new Resolution(ResolutionKind.Pending, null, codes.Order(StringComparer.Ordinal).ToArray(), applied);
    }

    private static Resolution Unmapped(List<string> applied) => new(ResolutionKind.Unmapped, null, [], applied);

    // The shortest chain of crosswalks from one scheme version to another (breadth-first; ties go to
    // the crosswalk listed first — within a scheme, the lower version — so the chain is deterministic).
    // Within one scheme only that scheme's crosswalks are followed.
    private List<Crosswalk>? Path((string Scheme, int Version) from, (string Scheme, int Version) to, bool sameScheme)
    {
        if (from == to) return [];
        var previous = new Dictionary<(string, int), Crosswalk>();
        var queue = new Queue<(string Scheme, int Version)>([from]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var crosswalk in _crosswalks.GetValueOrDefault(node) ?? [])
            {
                var reached = (crosswalk.TargetScheme, crosswalk.To);
                if ((sameScheme && crosswalk.TargetScheme != from.Scheme) || reached == from || previous.ContainsKey(reached)) continue;
                previous[reached] = crosswalk;
                if (reached == to)
                {
                    var path = new List<Crosswalk>();
                    for (var at = to; at != from; at = (previous[at].Scheme, previous[at].From)) path.Add(previous[at]);
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(reached);
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
