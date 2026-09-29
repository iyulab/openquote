namespace Openquote.Exports;

/// <summary>
/// One immutable version of an export form: a list of records over a period — one row per record,
/// one cell per column — laid out the way an outside form or system takes them in.
/// </summary>
/// <param name="Name">The form's name.</param>
/// <param name="Version">Its version; editing a form makes a new one.</param>
/// <param name="Label">What people call it.</param>
/// <param name="Rows">The entity type listed, one row each.</param>
/// <param name="PeriodField">The calendar-date field that places a record in the period, and orders the rows.</param>
/// <param name="Columns">The columns, in order.</param>
public sealed record ExportDefinition(
    string Name,
    int Version,
    string Label,
    string Rows,
    string PeriodField,
    IReadOnlyList<ExportColumn> Columns);

/// <summary>One column of an export form: its heading, and where its cells come from.</summary>
public abstract record ExportColumn(string Label);

/// <summary>A field of the record, as written.</summary>
public sealed record FieldColumn(string Label, string Field) : ExportColumn(Label);

/// <summary>
/// A classified field, carried to <paramref name="Version"/> of <paramref name="Scheme"/> and shown by
/// its label — the item's own, or with <paramref name="Top"/> its top-level ancestor's. A value that
/// waits for a person or has no code there leaves the cell empty and is listed apart.
/// </summary>
public sealed record CodedColumn(string Label, string Field, string Scheme, int Version, bool Top) : ExportColumn(Label);

/// <summary>
/// A field (<paramref name="ReferencedField"/>) of the entity the record's <paramref name="Field"/>
/// refers to, such as a practitioner's name.
/// </summary>
public sealed record ReferenceColumn(string Label, string Field, string ReferencedField) : ExportColumn(Label);

/// <summary>How many different subjects the record is about.</summary>
public sealed record PeopleCountColumn(string Label) : ExportColumn(Label);

/// <summary>
/// A field of the subjects the record is about. With <paramref name="All"/>, every subject's value,
/// joined; otherwise the one subject's, and empty when the record is about several — as forms that
/// have one row per session leave a group session's per-person cells blank.
/// </summary>
public sealed record PersonColumn(string Label, string Field, bool All) : ExportColumn(Label);

/// <summary>
/// The year a calendar-date field falls in, for a year that starts in <paramref name="StartMonth"/>
/// (an academic or fiscal year): a date before that month belongs to the year before.
/// </summary>
public sealed record YearColumn(string Label, string Field, int StartMonth) : ExportColumn(Label);
