using Openquote.Classification;

namespace Openquote.Reports;

/// <summary>
/// One way a report form splits what it counts: by a classified field carried to a scheme version,
/// or by the string value of a field.
/// </summary>
/// <param name="Field">The field the records are split by.</param>
/// <param name="Scheme">The scheme the field's classified values count in, or null to split by the field's string value.</param>
/// <param name="Version">
/// The scheme version the values count in, or null for the version in force on the last day of the
/// period a run covers (see <see cref="SchemeCatalog.InForce"/>), so a form follows a revision of its
/// scheme without being written again. Always null when <paramref name="Scheme"/> is.
/// </param>
public sealed record ReportDimension(string Field, string? Scheme = null, int? Version = null)
{
    /// <summary>True when the dimension splits by classified values rather than by a string value.</summary>
    public bool Classified => Scheme is not null;
}

/// <summary>
/// One immutable version of a report form: which entities it counts, which calendar-date field
/// places them in a period, and the dimensions — one to three — whose values make each cell's key.
/// </summary>
/// <param name="Name">The form's name, as its path names it.</param>
/// <param name="Version">The form's version, as its path names it.</param>
/// <param name="Label">What people see the form called.</param>
/// <param name="Counts">The entity type counted.</param>
/// <param name="PeriodField">The calendar-date field that places a record in a period.</param>
/// <param name="Dimensions">
/// The dimensions a cell's key is made of, in key order. A form counts each scheme in one version, so
/// two classified dimensions of the same scheme name the same version.
/// </param>
public sealed record ReportDefinition(
    string Name,
    int Version,
    string Label,
    string Counts,
    string PeriodField,
    IReadOnlyList<ReportDimension> Dimensions)
{
    /// <summary>The most dimensions a form may have.</summary>
    public const int MaxDimensions = 3;

    /// <summary>Two forms are equal when every part is, the dimensions compared in order.</summary>
    public bool Equals(ReportDefinition? other) =>
        other is not null && Name == other.Name && Version == other.Version && Label == other.Label
        && Counts == other.Counts && PeriodField == other.PeriodField && Dimensions.SequenceEqual(other.Dimensions);

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

    /// <summary>The schemes the form's classified dimensions count in, each once, in dimension order.</summary>
    public IReadOnlyList<string> Schemes =>
        [.. Dimensions.Where(d => d.Classified).Select(d => d.Scheme!).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The version <paramref name="scheme"/> counts in, or null when the form counts it in the version in
    /// force. Throws when no dimension counts in it.
    /// </summary>
    public int? VersionOf(string scheme) =>
        Dimensions.First(d => d.Scheme == scheme).Version;

    /// <summary>
    /// Why the form cannot be run as it stands — no dimension or too many, a string dimension naming a
    /// version, or one scheme counted in two versions — or null when it can.
    /// </summary>
    public string? Problem()
    {
        if (Dimensions.Count is 0 or > MaxDimensions) return $"a report has one to {MaxDimensions} dimensions";
        if (Dimensions.Any(d => !d.Classified && d.Version is not null)) return "only a classified dimension names a scheme version";
        if (Dimensions.Where(d => d.Classified).GroupBy(d => d.Scheme).Any(g => g.Select(d => d.Version).Distinct().Count() > 1))
            return "a report counts each scheme in one version";
        return null;
    }

    /// <summary>
    /// This form with the scheme versions its dimensions count in over a period ending on
    /// <paramref name="to"/>: each dimension that names no version takes the version in force that day.
    /// Null when such a dimension's scheme has no version in force that day.
    /// </summary>
    public ReportDefinition? For(DateOnly to, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var dimensions = new ReportDimension[Dimensions.Count];
        for (var i = 0; i < Dimensions.Count; i++)
        {
            var d = Dimensions[i];
            if (!d.Classified || d.Version is not null) dimensions[i] = d;
            else if (catalog.InForce(d.Scheme!, to) is { } scheme) dimensions[i] = d with { Version = scheme.Version };
            else return null;
        }
        return this with { Dimensions = dimensions };
    }

    /// <summary>
    /// True when every dimension counts in a named scheme version — as a form does once
    /// <see cref="For"/> has chosen the versions in force.
    /// </summary>
    public bool Resolved => Dimensions.All(d => !d.Classified || d.Version is not null);

    /// <summary>
    /// True when a run of the form can be written in run record format 0: a classified first dimension
    /// and at most a string second one — the rows and the column a format 0 record places each cell by.
    /// </summary>
    public bool RowsAndColumn =>
        Dimensions.Count is 1 or 2 && Dimensions[0].Classified && (Dimensions.Count == 1 || !Dimensions[1].Classified);
}
