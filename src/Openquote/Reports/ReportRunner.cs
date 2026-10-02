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
    /// unmapped over blank. A string dimension's place is the field's string value, or null. A
    /// dimension of the subjects reads their field instead (see <see cref="ReportDimension.OfSubject"/>).
    /// A record a filter places outside its values is not in the run at all; one a filter cannot
    /// place yet — pending, unmapped, blank or conflicted there — is listed with those records, so a
    /// filter never drops a record silently. Destroyed entities are not counted. Each counted record also carries the subjects it is
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
                $"no version of scheme '{report.Schemes.First(s => report.VersionOf(s) is null && catalog.InForce(s, to) is null)}' is in force on {to:yyyy-MM-dd}",
                nameof(report));
        var dimensions = form.Dimensions;
        var placing = new Placing(catalog,
            form.Schemes.ToDictionary(s => s, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal),
            Subjects(entities, form));

        var cells = new SortedDictionary<IReadOnlyList<string?>, List<string>>(KeyComparer.Instance);
        var pending = new List<string>();
        var unmapped = new List<string>();
        var blank = new List<string>();
        var conflicted = new List<string>();
        var people = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            if (entity.Destroyed || entity.Reference.Type != report.Counts) continue;
            var id = entity.Reference.Id;
            var disputed = entity.Conflicts.TryGetValue(report.PeriodField, out var dates);
            if (disputed)
            {
                if (!dates!.Any(d => ParseDate(d.Value) is { } day && day >= from && day <= to)) continue;
            }
            else if (!entity.Fields.TryGetValue(report.PeriodField, out var dateValue) || ParseDate(dateValue) is not { } date
                || date < from || date > to) continue;

            // A filter that reads a value outside its list leaves the record out of the run; one that
            // cannot read a value yet keeps it in, among the records waiting for that value.
            var outcome = disputed ? Outcome.Conflicted : Outcome.Placed;
            var excluded = false;
            foreach (var filter in form.Filters)
            {
                var (filterOutcome, value) = placing.Place(entity, filter.On);
                if (filterOutcome == Outcome.Placed && (value is null || !filter.In.Contains(value, StringComparer.Ordinal)))
                {
                    excluded = true;
                    break;
                }
                outcome = Max(outcome, filterOutcome);
            }
            if (excluded) continue;

            people[id] = entity.People;
            var key = new string?[dimensions.Count];
            for (var i = 0; i < dimensions.Count && outcome != Outcome.Conflicted; i++)
            {
                (var placed, key[i]) = placing.Place(entity, dimensions[i]);
                outcome = Max(outcome, placed);
            }
            switch (outcome)
            {
                case Outcome.Placed:
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
                    list.Add(id);
                    break;
                case Outcome.Conflicted: conflicted.Add(id); break;
                case Outcome.Pending: pending.Add(id); break;
                case Outcome.Unmapped: unmapped.Add(id); break;
                default: blank.Add(id); break;
            }
        }

        return new ReportRun(
            form, from, to,
            [.. form.Schemes.Select(s => new ReportScheme(s, form.VersionOf(s)!.Value, placing.Crosswalks[s].ToArray(),
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

    // Ordered so a record placed several ways lands in the set the later member names.
    private enum Outcome { Placed, Blank, Unmapped, Pending, Conflicted }

    private static Outcome Max(Outcome a, Outcome b) => a > b ? a : b;

    // The subjects records may be placed by, by id — only when the form reads a subject's field.
    private static Dictionary<string, Entity> Subjects(IEnumerable<Entity> entities, ReportDefinition form) =>
        form.Dimensions.Concat(form.Filters.Select(f => f.On)).Any(d => d.OfSubject)
            ? entities.Where(e => !e.Destroyed && e.Reference.Type == "subject")
                .ToDictionary(e => e.Reference.Id, StringComparer.Ordinal)
            : new Dictionary<string, Entity>();

    private sealed record Placing(SchemeCatalog Catalog, Dictionary<string, SortedSet<string>> Crosswalks,
        Dictionary<string, Entity> Subjects)
    {
        // Where a dimension places a record: by the record's own field, or by its subjects' — the
        // subject's place when there is one, their common value when there are several and they
        // agree on it, and otherwise no single value (null).
        public (Outcome, string?) Place(Entity record, ReportDimension d)
        {
            if (!d.OfSubject) return PlaceIn(record, d);
            var places = record.People
                .Select(id => Subjects.GetValueOrDefault(id))
                .OfType<Entity>()
                .Select(subject => PlaceIn(subject, d))
                .ToList();
            if (places.Count == 1) return places[0];
            return places.Count > 0 && places.All(p => p.Item1 == Outcome.Placed && p.Item2 is not null)
                && places.Select(p => p.Item2).Distinct(StringComparer.Ordinal).Count() == 1
                ? places[0]
                : (Outcome.Placed, null);
        }

        private (Outcome, string?) PlaceIn(Entity entity, ReportDimension d)
        {
            if (entity.Conflicts.ContainsKey(d.Field)) return (Outcome.Conflicted, null);
            if (!d.Classified) return (Outcome.Placed, StringOf(entity, d.Field));
            var resolution = entity.Classify(d.Field, d.Scheme!, d.Version!.Value, Catalog);
            Crosswalks[d.Scheme!].UnionWith(resolution.Crosswalks);
            return resolution.Kind switch
            {
                ResolutionKind.Assigned => (Outcome.Placed, resolution.Code),
                ResolutionKind.Pending => (Outcome.Pending, null),
                _ => (entity.HasValue(d.Field) ? Outcome.Unmapped : Outcome.Blank, null),
            };
        }
    }

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
