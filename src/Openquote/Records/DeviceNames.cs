using System.Text.Json;

namespace Openquote.Records;

/// <summary>
/// The names people give to the devices that write to a vault. Device ids are made for file names,
/// not for people; a device is named by a <c>device</c> entity it created, whose <c>name</c> field
/// every device sharing the vault reads. Any device may rename it — the entity stays the one its
/// creator made.
/// </summary>
public static class DeviceNames
{
    /// <summary>The entity type that names a device.</summary>
    public const string EntityType = "device";

    /// <summary>The field that holds the name.</summary>
    public const string NameField = "name";

    /// <summary>
    /// The entity that names <paramref name="device"/>: the one it created, or, should it have
    /// created several, the latest. Null when the device has none.
    /// </summary>
    public static Entity? EntityOf(IEnumerable<Entity> entities, string device)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return entities
            .Where(e => e.Reference.Type == EntityType && !e.Destroyed && e.Changes[0].Device == device)
            .MaxBy(e => e.Reference.Id, StringComparer.Ordinal);
    }

    /// <summary>Every named device's name, by device id. Blank names count as no name.</summary>
    public static IReadOnlyDictionary<string, string> Of(IEnumerable<Entity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var device in entities
                     .Where(e => e.Reference.Type == EntityType && !e.Destroyed)
                     .OrderBy(e => e.Reference.Id, StringComparer.Ordinal))
        {
            if (device.Fields.TryGetValue(NameField, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString()!.Trim() is { Length: > 0 } name)
                names[device.Changes[0].Device] = name;
            else
                names.Remove(device.Changes[0].Device);
        }
        return names;
    }
}
