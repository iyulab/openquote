using Openquote.Classification;

namespace Openquote.Reports;

/// <summary>
/// One immutable version of a report form: which entities it counts, which calendar-date field
/// places them in a month, which classified field and scheme version form the rows, and which
/// reference field (if any) splits the columns.
/// </summary>
/// <param name="Name">The form's name, as its path names it.</param>
/// <param name="Version">The form's version, as its path names it.</param>
/// <param name="Label">What people see the form called.</param>
/// <param name="Counts">The entity type counted.</param>
/// <param name="PeriodField">The calendar-date field that places a record in a period.</param>
/// <param name="RowField">The classified field the rows are made from.</param>
/// <param name="RowScheme">The scheme of <paramref name="RowField"/> the rows count in.</param>
/// <param name="ColumnField">The field whose string value splits the columns, or null for one column.</param>
/// <param name="RowVersion">
/// The scheme version the rows count in, or null for the version in force on the last day of the
/// period a run covers (see <see cref="SchemeCatalog.InForce"/>), so a form follows a revision of its
/// scheme without being written again.
/// </param>
public sealed record ReportDefinition(
    string Name,
    int Version,
    string Label,
    string Counts,
    string PeriodField,
    string RowField,
    string RowScheme,
    int? RowVersion,
    string? ColumnField)
{
    /// <summary>
    /// This form with the scheme version its rows count in over a period ending on <paramref name="to"/>:
    /// itself when it names a version, otherwise the version in force that day. Null when it names no
    /// version and none is in force that day.
    /// </summary>
    public ReportDefinition? For(DateOnly to, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (RowVersion is not null) return this;
        return catalog.InForce(RowScheme, to) is { } scheme ? this with { RowVersion = scheme.Version } : null;
    }
}
