using Openquote.Classification;

namespace Openquote.Reports;

/// <summary>
/// Why two runs of a report over the same period differ, record by record: records that appear only
/// in the later run (entered late), only in the earlier one (removed or destroyed), or in both but
/// in a different place — because a scheme was revised between the two runs, because a person
/// settled values that were in conflict, or for a reason neither explains (reclassified by a person,
/// or moved to another string value).
/// </summary>
/// <param name="Late">Only in the later run.</param>
/// <param name="Removed">Only in the earlier run.</param>
/// <param name="Revised">
/// In a different place, and exactly where the crosswalks carry the earlier place: the revision
/// alone moved it — into a new key, into pending when an old code was split, or into unmapped when
/// an old code has no link.
/// </param>
/// <param name="Settled">
/// Conflicted in the earlier run and anywhere else in the later one: a person picked a value, or a
/// change that was missing arrived and showed the values were not concurrent after all.
/// </param>
/// <param name="Moved">In a different place that neither the revision nor a settled conflict explains.</param>
/// <param name="Unchanged">In the same place in both runs.</param>
/// <remarks>
/// A run keeps where each record was counted, not the value it was counted from, so the reason is
/// read from the places: a person's choice that lands exactly where the crosswalks would have carried
/// the record reads as <see cref="Revised"/>. The two cannot be told apart from the counts, and the
/// numbers they produce are the same.
/// </remarks>
public sealed record ReportDiff(
    IReadOnlyList<string> Late,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Revised,
    IReadOnlyList<string> Settled,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Unchanged)
{
    private static readonly string?[] PendingPlace = ["\u0000pending"];
    private static readonly string?[] UnmappedPlace = ["\u0000unmapped"];
    private static readonly string?[] BlankPlace = ["\u0000blank"];
    private static readonly string?[] ConflictedPlace = ["\u0000conflicted"];

    /// <summary>
    /// Compares <paramref name="earlier"/> with <paramref name="later"/>. When the later run counts
    /// a later version of a scheme, <paramref name="catalog"/> carries each earlier place to that
    /// version to tell records the revision moved from records moved for another reason.
    /// </summary>
    /// <remarks>
    /// Only runs that place records the same way can be compared: the same form name, counting the
    /// same entity type by the same period field, split by the same dimensions — the same fields, each
    /// classified in the same scheme or split by its string value — over the same period. The form's
    /// version, label, and the scheme versions may differ. Otherwise every record would read as moved
    /// by a person, so such pairs are refused. The order is checked only where the runs show it: a
    /// later run that counts an earlier version of a scheme is refused, while two runs of one version
    /// cannot tell which came first.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="later"/> does not count the same thing over the same period as
    /// <paramref name="earlier"/>, or counts an earlier version of a scheme.
    /// </exception>
    public static ReportDiff Compare(ReportRun earlier, ReportRun later, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);
        ArgumentNullException.ThrowIfNull(catalog);
        if (Incomparable(earlier, later) is { } reason)
            throw new ArgumentException($"The runs cannot be compared: {reason}.", nameof(later));
        var a = Places(earlier);
        var b = Places(later);
        var revisedAcross = earlier.Report.Dimensions.Zip(later.Report.Dimensions).Any(p => p.First.Version < p.Second.Version);
        var differ = a.Keys.Intersect(b.Keys).Where(k => !Same(a[k], b[k])).ToList();
        var settled = differ.Where(k => Same(a[k], ConflictedPlace)).ToList();
        var revised = revisedAcross
            ? differ.Except(settled).Where(k => Carry(a[k], earlier.Report, later.Report, catalog) is { } carried && Same(carried, b[k])).ToList()
            : [];
        return new ReportDiff(
            Sorted(b.Keys.Except(a.Keys)),
            Sorted(a.Keys.Except(b.Keys)),
            Sorted(revised),
            Sorted(settled),
            Sorted(differ.Except(revised).Except(settled)),
            Sorted(a.Keys.Intersect(b.Keys).Where(k => Same(a[k], b[k]))));
    }

    private static string? Incomparable(ReportRun earlier, ReportRun later)
    {
        var (a, b) = (earlier.Report, later.Report);
        if (a.Name != b.Name) return $"form '{b.Name}' is not '{a.Name}'";
        if (a.Counts != b.Counts) return $"it counts '{b.Counts}', not '{a.Counts}'";
        if (a.PeriodField != b.PeriodField) return $"its period field is '{b.PeriodField}', not '{a.PeriodField}'";
        if (a.Dimensions.Count != b.Dimensions.Count)
            return $"it has {b.Dimensions.Count} dimensions, not {a.Dimensions.Count}";
        for (var i = 0; i < a.Dimensions.Count; i++)
        {
            var (x, y) = (a.Dimensions[i], b.Dimensions[i]);
            if (x.Field != y.Field) return $"dimension {i + 1} is field '{y.Field}', not '{x.Field}'";
            if (x.Scheme != y.Scheme) return $"dimension {i + 1} is {Split(y)}, not {Split(x)}";
        }
        if (!a.Resolved || !b.Resolved) return "a run counts in scheme versions, and one of them names none";
        if (earlier.From != later.From || earlier.To != later.To)
            return $"its period is {later.From:yyyy-MM-dd}..{later.To:yyyy-MM-dd}, not {earlier.From:yyyy-MM-dd}..{earlier.To:yyyy-MM-dd}";
        foreach (var (x, y) in a.Dimensions.Zip(b.Dimensions))
            if (y.Version < x.Version)
                return $"the later run counts version {y.Version} of '{y.Scheme}', before the earlier run's {x.Version}";
        return null;
    }

    private static string Split(ReportDimension d) => d.Scheme is { } scheme ? $"classified in '{scheme}'" : "split by its string value";

    // Where a revision alone would put a record from `place`: each classified place carried to the
    // later run's version. Places other than a cell hold no code, so nothing is carried from them.
    private static string?[]? Carry(string?[] place, ReportDefinition earlier, ReportDefinition later, SchemeCatalog catalog)
    {
        if (place.Length == 1 && place[0]?.StartsWith('\u0000') == true) return null;
        var carried = new string?[place.Length];
        var outcome = ResolutionKind.Assigned;
        for (var i = 0; i < place.Length; i++)
        {
            var (from, to) = (earlier.Dimensions[i], later.Dimensions[i]);
            if (!from.Classified || from.Version == to.Version)
            {
                carried[i] = place[i];
                continue;
            }
            var resolution = catalog.Resolve(new CodedValue(from.Scheme!, from.Version!.Value, place[i]!), to.Version!.Value);
            carried[i] = resolution.Code;
            if (resolution.Kind == ResolutionKind.Pending) outcome = ResolutionKind.Pending;
            else if (resolution.Kind != ResolutionKind.Assigned && outcome == ResolutionKind.Assigned) outcome = resolution.Kind;
        }
        return outcome switch
        {
            ResolutionKind.Assigned => carried,
            ResolutionKind.Pending => PendingPlace,
            _ => UnmappedPlace,
        };
    }

    private static Dictionary<string, string?[]> Places(ReportRun run)
    {
        var places = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        foreach (var cell in run.Cells)
            foreach (var id in cell.Records) places[id] = [.. cell.Key];
        foreach (var id in run.Pending) places[id] = PendingPlace;
        foreach (var id in run.Unmapped) places[id] = UnmappedPlace;
        foreach (var id in run.Blank) places[id] = BlankPlace;
        foreach (var id in run.Conflicted) places[id] = ConflictedPlace;
        return places;
    }

    private static bool Same(string?[] a, string?[] b) => KeyComparer.Instance.Equals(a, b);

    private static string[] Sorted(IEnumerable<string> ids) => [.. ids.Order(StringComparer.Ordinal)];
}
