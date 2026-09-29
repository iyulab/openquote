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
    /// date in range. Its row is its classified value carried to the form's scheme version: one
    /// code places it in a cell, several leave it pending, none leave it unmapped. Destroyed
    /// entities are not counted. Each counted record also carries the subjects it is about, so the
    /// run gives a head count beside every record count.
    /// </summary>
    public static ReportRun Run(ReportDefinition report, DateOnly from, DateOnly to,
        IEnumerable<Entity> entities, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(catalog);
        if (to < from) throw new ArgumentException("the period ends before it starts", nameof(to));

        var cells = new SortedDictionary<(string Row, string Column), List<string>>();
        var pending = new List<string>();
        var unmapped = new List<string>();
        var crosswalks = new SortedSet<string>(StringComparer.Ordinal);
        var people = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            if (entity.Destroyed || entity.Reference.Type != report.Counts) continue;
            if (!entity.Fields.TryGetValue(report.PeriodField, out var dateValue) || ParseDate(dateValue) is not { } date
                || date < from || date > to) continue;

            var id = entity.Reference.Id;
            people[id] = entity.People;
            var value = entity.LatestValue(report.RowField, v =>
                CodedValue.From(v) is { } c && c.Scheme == report.RowScheme && c.Version <= report.RowVersion);
            if (value is null || CodedValue.From(value.Value) is not { } coded)
            {
                unmapped.Add(id);
                continue;
            }

            var resolution = catalog.Resolve(coded, report.RowVersion);
            crosswalks.UnionWith(resolution.Crosswalks);
            switch (resolution.Kind)
            {
                case ResolutionKind.Assigned:
                    var key = (resolution.Code!, ColumnOf(entity, report.ColumnField));
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
                    list.Add(id);
                    break;
                case ResolutionKind.Pending:
                    pending.Add(id);
                    break;
                default:
                    unmapped.Add(id);
                    break;
            }
        }

        return new ReportRun(
            report, from, to,
            crosswalks.ToArray(),
            cells.Select(kv => new ReportCell(kv.Key.Row, kv.Key.Column.Length == 0 ? null : kv.Key.Column, Sorted(kv.Value))).ToArray(),
            Sorted(pending),
            Sorted(unmapped),
            people);
    }

    /// <summary>Runs <paramref name="report"/> over one calendar month.</summary>
    public static ReportRun RunMonth(ReportDefinition report, int year, int month, IEnumerable<Entity> entities, SchemeCatalog catalog)
    {
        var from = new DateOnly(year, month, 1);
        return Run(report, from, from.AddMonths(1).AddDays(-1), entities, catalog);
    }

    // An absent or cleared column value becomes the empty key, surfaced as a null column.
    private static string ColumnOf(Entity entity, string? field) =>
        field is not null && entity.Fields.TryGetValue(field, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : "";

    private static DateOnly? ParseDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static string[] Sorted(List<string> ids) => [.. ids.Order(StringComparer.Ordinal)];
}
