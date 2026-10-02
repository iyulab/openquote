using Openquote.Classification;

namespace Openquote.Reports;

/// <summary>
/// One cell: the records counted in it. Its key holds one value per dimension of the form, in the
/// form's order — a code for a classified dimension, a string or null for a string dimension. The
/// count is always the number of records.
/// </summary>
public sealed record ReportCell(IReadOnlyList<string?> Key, IReadOnlyList<string> Records)
{
    /// <summary>How many records the cell holds.</summary>
    public int Count => Records.Count;
}

/// <summary>
/// How a run counted in one scheme: the version, the crosswalks applied to reach it, and — for a
/// form that counts in the version in force — the days within the period on which that version changes.
/// </summary>
/// <param name="Scheme">The scheme.</param>
/// <param name="Version">The version counted in.</param>
/// <param name="Crosswalks">Crosswalks applied to reach it, as <c>from-to</c> or <c>scheme/from-into/to</c>.</param>
/// <param name="Boundaries">Days the version in force changes within the period; empty when it does not, or the form names its version.</param>
public sealed record ReportScheme(string Scheme, int Version, IReadOnlyList<string> Crosswalks, IReadOnlyList<SchemeBoundary> Boundaries);

/// <summary>
/// The result of running a report form over a period. Every number traces back to the ids of the
/// records behind it; the total is always the cells plus the pending, unmapped, blank and
/// conflicted records, each record in exactly one of them.
/// </summary>
/// <param name="Report">The form that was run, with the scheme versions it counted in.</param>
/// <param name="From">First day of the period, inclusive.</param>
/// <param name="To">Last day of the period, inclusive.</param>
/// <param name="Schemes">How the run counted in each scheme of the form's classified dimensions, in their order.</param>
/// <param name="Cells">Non-empty cells, ordered by key.</param>
/// <param name="Pending">Records a classified dimension's value maps to several codes for, waiting for a person.</param>
/// <param name="Unmapped">Records a classified dimension's value has no code for in the version counted in.</param>
/// <param name="Blank">
/// Records whose field holds no value to count for a classified dimension — never set, or cleared
/// with no earlier value — so an empty field is told apart from a gap in the crosswalks.
/// </param>
/// <param name="Conflicted">
/// Records a field the form places by (its period field or a dimension's field) holds two or more
/// values for that were set without seeing each other. Until a person picks one, the record is in
/// no cell: counting any of the values would decide for them.
/// </param>
/// <param name="People">
/// For every record in the period, the subjects it is about (see <see cref="Records.Entity.People"/>);
/// null when the run did not record them, as with runs kept before people were counted.
/// </param>
/// <remarks>
/// A record that more than one classified dimension leaves out of the cells is in one set only:
/// pending when any of them waits for a person, otherwise unmapped when any has no code, otherwise blank.
/// </remarks>
public sealed record ReportRun(
    ReportDefinition Report,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ReportScheme> Schemes,
    IReadOnlyList<ReportCell> Cells,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> Blank,
    IReadOnlyList<string> Conflicted,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? People = null)
{
    /// <summary>Every record in the period: the cells, pending, unmapped, blank and conflicted records.</summary>
    public IReadOnlyList<string> Total { get; } =
        Cells.SelectMany(c => c.Records).Concat(Pending).Concat(Unmapped).Concat(Blank).Concat(Conflicted)
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The visits behind <paramref name="records"/>: for each record, the number of people it is
    /// about, added up — a group session of three counts three. Null when the run did not record people.
    /// </summary>
    public int? VisitsOf(IEnumerable<string> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (People is null) return null;
        return records.Sum(r => People.TryGetValue(r, out var subjects) ? subjects.Count : 0);
    }

    /// <summary>
    /// The distinct subjects behind <paramref name="records"/> — a cell's, a set's or the total's
    /// records — ordered; their number is the head count beside the record count. Null when the
    /// run did not record people.
    /// </summary>
    public IReadOnlyList<string>? PeopleOf(IEnumerable<string> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (People is null) return null;
        return [.. records
            .SelectMany(r => People.TryGetValue(r, out var subjects) ? subjects : [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }
}
