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
/// cell left empty because it cannot be filled without a person — a classified value waiting for a
/// choice or with no code in the form's version, or a field holding values set without seeing each
/// other — are listed, so the gap is seen before the rows go anywhere.
/// </summary>
/// <param name="Export">The form that was run.</param>
/// <param name="From">The first day of the period.</param>
/// <param name="To">The last day of the period.</param>
/// <param name="Rows">The listed records, by date then id.</param>
/// <param name="Pending">Records with a classified cell waiting for a person.</param>
/// <param name="Unmapped">Records with a classified cell that has no code in the form's version.</param>
/// <param name="Conflicted">
/// Records with a cell over a field — of the record, of an entity it refers to, or of its subjects —
/// that holds two or more values set without seeing each other. The cell is empty until a person
/// picks one: writing any of them would decide for them.
/// </param>
/// <param name="Withheld">The columns whose cells were left empty in the rows produced because they would carry written content, by label, in column order; a period with no rows names none.</param>
public sealed record ExportTable(
    ExportDefinition Export,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ExportRow> Rows,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> Conflicted,
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
            // A disputed date lists the record in every period one of its dates falls in, at the earliest.
            IEnumerable<JsonElement> dates = e.Conflicts.TryGetValue(export.PeriodField, out var heads)
                ? heads.Select(h => h.Value)
                : e.Fields.TryGetValue(export.PeriodField, out var v) ? [v] : [];
            if (dates.Select(ParseDate).Where(d => d is { } day && day >= from && day <= to).Min() is { } date)
                listed.Add((date, e));
        }

        var pending = new SortedSet<string>(StringComparer.Ordinal);
        var unmapped = new SortedSet<string>(StringComparer.Ordinal);
        var conflicted = new SortedSet<string>(StringComparer.Ordinal);
        var withheld = new HashSet<ExportColumn>();
        var context = new CellContext(byId, catalog, fields, pending, unmapped, conflicted, withheld);
        var rows = listed
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Entity.Reference.Id, StringComparer.Ordinal)
            .Select(x => new ExportRow(x.Entity.Reference.Id, [.. export.Columns.Select(c => Cell(c, x.Entity, context))]))
            .ToArray();
        return new ExportTable(export, from, to, rows, [.. pending], [.. unmapped], [.. conflicted],
            [.. export.Columns.Where(withheld.Contains).Select(c => c.Label)]);
    }

    private sealed record CellContext(
        Dictionary<string, Entity> ById, SchemeCatalog Catalog, FieldCatalog Fields,
        SortedSet<string> Pending, SortedSet<string> Unmapped, SortedSet<string> Conflicted, HashSet<ExportColumn> Withheld);

    private static string Cell(ExportColumn column, Entity record, CellContext x) => column switch
    {
        FieldColumn f => Guarded(x, column, record, record, f.Field, () => Text(record, f.Field)),
        ReferenceColumn r => Guarded(x, column, record, record, r.Field, () =>
            record.Fields.TryGetValue(r.Field, out var id) && id.ValueKind == JsonValueKind.String
            && x.ById.TryGetValue(id.GetString()!, out var referred)
                ? Guarded(x, column, record, referred, r.ReferencedField, () => Text(referred, r.ReferencedField))
                : ""),
        PeopleCountColumn => record.People.Count.ToString(CultureInfo.InvariantCulture),
        // Every subject's value in the order of the values: the order of the subjects is that of their
        // ids, which is when each was made — and two made within the same millisecond are in either order.
        PersonColumn p => p.All || record.People.Count == 1
            ? string.Join(", ", PersonValues(p, record, x).Where(t => t.Length > 0).Order(StringComparer.Ordinal))
            : p.Mixed is { } mixed && record.People.Count > 1
                ? PersonValues(p, record, x).Distinct(StringComparer.Ordinal).ToArray() is [var shared] ? shared : mixed
                : "",
        DateColumn d => Guarded(x, column, record, record, d.Field, () =>
            record.Fields.TryGetValue(d.Field, out var v) && ParseDate(v) is { } date
                ? date.ToString(d.Style == DateStyle.Basic ? "yyyyMMdd" : "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : ""),
        DivisionColumn n => Guarded(x, column, record, record, n.Field, () =>
            record.Fields.TryGetValue(n.Field, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var whole)
                ? (n.Remainder ? Remainder(whole, n.Divisor) : (whole - Remainder(whole, n.Divisor)) / n.Divisor).ToString(CultureInfo.InvariantCulture)
                : ""),
        ValueColumn f => f.Value,
        YearColumn y => Guarded(x, column, record, record, y.Field, () =>
            record.Fields.TryGetValue(y.Field, out var v) && ParseDate(v) is { } date
                ? (date.Month >= y.StartMonth ? date.Year : date.Year - 1).ToString(CultureInfo.InvariantCulture)
                : ""),
        CodedColumn c => Guarded(x, column, record, record, c.Field, () => Coded(c, record, x.Catalog, x.Pending, x.Unmapped)),
        _ => throw new NotSupportedException(column.GetType().Name),
    };

    private static IEnumerable<string> PersonValues(PersonColumn column, Entity record, CellContext x) =>
        record.People.Select(s => x.ById.TryGetValue(s, out var subject) ? Guarded(x, column, record, subject, column.Field, () => Text(subject, column.Field)) : "");

    // The remainder of division rounded down: never negative, so the quotient of a negative number rounds down too.
    private static long Remainder(long value, int divisor) => (value % divisor + divisor) % divisor;

    // A field declared as written content never reaches a cell: the cell stays empty and the column is
    // named. A field holding concurrent values does not either, until a person picks one: the record is listed.
    private static string Guarded(CellContext x, ExportColumn column, Entity record, Entity entity, string field, Func<string> value)
    {
        if (x.Fields.IsNarrative(entity.Reference.Type, field))
        {
            x.Withheld.Add(column);
            return "";
        }
        if (entity.Conflicts.ContainsKey(field))
        {
            x.Conflicted.Add(record.Reference.Id);
            return "";
        }
        return value();
    }

    private static string Coded(CodedColumn column, Entity record, SchemeCatalog catalog, SortedSet<string> pending, SortedSet<string> unmapped)
    {
        if (!record.HasValue(column.Field)) return "";
        if (column.All) return EveryCoded(column, record, catalog, pending, unmapped);
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
        return LabelOf(column, resolution.Code!, catalog);
    }

    // Each value of the field carried to the column's version, the primary one first: their labels,
    // each once. A value that cannot be carried leaves the record listed apart; the others still show.
    private static string EveryCoded(CodedColumn column, Entity record, SchemeCatalog catalog, SortedSet<string> pending, SortedSet<string> unmapped)
    {
        if (record.ClassifiedValues(column.Field, column.Scheme, column.Version, catalog) is not { } values)
        {
            unmapped.Add(record.Reference.Id);
            return "";
        }
        var ordered = values.Primary is { } primary ? [primary, .. values.Values.Where(v => v != primary)] : values.Values;
        var labels = new List<string>();
        foreach (var value in ordered)
        {
            var resolution = catalog.Reaches(value, column.Scheme, column.Version)
                ? catalog.Resolve(value, column.Scheme, column.Version)
                : new Resolution(ResolutionKind.Unmapped, null, [], []);
            if (resolution.Kind == ResolutionKind.Assigned) labels.Add(LabelOf(column, resolution.Code!, catalog));
            else (resolution.Kind == ResolutionKind.Pending ? pending : unmapped).Add(record.Reference.Id);
        }
        return string.Join(", ", labels.Where(l => l != "").Distinct(StringComparer.Ordinal));
    }

    private static string LabelOf(CodedColumn column, string code, SchemeCatalog catalog)
    {
        if (catalog.Find(column.Scheme, column.Version) is not { } scheme || scheme.Items.FirstOrDefault(i => i.Code == code) is not { } item)
            return "";
        if (column.Level is not { } level) return item.Label;
        var path = new List<SchemeItem> { item };
        while (path[^1].Parent is { } parent && scheme.Items.FirstOrDefault(i => i.Code == parent) is { } up) path.Add(up);
        return path[Math.Max(path.Count - level, 0)].Label;
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
