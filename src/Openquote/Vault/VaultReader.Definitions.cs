using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Openquote.Classification;
using Openquote.Exports;
using Openquote.Reports;

namespace Openquote.Vault;

public static partial class VaultReader
{
    private enum DefinitionKind { None, Scheme, Crosswalk, Report, Export, Pack, Labels, Fields, Suggestions }

    private readonly record struct Definition<T>(T? Value, UnreadableFile? Error) where T : class;

    // schemes/<name>/v<N>.json · schemes/<name>/v<N>-v<M>.json · reports/<name>/v<N>.json
    // schemes/<scheme>/v<N>.json, v<N>-v<M>.json, or v<N>-<other scheme>.v<M>.json (a crosswalk across schemes)
    [GeneratedRegex(@"^schemes/(?<name>[^/]+)/v(?<from>[1-9][0-9]*)(-((?<into>[^/]+)\.)?v(?<to>[1-9][0-9]*))?\.json\z")]
    private static partial Regex SchemePath();

    [GeneratedRegex(@"^reports/(?<name>[^/]+)/v(?<version>[1-9][0-9]*)\.json\z")]
    private static partial Regex ReportPath();

    [GeneratedRegex(@"^exports/(?<name>[^/]+)/v(?<version>[1-9][0-9]*)\.json\z")]
    private static partial Regex ExportPath();

    private static DefinitionKind DefinitionKindOf(string path)
    {
        if (SchemePath().Match(path) is { Success: true } m)
            return m.Groups["to"].Success ? DefinitionKind.Crosswalk : DefinitionKind.Scheme;
        if (ReportPath().IsMatch(path)) return DefinitionKind.Report;
        if (ExportPath().IsMatch(path)) return DefinitionKind.Export;
        if (PackPath().IsMatch(path)) return DefinitionKind.Pack;
        if (LabelsPath().IsMatch(path)) return DefinitionKind.Labels;
        if (SuggestionsPath().IsMatch(path)) return DefinitionKind.Suggestions;
        return FieldsPath().IsMatch(path) ? DefinitionKind.Fields : DefinitionKind.None;
    }

    private static void Collect<T>(Definition<T> parsed, List<T> into, List<UnreadableFile> unreadable) where T : class
    {
        if (parsed.Error is { } error) unreadable.Add(error);
        else into.Add(parsed.Value!);
    }

    private static Definition<T> Bad<T>(VaultFile file, UnreadableReason reason, string detail) where T : class =>
        new(null, new UnreadableFile(file.Path, reason, detail));

    private static bool TryRoot(VaultFile file, string format, out JsonElement root, out UnreadableFile? error) =>
        TryRoot(file, [format], out root, out error);

    // A file kind read in more than one format version: the root's format is one of `formats`.
    private static bool TryRoot(VaultFile file, string[] formats, out JsonElement root, out UnreadableFile? error)
    {
        error = null;
        root = default;
        try
        {
            using var doc = JsonDocument.Parse(file.Content);
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            error = new UnreadableFile(file.Path, UnreadableReason.Malformed, e.Message);
            return false;
        }
        if (root.ValueKind != JsonValueKind.Object || !TryString(root, "format", out var f) || !formats.Contains(f))
        {
            error = new UnreadableFile(file.Path, UnreadableReason.UnknownFormat, $"expected format {string.Join(" or ", formats)}");
            return false;
        }
        return true;
    }

    private static bool TryInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }

    private static readonly string[] SchemeFormats = ["openquote.scheme/0", "openquote.scheme/1"];

    private static Definition<Scheme> ParseScheme(VaultFile file)
    {
        // Format 1 adds `extends` and, in an extending scheme, every item's `anchor`.
        if (!TryRoot(file, SchemeFormats, out var root, out var error)) return new(null, error);
        var extensible = TryString(root, "format", out var format) && format == "openquote.scheme/1";
        var path = SchemePath().Match(file.Path);

        if (!TryString(root, "scheme", out var name) || !TryInt(root, "version", out var version) || version < 1)
            return Bad<Scheme>(file, UnreadableReason.Invalid, "scheme must have a name and a version of 1 or more");
        if (name != path.Groups["name"].Value || version.ToString(CultureInfo.InvariantCulture) != path.Groups["from"].Value)
            return Bad<Scheme>(file, UnreadableReason.NameMismatch, $"the path should be schemes/{name}/v{version}.json");
        if (!root.TryGetProperty("items", out var itemsArray) || itemsArray.ValueKind != JsonValueKind.Array)
            return Bad<Scheme>(file, UnreadableReason.Invalid, "items must be an array");

        var items = new List<SchemeItem>();
        foreach (var item in itemsArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryString(item, "code", out var code) || !TryString(item, "label", out var label))
                return Bad<Scheme>(file, UnreadableReason.Invalid, "every item needs a code and a label");
            string? parent = null;
            if (item.TryGetProperty("parent", out var p))
            {
                if (p.ValueKind != JsonValueKind.String) return Bad<Scheme>(file, UnreadableReason.Invalid, $"{code}: parent must be a code");
                parent = p.GetString();
            }
            var suggest = false; // suggestions stay off unless the scheme allows them
            if (item.TryGetProperty("suggest", out var s))
            {
                if (s.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Bad<Scheme>(file, UnreadableReason.Invalid, $"{code}: suggest must be true or false");
                suggest = s.GetBoolean();
            }
            string? anchor = null;
            if (item.TryGetProperty("anchor", out var a))
            {
                if (!extensible || a.ValueKind != JsonValueKind.String || a.GetString() is not { Length: > 0 } named)
                    return Bad<Scheme>(file, UnreadableReason.Invalid, $"{code}: anchor must be a code of the extended scheme, in a scheme that extends one");
                anchor = named;
            }
            items.Add(new SchemeItem(code, label, parent, suggest) { Anchor = anchor });
        }

        SchemeVersion? extends = null;
        if (root.TryGetProperty("extends", out var extendsJson))
        {
            if (!extensible || extendsJson.ValueKind != JsonValueKind.Object || !TryString(extendsJson, "scheme", out var baseName)
                || !TryInt(extendsJson, "version", out var baseVersion) || baseVersion < 1 || baseName == name)
                return Bad<Scheme>(file, UnreadableReason.Invalid, "extends names another scheme and its version (format 1)");
            extends = new SchemeVersion(baseName, baseVersion);
        }
        if (extends is not null && items.FirstOrDefault(i => i.Anchor is null) is { } loose)
            return Bad<Scheme>(file, UnreadableReason.Invalid, $"{loose.Code}: every item of an extending scheme names its anchor");
        if (extends is null && items.FirstOrDefault(i => i.Anchor is not null) is { } anchored)
            return Bad<Scheme>(file, UnreadableReason.Invalid, $"{anchored.Code}: anchor must be a code of the extended scheme, in a scheme that extends one");

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (!codes.Add(item.Code)) return Bad<Scheme>(file, UnreadableReason.Invalid, $"code {item.Code} appears twice");
        foreach (var item in items)
            if (item.Parent is { } parent && !codes.Contains(parent))
                return Bad<Scheme>(file, UnreadableReason.Invalid, $"{item.Code}: parent {parent} is not an item");

        DateOnly? from = null, to = null;
        if (root.TryGetProperty("effective", out var effective))
        {
            if (effective.ValueKind != JsonValueKind.Object || !TryString(effective, "from", out var fromText) || Day(fromText) is not { } f)
                return Bad<Scheme>(file, UnreadableReason.Invalid, "effective needs a from date (yyyy-MM-dd)");
            from = f;
            if (effective.TryGetProperty("to", out _))
            {
                if (!TryString(effective, "to", out var toText) || Day(toText) is not { } t || t < f)
                    return Bad<Scheme>(file, UnreadableReason.Invalid, "effective.to must be a date on or after from");
                to = t;
            }
        }

        return new(new Scheme(name, version, items, from, to) { Extends = extends }, null);
    }

    private static DateOnly? Day(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static Definition<Crosswalk> ParseCrosswalk(VaultFile file)
    {
        // Format 1 lets a link state its relation as a third element, and a crosswalk lead into another scheme.
        if (!TryRoot(file, CrosswalkFormats, out var root, out var error)) return new(null, error);
        var related = TryString(root, "format", out var format) && format == "openquote.crosswalk/1";
        var path = SchemePath().Match(file.Path);

        if (!TryString(root, "scheme", out var name) || !TryInt(root, "from", out var from) || !TryInt(root, "to", out var to)
            || from < 1 || to < 1)
            return Bad<Crosswalk>(file, UnreadableReason.Invalid, "a crosswalk needs a scheme and from/to versions");
        string? into = null;
        if (root.TryGetProperty("into", out _))
        {
            if (!related || !TryString(root, "into", out var other) || other == name)
                return Bad<Crosswalk>(file, UnreadableReason.Invalid, "into names another scheme (format 1)");
            into = other;
        }
        if (into is null && to <= from)
            return Bad<Crosswalk>(file, UnreadableReason.Invalid, "a crosswalk must go from an older version to a newer one");
        var expected = into is null ? $"v{from}-v{to}" : $"v{from}-{into}.v{to}";
        if (name != path.Groups["name"].Value || (path.Groups["into"].Success ? path.Groups["into"].Value : null) != into
            || from.ToString(CultureInfo.InvariantCulture) != path.Groups["from"].Value
            || to.ToString(CultureInfo.InvariantCulture) != path.Groups["to"].Value)
            return Bad<Crosswalk>(file, UnreadableReason.NameMismatch, $"the path should be schemes/{name}/{expected}.json");
        if (!root.TryGetProperty("links", out var linksArray) || linksArray.ValueKind != JsonValueKind.Array)
            return Bad<Crosswalk>(file, UnreadableReason.Invalid, "links must be an array");

        var links = new List<(string, string)>();
        var relations = new Dictionary<(string From, string To), LinkRelation>();
        foreach (var link in linksArray.EnumerateArray())
        {
            var length = link.ValueKind == JsonValueKind.Array ? link.GetArrayLength() : 0;
            if ((length != 2 && !(related && length == 3))
                || link[0].ValueKind != JsonValueKind.String || link[1].ValueKind != JsonValueKind.String)
                return Bad<Crosswalk>(file, UnreadableReason.Invalid,
                    related ? "every link is a pair of codes, with its relation as a third element if stated" : "every link is a pair of codes");
            var pair = (link[0].GetString()!, link[1].GetString()!);
            links.Add(pair);
            if (length == 3)
            {
                if (link[2].ValueKind != JsonValueKind.String || Relation(link[2].GetString()!) is not { } relation)
                    return Bad<Crosswalk>(file, UnreadableReason.Invalid, "a relation is equivalent, narrower, broader or retired");
                if (relations.TryGetValue(pair, out var stated) && stated != relation)
                    return Bad<Crosswalk>(file, UnreadableReason.Invalid, $"{pair.Item1} → {pair.Item2} states two relations");
                relations[pair] = relation;
            }
        }

        return new(new Crosswalk(name, from, to, links) { Relations = relations, Into = into }, null);
    }

    private static readonly string[] CrosswalkFormats = ["openquote.crosswalk/0", "openquote.crosswalk/1"];

    private static LinkRelation? Relation(string text) => text switch
    {
        "equivalent" => LinkRelation.Equivalent,
        "narrower" => LinkRelation.Narrower,
        "broader" => LinkRelation.Broader,
        "retired" => LinkRelation.Retired,
        _ => null,
    };

    private static readonly string[] ReportFormats = ["openquote.report/0", "openquote.report/1"];

    private static Definition<ReportDefinition> ParseReport(VaultFile file)
    {
        // Format 0 splits by rows (a classified field) and at most one column (a string field);
        // format 1 by one to three dimensions, whose scheme version may be "in-force".
        if (!TryRoot(file, ReportFormats, out var root, out var error)) return new(null, error);
        var v1 = TryString(root, "format", out var format) && format == "openquote.report/1";
        var path = ReportPath().Match(file.Path);

        if (!TryString(root, "report", out var name) || !TryInt(root, "version", out var version) || version < 1
            || !TryString(root, "label", out var label) || !TryString(root, "counts", out var counts))
            return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "a report needs a name, a version, a label and what it counts");
        if (name != path.Groups["name"].Value || version.ToString(CultureInfo.InvariantCulture) != path.Groups["version"].Value)
            return Bad<ReportDefinition>(file, UnreadableReason.NameMismatch, $"the path should be reports/{name}/v{version}.json");
        if (ParsePeriod(root, v1) is not { } reportPeriod)
            return Bad<ReportDefinition>(file, UnreadableReason.Invalid, v1
                ? "period must be {\"unit\": \"day\" | \"month\" | \"year\" | \"range\", \"field\": ...}, with \"startMonth\" (1-12) only for a year"
                : "period must be {\"unit\": \"month\", \"field\": ...}");

        var dimensions = new List<ReportDimension>();
        var filters = new List<ReportFilter>();
        IReadOnlyList<ReportMeasure>? measures = null;
        if (v1)
        {
            if (root.TryGetProperty("rows", out _) || root.TryGetProperty("columns", out _))
                return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "a format 1 report splits by dimensions, not rows and columns");
            if (!root.TryGetProperty("dimensions", out var list) || list.ValueKind != JsonValueKind.Array)
                return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "a format 1 report lists its dimensions");
            foreach (var d in list.EnumerateArray())
            {
                if (ParseDimension(d) is not { } dimension)
                    return Bad<ReportDefinition>(file, UnreadableReason.Invalid, DimensionShape);
                dimensions.Add(dimension);
            }
            if (root.TryGetProperty("measures", out var measuresJson))
            {
                if (!TryIds(measuresJson, out var named) || named.Count == 0 || named.Any(m => Measure(m) is null))
                    return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "measures lists records, people or visits");
                measures = [.. named.Select(m => Measure(m)!.Value)];
            }
            if (root.TryGetProperty("filters", out var filtersJson))
            {
                if (filtersJson.ValueKind != JsonValueKind.Array)
                    return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "filters must be a list");
                foreach (var f in filtersJson.EnumerateArray())
                {
                    if (ParseDimension(f) is not { } on || !f.TryGetProperty("in", out var values) || !TryIds(values, out var allowed))
                        return Bad<ReportDefinition>(file, UnreadableReason.Invalid, $"{DimensionShape}; a filter also lists the values it lets through in \"in\"");
                    filters.Add(new ReportFilter(on, allowed));
                }
            }
        }
        else
        {
            if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Object
                || !TryString(rows, "field", out var rowField) || !TryString(rows, "scheme", out var rowScheme)
                || !TryInt(rows, "version", out var rowVersion) || rowVersion < 1)
                return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "rows must name a field, a scheme and a scheme version");
            dimensions.Add(new ReportDimension(rowField, rowScheme, rowVersion));
            if (root.TryGetProperty("columns", out var columns))
            {
                if (columns.ValueKind != JsonValueKind.Object || !TryString(columns, "field", out var columnField))
                    return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "columns must name a field");
                dimensions.Add(new ReportDimension(columnField));
            }
        }

        var report = new ReportDefinition(name, version, label, counts, reportPeriod, dimensions) { Filters = filters };
        if (measures is not null) report = report with { Measures = measures };
        return report.Problem() is { } problem ? Bad<ReportDefinition>(file, UnreadableReason.Invalid, problem) : new(report, null);
    }

    // Format 0 knows months only; format 1 also days, years from a start month, and ranges a person picks.
    private static ReportPeriod? ParsePeriod(JsonElement root, bool v1)
    {
        if (!root.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.Object
            || !TryString(period, "unit", out var unitText) || !TryString(period, "field", out var field)) return null;
        PeriodUnit? unit = unitText switch
        {
            "month" => PeriodUnit.Month,
            "day" when v1 => PeriodUnit.Day,
            "year" when v1 => PeriodUnit.Year,
            "range" when v1 => PeriodUnit.Range,
            _ => null,
        };
        if (unit is null) return null;
        var startMonth = 1;
        if (period.TryGetProperty("startMonth", out _) && (unit != PeriodUnit.Year || !TryInt(period, "startMonth", out startMonth)))
            return null;
        var parsed = new ReportPeriod(field, unit.Value, startMonth);
        return parsed.Problem() is null ? parsed : null;
    }

    private static ReportMeasure? Measure(string name) => name switch
    {
        "records" => ReportMeasure.Records,
        "people" => ReportMeasure.People,
        "visits" => ReportMeasure.Visits,
        _ => null,
    };

    private const string DimensionShape =
        "a dimension names a field, with a scheme and a scheme version or \"in-force\" when it is classified, \"of\": \"subject\" to read the subjects' field, and \"values\": \"primary\" or \"all\"";

    // {field, scheme?, version?, of?, values?}: classified when it has a scheme and a version, of the
    // subjects with of "subject", by every value with values "all".
    private static ReportDimension? ParseDimension(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || !TryString(d, "field", out var field)) return null;
        var ofSubject = false;
        if (d.TryGetProperty("of", out _))
        {
            if (!TryString(d, "of", out var of) || of != "subject") return null;
            ofSubject = true;
        }
        var all = false;
        if (d.TryGetProperty("values", out _))
        {
            if (!TryString(d, "values", out var values) || values is not ("primary" or "all")) return null;
            all = values == "all";
        }
        if (!d.TryGetProperty("scheme", out _) && !d.TryGetProperty("version", out _)) return new ReportDimension(field, OfSubject: ofSubject, All: all);
        return TryString(d, "scheme", out var scheme) && TryDimensionVersion(d, out var counted)
            ? new ReportDimension(field, scheme, counted, ofSubject, all)
            : null;
    }

    // A scheme version, or "in-force" (null): the version in force on the last day of the period run.
    private static bool TryDimensionVersion(JsonElement dimension, out int? version)
    {
        version = null;
        if (TryInt(dimension, "version", out var named)) { version = named; return named >= 1; }
        return TryString(dimension, "version", out var word) && word == "in-force";
    }

    private static Definition<ExportDefinition> ParseExport(VaultFile file)
    {
        if (!TryRoot(file, "openquote.export/0", out var root, out var error)) return new(null, error);
        var path = ExportPath().Match(file.Path);

        if (!TryString(root, "export", out var name) || !TryInt(root, "version", out var version) || version < 1
            || !TryString(root, "label", out var label) || !TryString(root, "rows", out var rows))
            return Bad<ExportDefinition>(file, UnreadableReason.Invalid, "an export needs a name, a version, a label and what it lists");
        if (name != path.Groups["name"].Value || version.ToString(CultureInfo.InvariantCulture) != path.Groups["version"].Value)
            return Bad<ExportDefinition>(file, UnreadableReason.NameMismatch, $"the path should be exports/{name}/v{version}.json");
        if (!root.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.Object || !TryString(period, "field", out var periodField))
            return Bad<ExportDefinition>(file, UnreadableReason.Invalid, "period must name a calendar-date field");
        if (!root.TryGetProperty("columns", out var columnsJson) || columnsJson.ValueKind != JsonValueKind.Array || columnsJson.GetArrayLength() == 0)
            return Bad<ExportDefinition>(file, UnreadableReason.Invalid, "columns must be a non-empty array");

        var columns = new List<ExportColumn>();
        foreach (var c in columnsJson.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || !TryString(c, "label", out var heading))
                return Bad<ExportDefinition>(file, UnreadableReason.Invalid, "every column needs a label");
            if (ParseColumn(c, heading) is not { } column)
                return Bad<ExportDefinition>(file, UnreadableReason.Invalid, $"column {heading}: say where its cells come from");
            columns.Add(column);
        }

        return new(new ExportDefinition(name, version, label, rows, periodField, columns), null);
    }

    // A column names one source: a field (optionally a classified one, or a reference), the people
    // the record is about, or the year a date falls in.
    private static ExportColumn? ParseColumn(JsonElement c, string label)
    {
        if (TryString(c, "people", out var people))
            return people == "count" ? new PeopleCountColumn(label) : null;
        if (TryString(c, "person", out var personField))
            return new PersonColumn(label, personField, c.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True);
        if (TryString(c, "year", out var yearField))
            return TryInt(c, "startMonth", out var start) && start is >= 1 and <= 12 ? new YearColumn(label, yearField, start) : null;
        if (!TryString(c, "field", out var field)) return null;
        if (TryString(c, "ref", out var referenced)) return new ReferenceColumn(label, field, referenced);
        if (TryString(c, "scheme", out var scheme))
        {
            if (!TryInt(c, "version", out var version) || version < 1) return null;
            var top = false;
            if (TryString(c, "part", out var part))
            {
                if (part is not ("top" or "item")) return null;
                top = part == "top";
            }
            return new CodedColumn(label, field, scheme, version, top);
        }
        return new FieldColumn(label, field);
    }
}
