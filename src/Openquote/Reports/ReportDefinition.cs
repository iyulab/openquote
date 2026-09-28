namespace Openquote.Reports;

/// <summary>
/// One immutable version of a report form: which entities it counts, which calendar-date field
/// places them in a month, which classified field and scheme version form the rows, and which
/// reference field (if any) splits the columns.
/// </summary>
public sealed record ReportDefinition(
    string Name,
    int Version,
    string Label,
    string Counts,
    string PeriodField,
    string RowField,
    string RowScheme,
    int RowVersion,
    string? ColumnField);
