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
    IReadOnlyList<ExportColumn> Columns)
{
    /// <summary>
    /// True when the form lists the records kept in the activity folder as well (see
    /// <see cref="Records.Entity.Activity"/>) — format 2. A form of format 0 or 1 lists the records kept
    /// under subjects and groups only.
    /// </summary>
    public bool Activities { get; init; }
}

/// <summary>One column of an export form: its heading, and where its cells come from.</summary>
public abstract record ExportColumn(string Label);

/// <summary>A field of the record, as written.</summary>
public sealed record FieldColumn(string Label, string Field) : ExportColumn(Label);

/// <summary>
/// A classified field, carried to <paramref name="Version"/> of <paramref name="Scheme"/> and shown by
/// its label — the item's own, or with a <paramref name="Level"/> that of its ancestor at that level,
/// counted from 1 at the top (a value no deeper than the level shows its own). A value that waits for a
/// person or has no code there leaves the cell empty and is listed apart.
/// </summary>
public sealed record CodedColumn(string Label, string Field, string Scheme, int Version, int? Level) : ExportColumn(Label)
{
    /// <summary>
    /// Every value of a field holding several, rather than the primary one: their labels in the
    /// order the record holds them (the primary one first), each once, joined by a comma and a space
    /// (format 1). A value that waits for a person or has no code there is left out and the record
    /// listed apart, as for a single value.
    /// </summary>
    public bool All { get; init; }
}

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
public sealed record PersonColumn(string Label, string Field, bool All) : ExportColumn(Label)
{
    /// <summary>
    /// For a record about several subjects (format 1): their value when they all have the same one, and this
    /// text when they differ — as a form that has one row per session marks a mixed group. Null leaves the
    /// cell of a record about several subjects empty, as in format 0. Never set together with <see cref="PersonColumn.All"/>.
    /// </summary>
    public string? Mixed { get; init; }
}

/// <summary>
/// The year a calendar-date field falls in, for a year that starts in <paramref name="StartMonth"/>
/// (an academic or fiscal year): a date before that month belongs to the year before.
/// </summary>
public sealed record YearColumn(string Label, string Field, int StartMonth) : ExportColumn(Label);

/// <summary>How a <see cref="DateColumn"/> writes a calendar date.</summary>
public enum DateStyle
{
    /// <summary>ISO 8601 extended format, as a vault writes dates: <c>2026-03-10</c>.</summary>
    Extended,

    /// <summary>ISO 8601 basic format, digits only: <c>20260310</c>.</summary>
    Basic,
}

/// <summary>A calendar-date field in a given <paramref name="Style"/>; empty when the field holds no date (format 1).</summary>
public sealed record DateColumn(string Label, string Field, DateStyle Style) : ExportColumn(Label);

/// <summary>
/// A whole-number field divided by <paramref name="Divisor"/>: the quotient rounded down, or with
/// <paramref name="Remainder"/> what is left, from 0 up to the divisor — a number of minutes as hours and
/// minutes, for example. Empty when the field holds no whole number (format 1).
/// </summary>
public sealed record DivisionColumn(string Label, string Field, int Divisor, bool Remainder) : ExportColumn(Label);

/// <summary>The same text in every row, as an outside form fixes a column (format 1).</summary>
public sealed record ValueColumn(string Label, string Value) : ExportColumn(Label);
