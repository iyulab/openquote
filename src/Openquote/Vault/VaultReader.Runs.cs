using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Openquote.Reports;

namespace Openquote.Vault;

public static partial class VaultReader
{
    internal const string RunFormat = "openquote.run/0";

    // runs/<yyyy>/<change-id>.<device>.json
    [GeneratedRegex(@"^runs/[0-9]{4}/(?<id>[0-9a-f-]{36})\.(?<device>[a-z0-9]{4,16})\.json$")]
    private static partial Regex RunPath();

    private static bool IsRunPath(string path) => RunPath().IsMatch(path);

    /// <summary>
    /// Reads a run record back into the run it records. The report form it names must be in the
    /// vault: a run is only meaningful against the form it was produced from.
    /// </summary>
    private static Definition<KeptRun> ParseRun(VaultFile file, IReadOnlyList<ReportDefinition> reports)
    {
        if (!TryRoot(file, RunFormat, out var root, out var error)) return new(null, error);
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

        var crosswalks = new List<string>();
        if (root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Object
            && schemes.TryGetProperty(report.RowScheme, out var scheme) && scheme.ValueKind == JsonValueKind.Object
            && scheme.TryGetProperty("crosswalks", out var applied))
        {
            if (!TryIds(applied, out var list)) return Bad<KeptRun>(file, UnreadableReason.Invalid, "crosswalks must be a list of names");
            crosswalks.AddRange(list);
        }

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
            if (cell.ValueKind != JsonValueKind.Object || !TryString(cell, "row", out var row)
                || !cell.TryGetProperty("column", out var column)
                || column.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || !cell.TryGetProperty("records", out var records) || !TryIds(records, out var ids))
                return Bad<KeptRun>(file, UnreadableReason.Invalid, "a cell has a row, a column (or null) and its records");
            cells.Add(new ReportCell(row, column.ValueKind == JsonValueKind.Null ? null : column.GetString(), ids));
        }

        if (!TrySet(root, "pending", out var pending) || !TrySet(root, "unmapped", out var unmapped) || !TrySet(root, "total", out var total))
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "pending, unmapped and total each list their records");

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

        var run = new ReportRun(report, from, to, crosswalks, cells, pending, unmapped, people);
        if (!run.Total.SequenceEqual(total.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "the total is not the cells plus pending plus unmapped");
        if (people is not null && !people.Keys.Order(StringComparer.Ordinal).SequenceEqual(run.Total, StringComparer.Ordinal))
            return Bad<KeptRun>(file, UnreadableReason.Invalid, "people must name exactly the records in the total");
        return new(new KeptRun(id, device, at, file.Path, run), null);
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
