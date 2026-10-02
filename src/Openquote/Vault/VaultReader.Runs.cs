using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Openquote.Reports;

namespace Openquote.Vault;

public static partial class VaultReader
{
    internal const string RunFormat = "openquote.run/0";
    internal const string RunFormatSets = "openquote.run/1";
    private static readonly string[] RunFormats = [RunFormat, RunFormatSets];

    // runs/<yyyy>/<change-id>.<device>.json
    [GeneratedRegex(@"^runs/[0-9]{4}/(?<id>[0-9a-f-]{36})\.(?<device>[a-z0-9]{4,16})\.json\z")]
    private static partial Regex RunPath();

    private static bool IsRunPath(string path) => RunPath().IsMatch(path);

    /// <summary>
    /// Reads a run record back into the run it records. The report form it names must be in the
    /// vault: a run is only meaningful against the form it was produced from.
    /// </summary>
    private static Definition<KeptRun> ParseRun(VaultFile file, IReadOnlyList<ReportDefinition> reports)
    {
        if (!TryRoot(file, RunFormats, out var root, out var error)) return new(null, error);
        var sets = TryString(root, "format", out var format) && format == RunFormatSets;
        var path = RunPath().Match(file.Path);

        if (!TryString(root, "id", out var id) || !TryString(root, "device", out var device)
            || !TryString(root, "at", out var atText)
            || !DateTimeOffset.TryParseExact(atText, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "a run record needs an id, a device and a time with its offset");
        if (id != path.Groups["id"].Value || device != path.Groups["device"].Value)
            return Bad<KeptRun>(file, UnreadableReason.NameMismatch, $"the name should be {id}.{device}.json");

        if (!root.TryGetProperty("report", out var reportJson) || reportJson.ValueKind != JsonValueKind.Object
            || !TryString(reportJson, "report", out var name) || !TryInt(reportJson, "version", out var version))
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "a run record names its report form and version");
        if (reports.FirstOrDefault(r => r.Name == name && r.Version == version) is not { } report)
            return Bad<KeptRun>(file, UnreadableReason.Invalid, $"report form {name} v{version} is not in the vault");

        var counted = new List<ReportScheme>();
        var schemes = root.TryGetProperty("schemes", out var schemesJson) && schemesJson.ValueKind == JsonValueKind.Object
            ? schemesJson : default;
        foreach (var schemeName in report.Schemes)
        {
            var scheme = default(JsonElement);
            var hasScheme = schemes.ValueKind == JsonValueKind.Object
                && schemes.TryGetProperty(schemeName, out scheme) && scheme.ValueKind == JsonValueKind.Object;
            var crosswalks = new List<string>();
            var boundaries = new List<Classification.SchemeBoundary>();
            if (hasScheme && scheme.TryGetProperty("crosswalks", out var applied))
            {
                if (!TryIds(applied, out var list)) return Bad<KeptRun>(file, UnreadableReason.Invalid, "crosswalks must be a list of names");
                crosswalks.AddRange(list);
            }
            var schemeVersion = report.VersionOf(schemeName);
            if (schemeVersion is null)
            {
                // A form that counts in the version in force: the run says which version that was.
                if (!hasScheme || !TryInt(scheme, "version", out var inForce) || inForce < 1)
                    return Bad<KeptRun>(file, UnreadableReason.Invalid, "a run of a form counting in the version in force names the version it counted in");
                schemeVersion = inForce;
            }
            if (hasScheme && scheme.TryGetProperty("boundaries", out var boundariesJson))
            {
                if (boundariesJson.ValueKind != JsonValueKind.Array)
                    return Bad<KeptRun>(file, UnreadableReason.Invalid, "boundaries must be a list");
                foreach (var b in boundariesJson.EnumerateArray())
                {
                    if (b.ValueKind != JsonValueKind.Object || !TryString(b, "date", out var dayText)
                        || !DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                        || !TryVersionOrNull(b, "from", out var before) || !TryVersionOrNull(b, "to", out var after))
                        return Bad<KeptRun>(file, UnreadableReason.Invalid, "a boundary has a date and the versions before and from it (or null)");
                    boundaries.Add(new Classification.SchemeBoundary(day, before, after));
                }
            }
            counted.Add(new ReportScheme(schemeName, schemeVersion.Value, crosswalks, boundaries));
        }
        report = report.Counted(counted.ToDictionary(c => c.Scheme, c => c.Version, StringComparer.Ordinal));

        if (!root.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.Object
            || !TryString(period, "from", out var fromText) || !TryString(period, "to", out var toText)
            || !DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(toText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "period must give from and to as calendar dates");

        var cells = new List<ReportCell>();
        if (!root.TryGetProperty("cells", out var cellsJson) || cellsJson.ValueKind != JsonValueKind.Array)
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "a run record lists its cells");
        foreach (var cell in cellsJson.EnumerateArray())
        {
            if (cell.ValueKind != JsonValueKind.Object || !cell.TryGetProperty("records", out var records) || !TryIds(records, out var ids))
                return Bad<KeptRun>(file, UnreadableReason.Invalid, "a cell lists its records");
            if ((sets ? KeyOf(cell, report) : RowAndColumnOf(cell, report)) is not { } key)
                return Bad<KeptRun>(file, UnreadableReason.Invalid, sets
                    ? "a cell's key holds a code for each classified dimension and a string or null for each other, in order"
                    : "a cell has a row, a column (or null) and its records");
            cells.Add(new ReportCell(key, ids));
        }

        if (!TrySet(root, "pending", out var pending) || !TrySet(root, "unmapped", out var unmapped) || !TrySet(root, "total", out var total))
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "pending, unmapped and total each list their records");
        IReadOnlyList<string> blank = [];
        IReadOnlyList<string> conflicted = [];
        if (sets)
        {
            if (!TrySet(root, "blank", out blank) || !TrySet(root, "conflicted", out conflicted))
                return Bad<KeptRun>(file, UnreadableReason.Invalid, "blank and conflicted each list their records");
        }
        else if (root.TryGetProperty("blank", out _))
        {
            // Format 0 may list blank records as a part of unmapped; they are read apart, as format 1 keeps them.
            if (!TrySet(root, "blank", out blank)) return Bad<KeptRun>(file, UnreadableReason.Invalid, "blank lists its records");
            var within = blank.ToHashSet(StringComparer.Ordinal);
            if (!within.IsSubsetOf(unmapped))
                return Bad<KeptRun>(file, UnreadableReason.Invalid, "in format 0, every blank record is also unmapped");
            unmapped = [.. unmapped.Where(r => !within.Contains(r))];
        }

        Dictionary<string, IReadOnlyList<string>>? people = null;
        if (root.TryGetProperty("people", out var peopleJson))
        {
            if (peopleJson.ValueKind != JsonValueKind.Object)
                return Bad<KeptRun>(file, UnreadableReason.Invalid, "people maps each record to its subjects");
            people = new(StringComparer.Ordinal);
            foreach (var entry in peopleJson.EnumerateObject())
            {
                if (!TryIds(entry.Value, out var subjects))
                    return Bad<KeptRun>(file, UnreadableReason.Invalid, "people maps each record to its subjects");
                people[entry.Name] = subjects;
            }
        }

        var run = new ReportRun(report, from, to, counted, cells, pending, unmapped, blank, conflicted, people);
        // Each record once in all of them — or, counting every value, once in each cell and in at most one set.
        IEnumerable<IReadOnlyList<string>> lists = [.. cells.Select(c => c.Records), pending, unmapped, blank, conflicted];
        IReadOnlyList<string>[] apart = [pending, unmapped, blank, conflicted];
        var once = run.Multiple
            ? lists.All(l => l.Distinct(StringComparer.Ordinal).Count() == l.Count)
                && apart.Sum(s => s.Count) == apart.SelectMany(s => s).Distinct(StringComparer.Ordinal).Count()
            : lists.Sum(l => l.Count) == run.Total.Count;
        if (!once || !run.Total.SequenceEqual(total.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || total.Count != run.Total.Count)
            return Bad<KeptRun>(file, UnreadableReason.Invalid, run.Multiple
                ? "the total is not every record in the cells, pending, unmapped, blank and conflicted, each once in a cell and in at most one set"
                : "the total is not the cells plus pending, unmapped, blank and conflicted, each record once");
        if ((root.TryGetProperty("multiple", out var multiple) && multiple.ValueKind == JsonValueKind.True) != run.Multiple)
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "multiple says whether the form counts every value of a field, as the form does");
        if (people is not null && !people.Keys.Order(StringComparer.Ordinal).SequenceEqual(run.Total, StringComparer.Ordinal))
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "people must name exactly the records in the total");
        return new(new KeptRun(id, device, at, file.Path, run), null);
    }

    // Format 1: one place per dimension — a code where it is classified, a string or null elsewhere,
    // and null where the subjects a record is about have no single value.
    private static string?[]? KeyOf(JsonElement cell, ReportDefinition report)
    {
        if (!cell.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.Array
            || key.GetArrayLength() != report.Dimensions.Count) return null;
        var places = new string?[report.Dimensions.Count];
        var i = 0;
        foreach (var place in key.EnumerateArray())
        {
            if (place.ValueKind == JsonValueKind.String) places[i] = place.GetString();
            else if (place.ValueKind != JsonValueKind.Null || (report.Dimensions[i].Classified && !report.Dimensions[i].OfSubject)) return null;
            i++;
        }
        return places;
    }

    // Format 0: a row code and a column (a string or null); the key has the column only when the form does.
    private static string?[]? RowAndColumnOf(JsonElement cell, ReportDefinition report)
    {
        if (!report.RowsAndColumn || !TryString(cell, "row", out var row) || !cell.TryGetProperty("column", out var column)
            || column.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return null;
        var value = column.ValueKind == JsonValueKind.Null ? null : column.GetString();
        if (report.Dimensions.Count == 1) return value is null ? [row] : null;
        return [row, value];
    }

    private static bool TryVersionOrNull(JsonElement obj, string name, out int? version)
    {
        version = null;
        if (!obj.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var v) || v < 1) return false;
        version = v;
        return true;
    }

    private static bool TrySet(JsonElement root, string name, out IReadOnlyList<string> ids)
    {
        ids = [];
        return root.TryGetProperty(name, out var set) && set.ValueKind == JsonValueKind.Object
            && set.TryGetProperty("records", out var records) && TryIds(records, out ids);
    }

    private static bool TryIds(JsonElement array, out IReadOnlyList<string> ids)
    {
        ids = [];
        if (array.ValueKind != JsonValueKind.Array) return false;
        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            list.Add(item.GetString()!);
        }
        ids = list;
        return true;
    }
}
