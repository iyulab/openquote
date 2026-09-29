using System.Globalization;
using System.Text.Json;
using Openquote.Classification;
using Openquote.Records;

namespace Openquote.Exports;

/// <summary>One row of an export: the record it lists and its cells, in the form's column order.</summary>
public sealed record ExportRow(string Record, IReadOnlyList<string> Cells);

/// <summary>
/// An export form run over a period. Rows are ordered by date, then by record id. Records with a
/// classified cell left empty — waiting for a person, or with no code in the form's version — are
/// listed, so the gap is seen before the rows go anywhere.
/// </summary>
public sealed record ExportTable(
    ExportDefinition Export,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ExportRow> Rows,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unmapped);

/// <summary>Lays records out as an export form's rows, without guessing.</summary>
public static class ExportRunner
{
    /// <summary>
    /// Lists the <see cref="ExportDefinition.Rows"/> entities whose period field falls from
    /// <paramref name="from"/> to <paramref name="to"/> inclusive. <paramref name="entities"/> is every
    /// entity of the vault, so references (a practitioner, the subjects a record is about) resolve.
    /// Destroyed entities are not listed.
    /// </summary>
    public static ExportTable Run(ExportDefinition export, DateOnly from, DateOnly to, IEnumerable<Entity> entities, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(catalog);
        if (to < from) throw new ArgumentException("the period ends before it starts", nameof(to));

        var all = entities.Where(e => !e.Destroyed).ToList();
        var byId = new Dictionary<string, Entity>(StringComparer.Ordinal);
        foreach (var e in all) byId.TryAdd(e.Reference.Id, e);

        var listed = new List<(DateOnly Date, Entity Entity)>();
        foreach (var e in all)
        {
            if (e.Reference.Type != export.Rows) continue;
            if (e.Fields.TryGetValue(export.PeriodField, out var v) && ParseDate(v) is { } date && date >= from && date <= to)
                listed.Add((date, e));
        }

        var pending = new SortedSet<string>(StringComparer.Ordinal);
        var unmapped = new SortedSet<string>(StringComparer.Ordinal);
        var rows = listed
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Entity.Reference.Id, StringComparer.Ordinal)
            .Select(x => new ExportRow(x.Entity.Reference.Id,
                [.. export.Columns.Select(c => Cell(c, x.Entity, byId, catalog, pending, unmapped))]))
            .ToArray();
        return new ExportTable(export, from, to, rows, [.. pending], [.. unmapped]);
    }

    private static string Cell(ExportColumn column, Entity record, Dictionary<string, Entity> byId, SchemeCatalog catalog,
        SortedSet<string> pending, SortedSet<string> unmapped) => column switch
    {
        FieldColumn f => Text(record, f.Field),
        ReferenceColumn r => record.Fields.TryGetValue(r.Field, out var id) && id.ValueKind == JsonValueKind.String
            && byId.TryGetValue(id.GetString()!, out var referred) ? Text(referred, r.ReferencedField) : "",
        PeopleCountColumn => record.People.Count.ToString(CultureInfo.InvariantCulture),
        PersonColumn p => p.All || record.People.Count == 1
            ? string.Join(", ", record.People.Select(s => byId.TryGetValue(s, out var subject) ? Text(subject, p.Field) : "").Where(t => t.Length > 0))
            : "",
        YearColumn y => record.Fields.TryGetValue(y.Field, out var v) && ParseDate(v) is { } date
            ? (date.Month >= y.StartMonth ? date.Year : date.Year - 1).ToString(CultureInfo.InvariantCulture)
            : "",
        CodedColumn c => Coded(c, record, catalog, pending, unmapped),
        _ => throw new NotSupportedException(column.GetType().Name),
    };

    private static string Coded(CodedColumn column, Entity record, SchemeCatalog catalog, SortedSet<string> pending, SortedSet<string> unmapped)
    {
        if (!record.Fields.TryGetValue(column.Field, out var current) || current.ValueKind == JsonValueKind.Null) return "";
        var value = record.LatestValue(column.Field, v =>
            CodedValue.From(v) is { } c && c.Scheme == column.Scheme && c.Version <= column.Version);
        if (value is null || CodedValue.From(value.Value) is not { } coded)
        {
            unmapped.Add(record.Reference.Id);
            return "";
        }
        var resolution = catalog.Resolve(coded, column.Version);
        switch (resolution.Kind)
        {
            case ResolutionKind.Pending:
                pending.Add(record.Reference.Id);
                return "";
            case ResolutionKind.Unmapped:
                unmapped.Add(record.Reference.Id);
                return "";
        }
        if (catalog.Find(column.Scheme, column.Version)?.Items.FirstOrDefault(i => i.Code == resolution.Code) is not { } item)
            return "";
        var scheme = catalog.Find(column.Scheme, column.Version)!;
        if (column.Top)
            while (item.Parent is { } parent && scheme.Items.FirstOrDefault(i => i.Code == parent) is { } up) item = up;
        return item.Label;
    }

    private static string Text(Entity entity, string field) =>
        entity.Fields.TryGetValue(field, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!,
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        } : "";

    private static DateOnly? ParseDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
