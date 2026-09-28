namespace Openquote.Reports;

/// <summary>
/// Why two runs of a report over the same period differ, record by record: records that appear only
/// in the later run (entered late), only in the earlier one (removed or destroyed), or in both but
/// in a different place (reclassified, or moved to another column).
/// </summary>
public sealed record ReportDiff(
    IReadOnlyList<string> Late,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Unchanged)
{
    private const string PendingPlace = "\u0000pending";
    private const string UnmappedPlace = "\u0000unmapped";

    /// <summary>Compares <paramref name="earlier"/> with <paramref name="later"/>.</summary>
    public static ReportDiff Compare(ReportRun earlier, ReportRun later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);
        var a = Places(earlier);
        var b = Places(later);
        return new ReportDiff(
            Sorted(b.Keys.Except(a.Keys)),
            Sorted(a.Keys.Except(b.Keys)),
            Sorted(a.Keys.Intersect(b.Keys).Where(k => a[k] != b[k])),
            Sorted(a.Keys.Intersect(b.Keys).Where(k => a[k] == b[k])));
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
