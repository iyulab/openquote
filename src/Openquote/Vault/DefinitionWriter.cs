using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Openquote.Classification;
using Openquote.Reports;

namespace Openquote.Vault;

/// <summary>
/// Produces definition files — a scheme version, a crosswalk, a report form — as <see cref="VaultReader"/>
/// reads them, for a host that keeps definitions of its own beside its packs' (a list a body extends a
/// shared scheme with, and a form counting by that list, say). Each file is named by what it defines, so a version already written is a path
/// that exists: the host writes with create-new semantics and, failing, reads the vault again and
/// writes the next version. The engine itself never writes to disk.
/// </summary>
public static class DefinitionWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The file of <paramref name="scheme"/>: format 1 when it extends another scheme, format 0 otherwise.
    /// </summary>
    public static VaultFile Scheme(Scheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var json = new JsonObject
        {
            ["format"] = scheme.Extends is null ? "openquote.scheme/0" : "openquote.scheme/1",
            ["scheme"] = scheme.Name,
            ["version"] = scheme.Version,
        };
        if (scheme.EffectiveFrom is { } from)
        {
            var effective = new JsonObject { ["from"] = Day(from) };
            if (scheme.EffectiveTo is { } to) effective["to"] = Day(to);
            json["effective"] = effective;
        }
        if (scheme.Extends is { } extended)
            json["extends"] = new JsonObject { ["scheme"] = extended.Scheme, ["version"] = extended.Version };
        var items = new JsonArray();
        foreach (var item in scheme.Items)
        {
            var node = new JsonObject { ["code"] = item.Code, ["label"] = item.Label };
            if (item.Parent is { } parent) node["parent"] = parent;
            if (item.Suggest) node["suggest"] = true;
            if (item.Anchor is { } anchor) node["anchor"] = anchor;
            items.Add((JsonNode)node);
        }
        json["items"] = items;
        return File($"schemes/{scheme.Name}/v{scheme.Version}.json", json);
    }

    /// <summary>
    /// The file of <paramref name="crosswalk"/>: format 1 when it leads into another scheme or states a
    /// relation for a link, format 0 otherwise.
    /// </summary>
    public static VaultFile Crosswalk(Crosswalk crosswalk)
    {
        ArgumentNullException.ThrowIfNull(crosswalk);
        var related = crosswalk.Into is not null || crosswalk.Relations.Count > 0;
        var json = new JsonObject
        {
            ["format"] = related ? "openquote.crosswalk/1" : "openquote.crosswalk/0",
            ["scheme"] = crosswalk.Scheme,
        };
        if (crosswalk.Into is { } into) json["into"] = into;
        json["from"] = crosswalk.From;
        json["to"] = crosswalk.To;
        var links = new JsonArray();
        foreach (var link in crosswalk.Links)
        {
            var pair = new JsonArray((JsonNode)JsonValue.Create(link.From), (JsonNode)JsonValue.Create(link.To));
            if (crosswalk.Relations.TryGetValue(link, out var relation)) pair.Add((JsonNode)JsonValue.Create(Relation(relation)));
            links.Add((JsonNode)pair);
        }
        json["links"] = links;
        var path = crosswalk.Into is { } other
            ? $"schemes/{crosswalk.Scheme}/v{crosswalk.From}-{other}.v{crosswalk.To}.json"
            : $"schemes/{crosswalk.Scheme}/v{crosswalk.From}-v{crosswalk.To}.json";
        return File(path, json);
    }

    /// <summary>
    /// The file of <paramref name="report"/>: format 0 when it splits by a classified field in a named
    /// version and at most one string field of the record, by month, with no filter and the default
    /// measures — what a format 0 form says; format 2 when it counts activity records too
    /// (<see cref="ReportDefinition.Activities"/>); format 1 otherwise. Throws when the form cannot be run as
    /// it stands (<see cref="ReportDefinition.Problem"/>), since the reader would not read it.
    /// </summary>
    public static VaultFile Report(ReportDefinition report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Problem() is { } problem) throw new ArgumentException(problem, nameof(report));
        var defaultMeasures = report.Sums.Count == 0 && report.Measures.SequenceEqual([ReportMeasure.Records, ReportMeasure.People]);
        var v0 = !report.Activities && report.RowsAndColumn && report.Dimensions[0].Version is not null && defaultMeasures;
        var json = new JsonObject
        {
            ["format"] = report.Activities ? "openquote.report/2" : v0 ? "openquote.report/0" : "openquote.report/1",
            ["report"] = report.Name,
            ["version"] = report.Version,
            ["label"] = report.Label,
            ["counts"] = report.Counts,
        };
        var period = new JsonObject { ["unit"] = Unit(report.Period.Unit), ["field"] = report.Period.Field };
        if (report.Period.Unit == PeriodUnit.Year && report.Period.StartMonth != 1) period["startMonth"] = report.Period.StartMonth;
        json["period"] = period;
        if (v0)
        {
            var rows = report.Dimensions[0];
            json["rows"] = new JsonObject { ["field"] = rows.Field, ["scheme"] = rows.Scheme, ["version"] = rows.Version };
            if (report.Dimensions.Count == 2) json["columns"] = new JsonObject { ["field"] = report.Dimensions[1].Field };
            return File($"reports/{report.Name}/v{report.Version}.json", json);
        }
        json["dimensions"] = new JsonArray([.. report.Dimensions.Select(d => (JsonNode)Dimension(d))]);
        if (report.Filters.Count > 0)
        {
            json["filters"] = new JsonArray([.. report.Filters.Select(f =>
            {
                var node = Dimension(f.On);
                node["in"] = new JsonArray([.. f.In.Select(v => (JsonNode)JsonValue.Create(v))]);
                return (JsonNode)node;
            })]);
        }
        if (!defaultMeasures)
        {
            json["measures"] = new JsonArray([
                .. report.Measures.Select(m => (JsonNode)JsonValue.Create(Measure(m))),
                .. report.Sums.Select(s => (JsonNode)new JsonObject { ["sum"] = s }),
            ]);
        }
        return File($"reports/{report.Name}/v{report.Version}.json", json);
    }

    private static JsonObject Dimension(ReportDimension dimension)
    {
        var node = new JsonObject { ["field"] = dimension.Field };
        if (dimension.Scheme is { } scheme)
        {
            node["scheme"] = scheme;
            node["version"] = dimension.Version is { } version ? version : "in-force";
        }
        if (dimension.OfSubject) node["of"] = "subject";
        if (dimension.All) node["values"] = "all";
        return node;
    }

    private static string Unit(PeriodUnit unit) => unit switch
    {
        PeriodUnit.Day => "day",
        PeriodUnit.Month => "month",
        PeriodUnit.Year => "year",
        PeriodUnit.Range => "range",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    private static string Measure(ReportMeasure measure) => measure switch
    {
        ReportMeasure.Records => "records",
        ReportMeasure.People => "people",
        ReportMeasure.Visits => "visits",
        _ => throw new ArgumentOutOfRangeException(nameof(measure), measure, null),
    };

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Relation(LinkRelation relation) => relation switch
    {
        LinkRelation.Equivalent => "equivalent",
        LinkRelation.Narrower => "narrower",
        LinkRelation.Broader => "broader",
        LinkRelation.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
    };

    private static VaultFile File(string path, JsonObject json) =>
        new(path, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString(Json) + "\n"));
}
