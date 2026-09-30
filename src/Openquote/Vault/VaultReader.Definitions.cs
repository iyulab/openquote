using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Openquote.Classification;
using Openquote.Exports;
using Openquote.Reports;

namespace Openquote.Vault;

public static partial class VaultReader
{
    private enum DefinitionKind { None, Scheme, Crosswalk, Report, Export, Pack }

    private readonly record struct Definition<T>(T? Value, UnreadableFile? Error) where T : class;

    // schemes/<name>/v<N>.json · schemes/<name>/v<N>-v<M>.json · reports/<name>/v<N>.json
    [GeneratedRegex(@"^schemes/(?<name>[^/]+)/v(?<from>[1-9][0-9]*)(-v(?<to>[1-9][0-9]*))?\.json$")]
    private static partial Regex SchemePath();

    [GeneratedRegex(@"^reports/(?<name>[^/]+)/v(?<version>[1-9][0-9]*)\.json$")]
    private static partial Regex ReportPath();

    [GeneratedRegex(@"^exports/(?<name>[^/]+)/v(?<version>[1-9][0-9]*)\.json$")]
    private static partial Regex ExportPath();

    private static DefinitionKind DefinitionKindOf(string path)
    {
        if (SchemePath().Match(path) is { Success: true } m)
            return m.Groups["to"].Success ? DefinitionKind.Crosswalk : DefinitionKind.Scheme;
        if (ReportPath().IsMatch(path)) return DefinitionKind.Report;
        if (ExportPath().IsMatch(path)) return DefinitionKind.Export;
        return PackPath().IsMatch(path) ? DefinitionKind.Pack : DefinitionKind.None;
    }

    private static void Collect<T>(Definition<T> parsed, List<T> into, List<UnreadableFile> unreadable) where T : class
    {
        if (parsed.Error is { } error) unreadable.Add(error);
        else into.Add(parsed.Value!);
    }

    private static Definition<T> Bad<T>(VaultFile file, UnreadableReason reason, string detail) where T : class =>
        new(null, new UnreadableFile(file.Path, reason, detail));

    private static bool TryRoot(VaultFile file, string format, out JsonElement root, out UnreadableFile? error)
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
        if (root.ValueKind != JsonValueKind.Object || !TryString(root, "format", out var f) || f != format)
        {
            error = new UnreadableFile(file.Path, UnreadableReason.UnknownFormat, $"expected format {format}");
            return false;
        }
        return true;
    }

    private static bool TryInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }

    private static Definition<Scheme> ParseScheme(VaultFile file)
    {
        if (!TryRoot(file, "openquote.scheme/0", out var root, out var error)) return new(null, error);
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
            items.Add(new SchemeItem(code, label, parent, suggest));
        }

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (!codes.Add(item.Code)) return Bad<Scheme>(file, UnreadableReason.Invalid, $"code {item.Code} appears twice");
        foreach (var item in items)
            if (item.Parent is { } parent && !codes.Contains(parent))
                return Bad<Scheme>(file, UnreadableReason.Invalid, $"{item.Code}: parent {parent} is not an item");

        return new(new Scheme(name, version, items), null);
    }

    private static Definition<Crosswalk> ParseCrosswalk(VaultFile file)
    {
        if (!TryRoot(file, "openquote.crosswalk/0", out var root, out var error)) return new(null, error);
        var path = SchemePath().Match(file.Path);

        if (!TryString(root, "scheme", out var name) || !TryInt(root, "from", out var from) || !TryInt(root, "to", out var to))
            return Bad<Crosswalk>(file, UnreadableReason.Invalid, "a crosswalk needs a scheme and from/to versions");
        if (to <= from)
            return Bad<Crosswalk>(file, UnreadableReason.Invalid, "a crosswalk must go from an older version to a newer one");
        if (name != path.Groups["name"].Value
            || from.ToString(CultureInfo.InvariantCulture) != path.Groups["from"].Value
            || to.ToString(CultureInfo.InvariantCulture) != path.Groups["to"].Value)
            return Bad<Crosswalk>(file, UnreadableReason.NameMismatch, $"the path should be schemes/{name}/v{from}-v{to}.json");
        if (!root.TryGetProperty("links", out var linksArray) || linksArray.ValueKind != JsonValueKind.Array)
            return Bad<Crosswalk>(file, UnreadableReason.Invalid, "links must be an array");

        var links = new List<(string, string)>();
        foreach (var link in linksArray.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Array || link.GetArrayLength() != 2
                || link[0].ValueKind != JsonValueKind.String || link[1].ValueKind != JsonValueKind.String)
                return Bad<Crosswalk>(file, UnreadableReason.Invalid, "every link is a pair of codes");
            links.Add((link[0].GetString()!, link[1].GetString()!));
        }

        return new(new Crosswalk(name, from, to, links), null);
    }

    private static Definition<ReportDefinition> ParseReport(VaultFile file)
    {
        if (!TryRoot(file, "openquote.report/0", out var root, out var error)) return new(null, error);
        var path = ReportPath().Match(file.Path);

        if (!TryString(root, "report", out var name) || !TryInt(root, "version", out var version) || version < 1
            || !TryString(root, "label", out var label) || !TryString(root, "counts", out var counts))
            return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "a report needs a name, a version, a label and what it counts");
        if (name != path.Groups["name"].Value || version.ToString(CultureInfo.InvariantCulture) != path.Groups["version"].Value)
            return Bad<ReportDefinition>(file, UnreadableReason.NameMismatch, $"the path should be reports/{name}/v{version}.json");
        if (!root.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.Object
            || !TryString(period, "unit", out var unit) || unit != "month" || !TryString(period, "field", out var periodField))
            return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "period must be {\"unit\": \"month\", \"field\": ...}");
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Object
            || !TryString(rows, "field", out var rowField) || !TryString(rows, "scheme", out var rowScheme)
            || !TryInt(rows, "version", out var rowVersion) || rowVersion < 1)
            return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "rows must name a field, a scheme and a scheme version");

        string? columnField = null;
        if (root.TryGetProperty("columns", out var columns))
        {
            if (columns.ValueKind != JsonValueKind.Object || !TryString(columns, "field", out var c))
                return Bad<ReportDefinition>(file, UnreadableReason.Invalid, "columns must name a field");
            columnField = c;
        }

        return new(new ReportDefinition(name, version, label, counts, periodField, rowField, rowScheme, rowVersion, columnField), null);
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
