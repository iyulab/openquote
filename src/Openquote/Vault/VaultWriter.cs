using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Openquote.Classification;
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

    /// <summary>A new record of <paramref name="type"/> (a session, for example) kept in its subject's folder.</summary>
    public VaultFile CreateInSubject(string subjectId, string type, IReadOnlyDictionary<string, JsonNode?> fields,
        IReadOnlyDictionary<string, string>? sources = null) =>
        CreateIn("subjects", subjectId, type, fields, sources);

    /// <summary>
    /// A new group: several subjects served together. Its id is the id of this change, and its
    /// folder holds the records kept for the group — a subject's folder
    /// never carries a record that names other subjects.
    /// </summary>
    public VaultFile CreateGroup(IReadOnlyDictionary<string, JsonNode?> fields, IReadOnlyDictionary<string, string>? sources = null)
    {
        var (id, at) = Stamp();
        return ChangeFile(id, at, new EntityRef("group", id), "create", [], fields, sources, $"groups/{id}");
    }

    /// <summary>
    /// A new record of <paramref name="type"/> (a session, for example) kept in its group's folder. A
    /// session lists the subjects who took part in <see cref="Entity.AttendeesField"/>.
    /// </summary>
    public VaultFile CreateInGroup(string groupId, string type, IReadOnlyDictionary<string, JsonNode?> fields,
        IReadOnlyDictionary<string, string>? sources = null) =>
        CreateIn("groups", groupId, type, fields, sources);

    private VaultFile CreateIn(string container, string ownerId, string type, IReadOnlyDictionary<string, JsonNode?> fields,
        IReadOnlyDictionary<string, string>? sources)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        ArgumentException.ThrowIfNullOrEmpty(type);
        if (type is "subject" or "group" or "practitioner" or DeviceNames.EntityType)
            throw new ArgumentException($"a {type} is not kept in another entity's folder", nameof(type));
        var (id, at) = Stamp();
        return ChangeFile(id, at, new EntityRef(type, id), "create", [], fields, sources, $"{container}/{ownerId}");
    }

    /// <summary>
    /// A new record of <paramref name="type"/> kept in the activity folder: work about no subject or group, such
    /// as a training given or attended. A subject, group, practitioner or device name has a folder of its own.
    /// </summary>
    public VaultFile CreateActivity(string type, IReadOnlyDictionary<string, JsonNode?> fields, IReadOnlyDictionary<string, string>? sources = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        if (type is "subject" or "group" or "practitioner" or DeviceNames.EntityType)
            throw new ArgumentException($"a {type} is not kept in the activity folder", nameof(type));
        var (id, at) = Stamp();
        return ChangeFile(id, at, new EntityRef(type, id), "create", [], fields, sources, "activities");
    }

    /// <summary>A new practitioner.</summary>
    public VaultFile CreatePractitioner(IReadOnlyDictionary<string, JsonNode?> fields)
    {
        var (id, at) = Stamp();
        return ChangeFile(id, at, new EntityRef("practitioner", id), "create", [], fields, null, "practitioners");
    }

    /// <summary>
    /// A new device entity: the name people see for the device this writer writes as. See
    /// <see cref="DeviceNames"/>.
    /// </summary>
    public VaultFile CreateDevice(IReadOnlyDictionary<string, JsonNode?> fields)
    {
        var (id, at) = Stamp();
        return ChangeFile(id, at, new EntityRef(DeviceNames.EntityType, id), "create", [], fields, null, "devices");
    }

    /// <summary>
    /// Sets <paramref name="fields"/> on <paramref name="entity"/>. The change names every current
    /// head as its base: it has seen the whole entity, so it also settles any conflict on the
    /// fields it sets.
    /// </summary>
    public VaultFile Update(Entity entity, IReadOnlyDictionary<string, JsonNode?> fields, IReadOnlyDictionary<string, string>? sources = null) =>
        Follow(entity, "update", fields, sources);

    /// <summary>
    /// A person's choice for a record waiting in <paramref name="choice"/>'s scheme version: sets
    /// <paramref name="field"/> to that code. The record must be pending there
    /// (<see cref="Entity.Classify"/>) and the code one of its candidates; anything else is refused,
    /// since a change file cannot be taken back. In a field holding several values, the choice says
    /// which value comes first: it is marked primary, and no other value is lost.
    /// </summary>
    /// <exception cref="ArgumentException">The record is not waiting in that version, or the code is not one of its candidates.</exception>
    public VaultFile Reclassify(Entity entity, string field, CodedValue choice, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(choice);
        var resolution = entity.Classify(field, choice.Scheme, choice.Version, catalog);
        if (resolution.Kind != ResolutionKind.Pending)
            throw new ArgumentException($"the record's {field} is not waiting for a choice in {choice.Scheme} version {choice.Version}", nameof(choice));
        if (!resolution.Candidates.Contains(choice.Code, StringComparer.Ordinal))
            throw new ArgumentException($"{choice.Code} is not one of the codes the record's {field} waits for: {string.Join(", ", resolution.Candidates)}", nameof(choice));
        var current = entity.ClassifiedValues(field, choice.Scheme, choice.Version, catalog)!;
        if (!current.Many)
            return Follow(entity, "reclassify", new Dictionary<string, JsonNode?> { [field] = new CodedValues([choice], null).ToJson() }, null);

        // Several values: the primary one waits among codes and the choice replaces it, or none is
        // marked and the one the choice comes from becomes primary — kept as entered when it already
        // carries to the choice, replaced by it when it waited among codes. The others stay as they are.
        var values = current.Values.ToArray();
        var from = current.Marked ?? Array.FindIndex(values, v => catalog.Reaches(v, choice.Scheme, choice.Version)
            && catalog.Resolve(v, choice.Scheme, choice.Version) is var r
            && (r.Kind == ResolutionKind.Assigned ? r.Code == choice.Code : r.Candidates.Contains(choice.Code, StringComparer.Ordinal)));
        if (catalog.Resolve(values[from], choice.Scheme, choice.Version).Kind != ResolutionKind.Assigned) values[from] = choice;
        return Follow(entity, "reclassify", new Dictionary<string, JsonNode?> { [field] = new CodedValues(values, from).ToJson() }, null);
    }

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
