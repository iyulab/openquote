using System.Globalization;
using System.Text.Json;
using Openquote.Classification;
using Openquote.Fields;
using Openquote.Records;

namespace Openquote.Exports;

/// <summary>One row of an export: the record it lists and its cells, in the form's column order.</summary>
public sealed record ExportRow(string Record, IReadOnlyList<string> Cells);

/// <summary>
/// An export form run over a period. Rows are ordered by date, then by record id. Records with a
/// classified cell left empty — waiting for a person, or with no code in the form's version — are
/// listed, so the gap is seen before the rows go anywhere.
/// </summary>
/// <param name="Export">The form that was run.</param>
/// <param name="From">The first day of the period.</param>
/// <param name="To">The last day of the period.</param>
/// <param name="Rows">The listed records, by date then id.</param>
/// <param name="Pending">Records with a classified cell waiting for a person.</param>
/// <param name="Unmapped">Records with a classified cell that has no code in the form's version.</param>
/// <param name="Withheld">The columns whose cells were left empty in the rows produced because they would carry written content, by label, in column order; a period with no rows names none.</param>
public sealed record ExportTable(
    ExportDefinition Export,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ExportRow> Rows,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> Withheld);

/// <summary>Lays records out as an export form's rows, without guessing.</summary>
public static class ExportRunner
{
    /// <summary>
    /// Lists the <see cref="ExportDefinition.Rows"/> entities whose period field falls from
    /// <paramref name="from"/> to <paramref name="to"/> inclusive. <paramref name="entities"/> is every
    /// entity of the vault, so references (a practitioner, the subjects a record is about) resolve.
    /// Destroyed entities are not listed. A column that would carry a field <paramref name="fields"/> declares as written
    /// content, in any form — the field's own text, a year taken from it, the label of its code — is left empty and named in
    /// <see cref="ExportTable.Withheld"/>. Pass the vault's field definitions; <see cref="FieldCatalog.Empty"/> is for a vault
    /// that declares none.
    /// </summary>
    public static ExportTable Run(ExportDefinition export, DateOnly from, DateOnly to, IEnumerable<Entity> entities, SchemeCatalog catalog,
        FieldCatalog fields)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fields);
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
        var withheld = new HashSet<ExportColumn>();
        var context = new CellContext(byId, catalog, fields, pending, unmapped, withheld);
        var rows = listed
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Entity.Reference.Id, StringComparer.Ordinal)
            .Select(x => new ExportRow(x.Entity.Reference.Id, [.. export.Columns.Select(c => Cell(c, x.Entity, context))]))
            .ToArray();
        return new ExportTable(export, from, to, rows, [.. pending], [.. unmapped],
            [.. export.Columns.Where(withheld.Contains).Select(c => c.Label)]);
    }

    private sealed record CellContext(
        Dictionary<string, Entity> ById, SchemeCatalog Catalog, FieldCatalog Fields,
        SortedSet<string> Pending, SortedSet<string> Unmapped, HashSet<ExportColumn> Withheld);

    private static string Cell(ExportColumn column, Entity record, CellContext x) => column switch
    {
        FieldColumn f => Guarded(x, column, record, f.Field, () => Text(record, f.Field)),
        ReferenceColumn r => record.Fields.TryGetValue(r.Field, out var id) && id.ValueKind == JsonValueKind.String
            && x.ById.TryGetValue(id.GetString()!, out var referred)
                ? Guarded(x, column, referred, r.ReferencedField, () => Text(referred, r.ReferencedField))
                : "",
        PeopleCountColumn => record.People.Count.ToString(CultureInfo.InvariantCulture),
        PersonColumn p => p.All || record.People.Count == 1
            ? string.Join(", ", record.People
                .Select(s => x.ById.TryGetValue(s, out var subject) ? Guarded(x, column, subject, p.Field, () => Text(subject, p.Field)) : "")
                .Where(t => t.Length > 0))
            : "",
        YearColumn y => Guarded(x, column, record, y.Field, () =>
            record.Fields.TryGetValue(y.Field, out var v) && ParseDate(v) is { } date
                ? (date.Month >= y.StartMonth ? date.Year : date.Year - 1).ToString(CultureInfo.InvariantCulture)
                : ""),
        CodedColumn c => Guarded(x, column, record, c.Field, () => Coded(c, record, x.Catalog, x.Pending, x.Unmapped)),
        _ => throw new NotSupportedException(column.GetType().Name),
    };

    // A field declared as written content never reaches a cell: the cell stays empty and the column is named.
    private static string Guarded(CellContext x, ExportColumn column, Entity entity, string field, Func<string> value)
    {
        if (!x.Fields.IsNarrative(entity.Reference.Type, field)) return value();
        x.Withheld.Add(column);
        return "";
    }

    private static string Coded(CodedColumn column, Entity record, SchemeCatalog catalog, SortedSet<string> pending, SortedSet<string> unmapped)
    {
        if (!record.HasValue(column.Field)) return "";
        var resolution = record.Classify(column.Field, column.Scheme, column.Version, catalog);
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
