namespace Openquote.Reports;

/// <summary>One cell: the records counted in it. The count is always the number of records.</summary>
public sealed record ReportCell(string Row, string? Column, IReadOnlyList<string> Records)
{
    /// <summary>How many records the cell holds.</summary>
    public int Count => Records.Count;
}

/// <summary>
/// The result of running a report form over a period. Every number traces back to the ids of the
/// records behind it; the total is always the cells plus the pending and unmapped records.
/// </summary>
/// <param name="Report">The form that was run.</param>
/// <param name="From">First day of the period, inclusive.</param>
/// <param name="To">Last day of the period, inclusive.</param>
/// <param name="Crosswalks">Crosswalks applied to reach the form's scheme version, as <c>from-to</c>.</param>
/// <param name="Cells">Non-empty cells, ordered by row then column.</param>
/// <param name="Pending">Records whose value maps to several codes and waits for a person.</param>
/// <param name="Unmapped">Records whose value has no code in the form's scheme version.</param>
public sealed record ReportRun(
    ReportDefinition Report,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<string> Crosswalks,
    IReadOnlyList<ReportCell> Cells,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unmapped)
{
    /// <summary>Every record in the period: the cells, then pending, then unmapped.</summary>
    public IReadOnlyList<string> Total { get; } =
        Cells.SelectMany(c => c.Records).Concat(Pending).Concat(Unmapped).Order(StringComparer.Ordinal).ToArray();
}
