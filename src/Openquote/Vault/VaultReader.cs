using System.Globalization;
using System.Text.Json;

namespace Openquote.Vault;

/// <summary>What <see cref="VaultReader.Read"/> found in a vault.</summary>
public sealed record VaultContent(IReadOnlyList<Change> Changes, IReadOnlyList<UnreadableFile> Unreadable);

/// <summary>
/// Reads the change files of a vault. A file that cannot be used never stops the read: it is
/// reported in <see cref="VaultContent.Unreadable"/> and everything else is still returned.
/// </summary>
public static class VaultReader
{
    internal const string ChangeFormat = "openquote.change/0";

    /// <summary>Reads every change file among <paramref name="files"/>. Files outside the vault layout are ignored.</summary>
    public static VaultContent Read(IEnumerable<VaultFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var unreadable = new List<UnreadableFile>();
        var byId = new Dictionary<string, List<(Change Change, JsonElement Json)>>(StringComparer.Ordinal);

        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!IsChangePath(file.Path)) continue;

            var parsed = ParseChange(file);
            if (parsed.Error is { } error)
            {
                unreadable.Add(error);
                continue;
            }

            var (change, json) = parsed.Value!.Value;
            if (!byId.TryGetValue(change.Id, out var copies)) byId[change.Id] = copies = [];
            copies.Add((change, json));
        }

        var changes = new List<Change>();
        foreach (var (id, copies) in byId)
        {
            // A sync client's conflicted copy of an unchanged file is the same change twice.
            if (copies.Skip(1).All(c => JsonElement.DeepEquals(c.Json, copies[0].Json)))
            {
                changes.Add(copies[0].Change);
                continue;
            }

            unreadable.AddRange(copies.Select(c => new UnreadableFile(
                c.Change.Path, UnreadableReason.DuplicateId, $"{copies.Count} files carry id {id} with different content")));
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        unreadable.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new VaultContent(changes, unreadable);
    }

    // practitioners/<file>.json, subjects/<subject-id>/<file>.json
    private static bool IsChangePath(string path)
    {
        if (!path.EndsWith(".json", StringComparison.Ordinal)) return false;
        var parts = path.Split('/');
        return (parts.Length == 2 && parts[0] == "practitioners")
            || (parts.Length == 3 && parts[0] == "subjects");
    }

    private readonly record struct Parsed((Change, JsonElement)? Value, UnreadableFile? Error);

    private static Parsed Fail(VaultFile file, UnreadableReason reason, string detail) =>
        new(null, new UnreadableFile(file.Path, reason, detail));

    private static Parsed ParseChange(VaultFile file)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(file.Content);
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            return Fail(file, UnreadableReason.Malformed, e.Message);
        }

        if (root.ValueKind != JsonValueKind.Object)
            return Fail(file, UnreadableReason.Malformed, "the content is not a JSON object");
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != ChangeFormat)
            return Fail(file, UnreadableReason.UnknownFormat, $"expected format {ChangeFormat}");

        if (!TryString(root, "id", out var id) || !Guid.TryParseExact(id, "D", out _) || id.Any(char.IsAsciiLetterUpper))
            return Fail(file, UnreadableReason.Invalid, "id must be a lowercase hyphenated UUID");
        if (!TryString(root, "device", out var device) || !IsDevice(device))
            return Fail(file, UnreadableReason.Invalid, "device must be 4-16 lowercase letters or digits");
        if (!TryString(root, "at", out var atText)
            || !DateTimeOffset.TryParseExact(atText, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return Fail(file, UnreadableReason.Invalid, "at must be a timestamp with an offset");
        if (!root.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.Object
            || !TryString(entity, "type", out var entityType) || !TryString(entity, "id", out var entityId))
            return Fail(file, UnreadableReason.Invalid, "entity must name a type and an id");
        if (!TryString(root, "op", out var opText) || ParseOp(opText) is not { } op)
            return Fail(file, UnreadableReason.Invalid, "op must be create, update, reclassify or destroy");
        if (!root.TryGetProperty("base", out var baseArray) || baseArray.ValueKind != JsonValueKind.Array
            || baseArray.EnumerateArray().Any(b => b.ValueKind != JsonValueKind.String))
            return Fail(file, UnreadableReason.Invalid, "base must be an array of change ids");
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            return Fail(file, UnreadableReason.Invalid, "fields must be an object");

        var source = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("source", out var sourceObject))
        {
            if (sourceObject.ValueKind != JsonValueKind.Object)
                return Fail(file, UnreadableReason.Invalid, "source must be an object");
            foreach (var p in sourceObject.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not ("manual" or "suggestion"))
                    return Fail(file, UnreadableReason.Invalid, "source values must be manual or suggestion");
                source[p.Name] = p.Value.GetString()!;
            }
        }

        // A sync client may append to the name ("<id>.<device> (conflicted copy).json"), but the
        // name must still begin with the id and device the content claims.
        var stem = file.Path[(file.Path.LastIndexOf('/') + 1)..^".json".Length];
        var expected = $"{id}.{device}";
        if (!(stem == expected || (stem.StartsWith(expected, StringComparison.Ordinal) && !char.IsAsciiLetterOrDigit(stem[expected.Length]))))
            return Fail(file, UnreadableReason.NameMismatch, $"the name should begin with {expected}");

        var change = new Change(
            id,
            device,
            at,
            new EntityRef(entityType, entityId),
            op,
            baseArray.EnumerateArray().Select(b => b.GetString()!).ToArray(),
            fields.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal),
            source,
            file.Path);
        return new Parsed((change, root), null);
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = "";
        return false;
    }

    private static bool IsDevice(string device) =>
        device.Length is >= 4 and <= 16 && device.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

    private static ChangeOp? ParseOp(string op) => op switch
    {
        "create" => ChangeOp.Create,
        "update" => ChangeOp.Update,
        "reclassify" => ChangeOp.Reclassify,
        "destroy" => ChangeOp.Destroy,
        _ => null,
    };
}
