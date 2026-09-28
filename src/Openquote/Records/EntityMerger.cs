using System.Text.Json;
using Openquote.Vault;

namespace Openquote.Records;

/// <summary>
/// Merges change files into entities. For each field, a change that another change of the same
/// entity already saw (through <see cref="Change.Base"/>, transitively) is superseded. If exactly
/// one change remains, its value is current; if more remain, the field is in conflict.
/// </summary>
public static class EntityMerger
{
    /// <summary>Merges <paramref name="changes"/> into one entity per (type, id).</summary>
    public static IReadOnlyDictionary<EntityRef, Entity> Merge(IEnumerable<Change> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return changes
            .GroupBy(c => c.Entity)
            .ToDictionary(g => g.Key, g => MergeOne(g.Key, [.. g.OrderBy(c => c.Id, StringComparer.Ordinal)]));
    }

    private static Entity MergeOne(EntityRef reference, List<Change> changes)
    {
        var graph = new ChangeGraph(changes);

        if (changes.Any(c => c.Op == ChangeOp.Destroy))
            return new Entity(reference, changes, graph, destroyed: true,
                new Dictionary<string, JsonElement>(), new Dictionary<string, IReadOnlyList<FieldHead>>());

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var conflicts = new Dictionary<string, IReadOnlyList<FieldHead>>(StringComparer.Ordinal);

        foreach (var field in changes.SelectMany(c => c.Fields.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var heads = graph.Heads(changes.Where(c => c.Fields.ContainsKey(field)).ToList());
            fields[field] = heads[^1].Fields[field]; // heads keep ascending id order
            if (heads.Count > 1)
                conflicts[field] = heads.Select(h => new FieldHead(h.Id, h.Device, h.Fields[field])).ToArray();
        }

        return new Entity(reference, changes, graph, destroyed: false, fields, conflicts);
    }
}
