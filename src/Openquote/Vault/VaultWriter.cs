using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Openquote.Records;
using Openquote.Reports;

namespace Openquote.Vault;

/// <summary>
/// Produces new vault files for one device. Every file gets a fresh time-ordered id, so its path
/// never names an existing file; the host writes it with create-new semantics (failing rather than
/// replacing) and, for an encrypted vault, encrypts it first. The engine itself never writes to disk.
/// </summary>
public sealed class VaultWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _device;
    private readonly TimeProvider _clock;

    /// <summary>A writer for <paramref name="device"/> (4–16 lowercase letters or digits), stamping local time from <paramref name="clock"/>.</summary>
    public VaultWriter(string device, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Length is < 4 or > 16 || !device.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            throw new ArgumentException("a device id is 4-16 lowercase letters or digits", nameof(device));
        _device = device;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// A new subject. Its id is the id of this change, and its folder holds every later change to
    /// it and to its cases and sessions.
    /// </summary>
    public VaultFile CreateSubject(IReadOnlyDictionary<string, JsonNode?> fields, IReadOnlyDictionary<string, string>? sources = null)
    {
        var (id, at) = Stamp();
        return ChangeFile(id, at, new EntityRef("subject", id), "create", [], fields, sources, $"subjects/{id}");
    }

    /// <summary>A new entity of <paramref name="type"/> (a case or a session) kept in its subject's folder.</summary>
    public VaultFile CreateInSubject(string subjectId, string type, IReadOnlyDictionary<string, JsonNode?> fields,
        IReadOnlyDictionary<string, string>? sources = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        ArgumentException.ThrowIfNullOrEmpty(type);
        if (type is "subject" or "practitioner") throw new ArgumentException($"a {type} is not kept in a subject folder", nameof(type));
        var (id, at) = Stamp();
        return ChangeFile(id, at, new EntityRef(type, id), "create", [], fields, sources, $"subjects/{subjectId}");
    }

    /// <summary>A new practitioner.</summary>
    public VaultFile CreatePractitioner(IReadOnlyDictionary<string, JsonNode?> fields)
    {
        var (id, at) = Stamp();
        return ChangeFile(id, at, new EntityRef("practitioner", id), "create", [], fields, null, "practitioners");
    }

    /// <summary>
    /// Sets <paramref name="fields"/> on <paramref name="entity"/>. The change names every current
    /// head as its base: it has seen the whole entity, so it also settles any conflict on the
    /// fields it sets.
    /// </summary>
    public VaultFile Update(Entity entity, IReadOnlyDictionary<string, JsonNode?> fields, IReadOnlyDictionary<string, string>? sources = null) =>
        Follow(entity, "update", fields, sources);

    /// <summary>A person's choice of a newer-version code for a record that was waiting for one.</summary>
    public VaultFile Reclassify(Entity entity, string field, JsonNode codedValue) =>
        Follow(entity, "reclassify", new Dictionary<string, JsonNode?> { [field] = codedValue }, null);

    /// <summary>The run record for <paramref name="run"/>, filed under the year it was produced.</summary>
    public VaultFile RunRecord(ReportRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (id, at) = Stamp();
        return new VaultFile($"runs/{at.Year.ToString("D4", CultureInfo.InvariantCulture)}/{id}.{_device}.json",
            ReportRunJson.Write(run, id, _device, at));
    }

    private VaultFile Follow(Entity entity, string op, IReadOnlyDictionary<string, JsonNode?> fields, IReadOnlyDictionary<string, string>? sources)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Destroyed) throw new InvalidOperationException("a destroyed entity takes no further changes");
        if (fields.Count == 0) throw new ArgumentException("a change sets at least one field", nameof(fields));
        var path = entity.Changes[0].Path;
        var folder = path[..path.LastIndexOf('/')];
        var (id, at) = Stamp();
        return ChangeFile(id, at, entity.Reference, op, entity.Heads, fields, sources, folder);
    }

    private (string Id, DateTimeOffset At) Stamp()
    {
        var utc = _clock.GetUtcNow();
        var at = TimeZoneInfo.ConvertTime(utc, _clock.LocalTimeZone);
        at = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, at.Offset); // the format keeps whole seconds
        return (Guid.CreateVersion7(utc).ToString("D"), at);
    }

    private VaultFile ChangeFile(string id, DateTimeOffset at, EntityRef entity, string op, IReadOnlyList<string> baseIds,
        IReadOnlyDictionary<string, JsonNode?> fields, IReadOnlyDictionary<string, string>? sources, string folder)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var json = new JsonObject
        {
            ["format"] = VaultReader.ChangeFormat,
            ["id"] = id,
            ["device"] = _device,
            ["at"] = at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["entity"] = new JsonObject { ["type"] = entity.Type, ["id"] = entity.Id },
            ["op"] = op,
            ["base"] = new JsonArray([.. baseIds.Select(b => (JsonNode)JsonValue.Create(b))]),
            ["fields"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Key, f.Value?.DeepClone()))),
        };
        if (sources is { Count: > 0 })
        {
            if (sources.Any(s => s.Value is not ("manual" or "suggestion") || !fields.ContainsKey(s.Key)))
                throw new ArgumentException("a source is manual or suggestion, for a field this change sets", nameof(sources));
            json["source"] = new JsonObject(sources.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value)));
        }
        return new VaultFile($"{folder}/{id}.{_device}.json", System.Text.Encoding.UTF8.GetBytes(json.ToJsonString(Json) + "\n"));
    }
}
