using System.Globalization;
using System.Text.Json;
using Openquote.Classification;
using Openquote.Records;

namespace Openquote.Reports;

/// <summary>Counts entities into a report form's cells without guessing.</summary>
public static class ReportRunner
{
    /// <summary>
    /// Runs <paramref name="report"/> over the calendar days <paramref name="from"/> to
    /// <paramref name="to"/> inclusive. An entity is in the period when its period field holds a
    /// date in range. When a field the form places by — the period field or a dimension's — holds
    /// concurrent values (see <see cref="Entity.Conflicts"/>), the entity is conflicted and in no
    /// cell; a disputed date puts it in every period one of its values falls in. Otherwise each
    /// classified dimension carries the entity's value to the form's scheme version: one code is its
    /// place in the key, several leave the entity pending, none leave it unmapped — or blank when the
    /// field holds no value to count (see <see cref="Entity.HasValue"/>), so an empty field is told
    /// apart from a gap in the crosswalks. When dimensions disagree, pending wins over unmapped and
    /// unmapped over blank. A string dimension's place is the field's string value, or null.
    /// Destroyed entities are not counted. Each counted record also carries the subjects it is
    /// about, so the run gives a head count beside every record count. A dimension that names no
    /// scheme version counts in the version in force on <paramref name="to"/>, and the run notes any
    /// day within the period on which that version changes.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The form cannot be run (see <see cref="ReportDefinition.Problem"/>), or a dimension names no
    /// scheme version and none is in force on <paramref name="to"/>.
    /// </exception>
    public static ReportRun Run(ReportDefinition report, DateOnly from, DateOnly to,
        IEnumerable<Entity> entities, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(catalog);
        if (to < from) throw new ArgumentException("the period ends before it starts", nameof(to));
        if (report.Problem() is { } problem) throw new ArgumentException(problem, nameof(report));
        var form = report.For(to, catalog)
            ?? throw new ArgumentException(
                $"no version of scheme '{report.Dimensions.First(d => d.Classified && d.Version is null && catalog.InForce(d.Scheme!, to) is null).Scheme}' is in force on {to:yyyy-MM-dd}",
                nameof(report));
        var dimensions = form.Dimensions;

        var cells = new SortedDictionary<IReadOnlyList<string?>, List<string>>(KeyComparer.Instance);
        var pending = new List<string>();
        var unmapped = new List<string>();
        var blank = new List<string>();
        var conflicted = new List<string>();
        var crosswalks = form.Schemes.ToDictionary(s => s, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var people = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            if (entity.Destroyed || entity.Reference.Type != report.Counts) continue;
            var id = entity.Reference.Id;
            if (entity.Conflicts.TryGetValue(report.PeriodField, out var dates))
            {
                if (!dates.Any(d => ParseDate(d.Value) is { } day && day >= from && day <= to)) continue;
                people[id] = entity.People;
                conflicted.Add(id);
                continue;
            }
            if (!entity.Fields.TryGetValue(report.PeriodField, out var dateValue) || ParseDate(dateValue) is not { } date
                || date < from || date > to) continue;

            people[id] = entity.People;
            if (dimensions.Any(d => entity.Conflicts.ContainsKey(d.Field)))
            {
                conflicted.Add(id);
                continue;
            }
            var key = new string?[dimensions.Count];
            var outcome = Outcome.Placed;
            for (var i = 0; i < dimensions.Count; i++)
            {
                var d = dimensions[i];
                if (!d.Classified)
                {
                    key[i] = StringOf(entity, d.Field);
                    continue;
                }
                var resolution = entity.Classify(d.Field, d.Scheme!, d.Version!.Value, catalog);
                crosswalks[d.Scheme!].UnionWith(resolution.Crosswalks);
                key[i] = resolution.Code;
                var missed = resolution.Kind switch
                {
                    ResolutionKind.Assigned => Outcome.Placed,
                    ResolutionKind.Pending => Outcome.Pending,
                    _ => entity.HasValue(d.Field) ? Outcome.Unmapped : Outcome.Blank,
                };
                if (missed > outcome) outcome = missed;
            }
            switch (outcome)
            {
                case Outcome.Placed:
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
                    list.Add(id);
                    break;
                case Outcome.Pending: pending.Add(id); break;
                case Outcome.Unmapped: unmapped.Add(id); break;
                default: blank.Add(id); break;
            }
        }

        return new ReportRun(
            form, from, to,
            [.. form.Schemes.Select(s => new ReportScheme(s, form.VersionOf(s)!.Value, crosswalks[s].ToArray(),
                report.VersionOf(s) is null ? catalog.Boundaries(s, from, to) : []))],
            cells.Select(kv => new ReportCell(kv.Key, Sorted(kv.Value))).ToArray(),
            Sorted(pending),
            Sorted(unmapped),
            Sorted(blank),
            Sorted(conflicted),
            people);
    }

    /// <summary>Runs <paramref name="report"/> over one calendar month.</summary>
    public static ReportRun RunMonth(ReportDefinition report, int year, int month, IEnumerable<Entity> entities, SchemeCatalog catalog)
    {
        var from = new DateOnly(year, month, 1);
        return Run(report, from, from.AddMonths(1).AddDays(-1), entities, catalog);
    }

    // Ordered so a record several dimensions leave out lands in the set the later member names.
    private enum Outcome { Placed, Blank, Unmapped, Pending }

    // An absent or cleared string value is the null place.
    private static string? StringOf(Entity entity, string field) =>
        entity.Fields.TryGetValue(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateOnly? ParseDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static string[] Sorted(List<string> ids) => [.. ids.Order(StringComparer.Ordinal)];
}

/// <summary>Orders cell keys place by place, a null place before any string.</summary>
internal sealed class KeyComparer : IComparer<IReadOnlyList<string?>>, IEqualityComparer<IReadOnlyList<string?>>
{
    public static readonly KeyComparer Instance = new();

    public int Compare(IReadOnlyList<string?>? x, IReadOnlyList<string?>? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
        {
            var c = (x[i], y[i]) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                var (a, b) => string.CompareOrdinal(a, b),
            };
            if (c != 0) return c;
        }
        return x.Count.CompareTo(y.Count);
    }

    public bool Equals(IReadOnlyList<string?>? x, IReadOnlyList<string?>? y) => Compare(x, y) == 0;

    public int GetHashCode(IReadOnlyList<string?> key)
    {
        var hash = new HashCode();
        foreach (var place in key) hash.Add(place, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
