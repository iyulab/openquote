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
    private readonly ChangeGraph _graph;

    internal Entity(EntityRef reference, IReadOnlyList<Change> changes, ChangeGraph graph, bool destroyed,
        IReadOnlyDictionary<string, JsonElement> fields, IReadOnlyDictionary<string, IReadOnlyList<FieldHead>> conflicts)
    {
        _graph = graph;
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

    /// <summary>
    /// The changes no other change of this entity has seen, in ascending id order. A new change
    /// that names all of them as its base has seen everything, and so settles any conflict.
    /// </summary>
    public IReadOnlyList<string> Heads => _heads ??= [.. _graph.Heads(Changes).Select(c => c.Id)];

    private IReadOnlyList<string>? _heads;

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

    /// <summary>
    /// The most recent value of <paramref name="field"/> among the values that satisfy
    /// <paramref name="accept"/>, ignoring any later value that does not. Earlier values are never
    /// lost, so a record reclassified to a newer version still answers with the value it had in an
    /// older one. Null when no accepted value exists or the entity is destroyed. Ties between
    /// concurrent values go to the highest change id, as in <see cref="Fields"/>.
    /// </summary>
    public JsonElement? LatestValue(string field, Func<JsonElement, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(accept);
        if (Destroyed) return null;
        var setters = Changes.Where(c => c.Fields.TryGetValue(field, out var v) && accept(v)).ToList();
        return setters.Count == 0 ? null : _graph.Heads(setters)[^1].Fields[field];
    }
}
