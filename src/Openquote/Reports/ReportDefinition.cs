using Openquote.Classification;

namespace Openquote.Reports;

/// <summary>
/// One way a report form places what it counts: by a classified field carried to a scheme version,
/// or by the string value of a field — of the record itself, or of the subjects it is about.
/// </summary>
/// <param name="Field">The field the records are placed by.</param>
/// <param name="Scheme">The scheme the field's classified values count in, or null to place by the field's string value.</param>
/// <param name="Version">
/// The scheme version the values count in, or null for the version in force on the last day of the
/// period a run covers (see <see cref="SchemeCatalog.InForce"/>), so a form follows a revision of its
/// scheme without being written again. Always null when <paramref name="Scheme"/> is.
/// </param>
/// <param name="OfSubject">
/// True to read the field of the subjects a record is about (see <see cref="Records.Entity.People"/>)
/// rather than of the record. A record about several subjects has a value only when they all have
/// the same one; otherwise, and when it is about none, its place is null — no single value.
/// </param>
/// <param name="All">
/// For a classified dimension of the record, true to place a field holding several values by every
/// one of them rather than by the primary one: a record then counts once in each cell its values
/// lead to — the cells add up to more than the records (see <see cref="ReportRun.Multiple"/>).
/// </param>
public sealed record ReportDimension(string Field, string? Scheme = null, int? Version = null, bool OfSubject = false, bool All = false)
{
    /// <summary>True when the dimension places by classified values rather than by a string value.</summary>
    public bool Classified => Scheme is not null;
}

/// <summary>
/// A condition a record must meet to be counted: its value for <paramref name="On"/> — placed as a
/// dimension would place it — is one of <paramref name="In"/>.
/// </summary>
/// <param name="On">What the filter reads, as a dimension.</param>
/// <param name="In">The codes, or string values, a counted record has there.</param>
public sealed record ReportFilter(ReportDimension On, IReadOnlyList<string> In)
{
    /// <inheritdoc/>
    public bool Equals(ReportFilter? other) => other is not null && On == other.On && In.SequenceEqual(other.In);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(On, In.Count);
}

/// <summary>A number a report form shows for each cell, set and total.</summary>
public enum ReportMeasure
{
    /// <summary>How many records: each record once.</summary>
    Records,

    /// <summary>How many distinct people the records are about — a head count (see <see cref="ReportRun.PeopleOf"/>).</summary>
    People,

    /// <summary>How many visits: for each record, the number of people it is about, added up (see <see cref="ReportRun.VisitsOf"/>).</summary>
    Visits,
}

/// <summary>
/// One immutable version of a report form: which entities it counts, how it places them in a
/// period, which of them it counts, the dimensions — one to three — whose values make each cell's
/// key, and the numbers it shows.
/// </summary>
/// <param name="Name">The form's name, as its path names it.</param>
/// <param name="Version">The form's version, as its path names it.</param>
/// <param name="Label">What people see the form called.</param>
/// <param name="Counts">The entity type counted.</param>
/// <param name="Period">The calendar-date field that places a record in a period, and the unit the form is run over.</param>
/// <param name="Dimensions">
/// The dimensions a cell's key is made of, in key order. A form counts each scheme in one version, so
/// two classified dimensions or filters of the same scheme name the same version.
/// </param>
public sealed record ReportDefinition(
    string Name,
    int Version,
    string Label,
    string Counts,
    ReportPeriod Period,
    IReadOnlyList<ReportDimension> Dimensions)
{
    /// <summary>The most dimensions a form may have.</summary>
    public const int MaxDimensions = 3;

    /// <summary>The conditions every counted record meets; none by default.</summary>
    public IReadOnlyList<ReportFilter> Filters { get; init; } = [];

    /// <summary>The numbers the form shows, in order; records and people by default.</summary>
    public IReadOnlyList<ReportMeasure> Measures { get; init; } = [ReportMeasure.Records, ReportMeasure.People];

    /// <summary>
    /// The number fields of the counted records the form adds up for each cell, set and total, in
    /// order after <see cref="Measures"/> (see <see cref="ReportRun.SumOf"/>); none by default.
    /// </summary>
    public IReadOnlyList<string> Sums { get; init; } = [];

    /// <summary>Two forms are equal when every part is, dimensions and filters compared in order.</summary>
    public bool Equals(ReportDefinition? other) =>
        other is not null && Name == other.Name && Version == other.Version && Label == other.Label
        && Counts == other.Counts && Period == other.Period && Dimensions.SequenceEqual(other.Dimensions)
        && Filters.SequenceEqual(other.Filters) && Measures.SequenceEqual(other.Measures) && Sums.SequenceEqual(other.Sums);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Version);
        hash.Add(Counts);
        foreach (var d in Dimensions) hash.Add(d);
        return hash.ToHashCode();
    }

    // Everything that places a record: the dimensions, then what the filters read.
    private IEnumerable<ReportDimension> Placers => Dimensions.Concat(Filters.Select(f => f.On));

    /// <summary>The schemes the form's classified dimensions and filters count in, each once, dimensions first.</summary>
    public IReadOnlyList<string> Schemes =>
        [.. Placers.Where(d => d.Classified).Select(d => d.Scheme!).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The version <paramref name="scheme"/> counts in, or null when the form counts it in the version in
    /// force. Throws when no dimension or filter counts in it.
    /// </summary>
    public int? VersionOf(string scheme) => Placers.First(d => d.Scheme == scheme).Version;

    /// <summary>
    /// Why the form cannot be run as it stands — a period it cannot use, no dimension or too many, a
    /// string dimension naming a version, a filter that lets nothing through, one scheme counted in two
    /// versions, or no measure or one twice (a field added up counting as a measure) — or null when it can.
    /// </summary>
    public string? Problem()
    {
        if (Period.Problem() is { } period) return period;
        if (Measures.Count + Sums.Count == 0 || Measures.Distinct().Count() != Measures.Count || Sums.Distinct(StringComparer.Ordinal).Count() != Sums.Count)
            return "a report shows each of its measures once, and at least one";
        if (Dimensions.Count is 0 or > MaxDimensions) return $"a report has one to {MaxDimensions} dimensions";
        if (Placers.Any(d => !d.Classified && d.Version is not null)) return "only a classified dimension or filter names a scheme version";
        if (Filters.Any(f => f.In.Count == 0)) return "a filter names the values it lets through";
        if (Placers.Any(d => d.All && (!d.Classified || d.OfSubject))) return "only a classified dimension of the record counts every value";
        if (Filters.Any(f => f.On.All)) return "a filter reads the primary value";
        if (Placers.Where(d => d.Classified).GroupBy(d => d.Scheme).Any(g => g.Select(d => d.Version).Distinct().Count() > 1))
            return "a report counts each scheme in one version";
        return null;
    }

    /// <summary>
    /// This form with the scheme versions it counts in over a period ending on <paramref name="to"/>:
    /// each scheme it names no version of takes the version in force that day. Null when such a
    /// scheme has no version in force that day.
    /// </summary>
    public ReportDefinition? For(DateOnly to, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var scheme in Schemes)
        {
            if (VersionOf(scheme) is { } named) versions[scheme] = named;
            else if (catalog.InForce(scheme, to) is { } inForce) versions[scheme] = inForce.Version;
            else return null;
        }
        return Counted(versions);
    }

    /// <summary>This form counting each of its schemes in the version <paramref name="versions"/> gives it.</summary>
    public ReportDefinition Counted(IReadOnlyDictionary<string, int> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ReportDimension In(ReportDimension d) => d.Classified ? d with { Version = versions[d.Scheme!] } : d;
        return this with
        {
            Dimensions = [.. Dimensions.Select(In)],
            Filters = [.. Filters.Select(f => f with { On = In(f.On) })],
        };
    }

    /// <summary>
    /// True when every dimension and filter counts in a named scheme version — as a form does once
    /// <see cref="For"/> has chosen the versions in force.
    /// </summary>
    public bool Resolved => Placers.All(d => !d.Classified || d.Version is not null);

    /// <summary>
    /// True when a run of the form can be written in run record format 0: a monthly form with a
    /// classified first dimension and at most a string second one, both of the record, and no filter —
    /// the form a format 0 record was made for.
    /// </summary>
    public bool RowsAndColumn =>
        Filters.Count == 0 && Period.Unit == PeriodUnit.Month && Dimensions.Count is 1 or 2 && Dimensions.All(d => !d.OfSubject && !d.All)
        && Dimensions[0].Classified && (Dimensions.Count == 1 || !Dimensions[1].Classified);
}
