using Openquote.Classification;

namespace Openquote.Reports;

/// <summary>
/// Why two runs of a report over the same period differ, record by record: records that appear only
/// in the later run (entered late), only in the earlier one (removed or destroyed), or in both but
/// in a different place — because the scheme was revised between the two runs, because a person
/// settled values that were in conflict, or for a reason neither explains (reclassified by a person,
/// or moved to another column).
/// </summary>
/// <param name="Late">Only in the later run.</param>
/// <param name="Removed">Only in the earlier run.</param>
/// <param name="Revised">
/// In a different place, and exactly where the crosswalks carry the earlier place: the revision
/// alone moved it — into a new row, into pending when its old code was split, or into unmapped when
/// its old code has no link.
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
    private const string PendingPlace = "\u0000pending";
    private const string UnmappedPlace = "\u0000unmapped";
    private const string BlankPlace = "\u0000blank";
    private const string ConflictedPlace = "\u0000conflicted";

    /// <summary>
    /// Compares <paramref name="earlier"/> with <paramref name="later"/>. When the later run counts
    /// a later version of the same scheme, <paramref name="catalog"/> carries each earlier place to
    /// that version to tell records the revision moved from records moved for another reason.
    /// </summary>
    /// <remarks>
    /// Only runs that place records the same way can be compared: the same form name, counting the
    /// same entity type by the same period field, into rows of the same field and scheme and the same
    /// columns, over the same period. The form's version, label, and the scheme's version may
    /// differ. Otherwise every record would read as moved by a person, so such pairs are refused.
    /// The order is checked only where the runs show it: a later run that counts an earlier version
    /// of the scheme is refused, while two runs of one version cannot tell which came first.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="later"/> does not count the same thing over the same period as
    /// <paramref name="earlier"/>, or counts an earlier version of the scheme.
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
        var revisedAcross = earlier.Report.RowScheme == later.Report.RowScheme
            && earlier.Report.RowVersion < later.Report.RowVersion;
        var differ = a.Keys.Intersect(b.Keys).Where(k => a[k] != b[k]).ToList();
        var settled = differ.Where(k => a[k].Row == ConflictedPlace).ToList();
        var revised = revisedAcross
            ? differ.Except(settled).Where(k => Carry(a[k], earlier.Report, later.Report.RowVersion, catalog) == b[k]).ToList()
            : [];
        return new ReportDiff(
            Sorted(b.Keys.Except(a.Keys)),
            Sorted(a.Keys.Except(b.Keys)),
            Sorted(revised),
            Sorted(settled),
            Sorted(differ.Except(revised).Except(settled)),
            Sorted(a.Keys.Intersect(b.Keys).Where(k => a[k] == b[k])));
    }

    private static string? Incomparable(ReportRun earlier, ReportRun later)
    {
        var (a, b) = (earlier.Report, later.Report);
        if (a.Name != b.Name) return $"form '{b.Name}' is not '{a.Name}'";
        if (a.Counts != b.Counts) return $"it counts '{b.Counts}', not '{a.Counts}'";
        if (a.PeriodField != b.PeriodField) return $"its period field is '{b.PeriodField}', not '{a.PeriodField}'";
        if (a.RowField != b.RowField) return $"its row field is '{b.RowField}', not '{a.RowField}'";
        if (a.RowScheme != b.RowScheme) return $"its rows are scheme '{b.RowScheme}', not '{a.RowScheme}'";
        if (a.ColumnField != b.ColumnField)
            return $"its column field is {Quoted(b.ColumnField)}, not {Quoted(a.ColumnField)}";
        if (earlier.From != later.From || earlier.To != later.To)
            return $"its period is {later.From:yyyy-MM-dd}..{later.To:yyyy-MM-dd}, not {earlier.From:yyyy-MM-dd}..{earlier.To:yyyy-MM-dd}";
        if (b.RowVersion < a.RowVersion)
            return $"the later run counts version {b.RowVersion} of '{b.RowScheme}', before the earlier run's {a.RowVersion}";
        return null;
    }

    private static string Quoted(string? field) => field is null ? "none" : $"'{field}'";

    // Where a revision alone would put a record from `place`. Places other than a cell hold no
    // code, so nothing is carried from them.
    private static (string Row, string? Column)? Carry((string Row, string? Column) place, ReportDefinition form,
        int targetVersion, SchemeCatalog catalog)
    {
        if (place.Row is PendingPlace or UnmappedPlace or BlankPlace or ConflictedPlace) return null;
        var resolution = catalog.Resolve(new CodedValue(form.RowScheme, form.RowVersion, place.Row), targetVersion);
        return resolution.Kind switch
        {
            ResolutionKind.Assigned => (resolution.Code!, place.Column),
            ResolutionKind.Pending => (PendingPlace, null),
            _ => (UnmappedPlace, null),
        };
    }

    private static Dictionary<string, (string Row, string? Column)> Places(ReportRun run)
    {
        var places = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var cell in run.Cells)
            foreach (var id in cell.Records) places[id] = (cell.Row, cell.Column);
        foreach (var id in run.Pending) places[id] = (PendingPlace, null);
        foreach (var id in run.Unmapped) places[id] = (UnmappedPlace, null);
        foreach (var id in run.Blank) places[id] = (BlankPlace, null);
        foreach (var id in run.Conflicted) places[id] = (ConflictedPlace, null);
        return places;
    }

    private static string[] Sorted(IEnumerable<string> ids) => [.. ids.Order(StringComparer.Ordinal)];
}
