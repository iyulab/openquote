using System.Text.Json;
using Openquote.Vault;

namespace Openquote.Records;

/// <summary>A value one change set for a field, among several that did not see each other.</summary>
public sealed record FieldHead(string ChangeId, string Device, JsonElement Value);

/// <summary>
/// The current state of an entity, merged from all of its changes. Nothing here is stored: it is
/// rebuilt from the change files whenever they are read.
/// </summary>
public sealed class Entity
{
    internal Entity(EntityRef reference, IReadOnlyList<Change> changes, bool destroyed,
        IReadOnlyDictionary<string, JsonElement> fields, IReadOnlyDictionary<string, IReadOnlyList<FieldHead>> conflicts)
    {
        Reference = reference;
        Changes = changes;
        Destroyed = destroyed;
        Fields = fields;
        Conflicts = conflicts;
    }

    /// <summary>The entity's type and id.</summary>
    public EntityRef Reference { get; }

    /// <summary>The entity's changes, oldest id first.</summary>
    public IReadOnlyList<Change> Changes { get; }

    /// <summary>True once a person has destroyed the entity; it then has no fields.</summary>
    public bool Destroyed { get; }

    /// <summary>
    /// The current value of every field a change has set. A field set to JSON <c>null</c> was
    /// cleared on purpose. When a field is in <see cref="Conflicts"/>, the value here is the head
    /// with the highest change id — a deterministic pick, not a decision.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Fields { get; }

    /// <summary>
    /// Fields that two or more changes set without seeing each other. Every head is kept so a
    /// person can choose; none is silently lost.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FieldHead>> Conflicts { get; }
}
