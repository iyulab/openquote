using System.Text.Json;

namespace Openquote.Vault;

/// <summary>What a change does to its entity.</summary>
public enum ChangeOp
{
    /// <summary>Brings the entity into existence with its initial fields.</summary>
    Create,

    /// <summary>Sets the listed fields.</summary>
    Update,

    /// <summary>Sets a coded field to a value from a newer classification version, confirmed by a person.</summary>
    Reclassify,

    /// <summary>Records that a person destroyed the entity. Carries no fields.</summary>
    Destroy,
}

/// <summary>The entity a change applies to.</summary>
public sealed record EntityRef(string Type, string Id);

/// <summary>
/// One immutable change file. A vault never edits a change: every edit is a new change whose
/// <see cref="Base"/> names the changes its writer had already seen for the same entity.
/// </summary>
public sealed record Change(
    string Id,
    string Device,
    DateTimeOffset At,
    EntityRef Entity,
    ChangeOp Op,
    IReadOnlyList<string> Base,
    IReadOnlyDictionary<string, JsonElement> Fields,
    IReadOnlyDictionary<string, string> Source,
    string Path);
