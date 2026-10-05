using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Openquote.Classification;

namespace Openquote.Vault;

/// <summary>
/// Produces definition files — a scheme version, a crosswalk — as <see cref="VaultReader"/> reads them,
/// for a host that keeps definitions of its own beside its packs' (a list a body extends a shared
/// scheme with, say). Each file is named by what it defines, so a version already written is a path
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
