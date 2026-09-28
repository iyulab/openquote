using Openquote.Classification;

namespace Openquote.Reports;

/// <summary>
/// Why two runs of a report over the same period differ, record by record: records that appear only
/// in the later run (entered late), only in the earlier one (removed or destroyed), or in both but
/// in a different place — either because the scheme was revised between the two runs, or for a
/// reason the revision does not explain (reclassified by a person, or moved to another column).
/// </summary>
/// <param name="Late">Only in the later run.</param>
/// <param name="Removed">Only in the earlier run.</param>
/// <param name="Revised">
/// In a different place, and exactly where the crosswalks carry the earlier place: the revision
/// alone moved it — into a new row, into pending when its old code was split, or into unmapped when
/// its old code has no link.
/// </param>
/// <param name="Moved">In a different place that the revision does not explain.</param>
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
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Unchanged)
{
    private const string PendingPlace = "\u0000pending";
    private const string UnmappedPlace = "\u0000unmapped";

    /// <summary>
    /// Compares <paramref name="earlier"/> with <paramref name="later"/>. When the later run counts
    /// a later version of the same scheme, <paramref name="catalog"/> carries each earlier place to
    /// that version to tell records the revision moved from records moved for another reason.
    /// </summary>
    public static ReportDiff Compare(ReportRun earlier, ReportRun later, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);
        ArgumentNullException.ThrowIfNull(catalog);
        var a = Places(earlier);
        var b = Places(later);
        var revisedAcross = earlier.Report.RowScheme == later.Report.RowScheme
            && earlier.Report.RowVersion < later.Report.RowVersion;
        var differ = a.Keys.Intersect(b.Keys).Where(k => a[k] != b[k]).ToList();
        var revised = revisedAcross
            ? differ.Where(k => Carry(a[k], earlier.Report, later.Report.RowVersion, catalog) == b[k]).ToList()
            : [];
        return new ReportDiff(
            Sorted(b.Keys.Except(a.Keys)),
            Sorted(a.Keys.Except(b.Keys)),
            Sorted(revised),
            Sorted(differ.Except(revised)),
            Sorted(a.Keys.Intersect(b.Keys).Where(k => a[k] == b[k])));
    }

    // Where a revision alone would put a record from `place`. Pending and unmapped places hold no
    // code, so nothing is carried from them.
    private static (string Row, string? Column)? Carry((string Row, string? Column) place, ReportDefinition form,
        int targetVersion, SchemeCatalog catalog)
    {
        if (place.Row is PendingPlace or UnmappedPlace) return null;
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
        return places;
    }

    private static string[] Sorted(IEnumerable<string> ids) => [.. ids.Order(StringComparer.Ordinal)];
}
