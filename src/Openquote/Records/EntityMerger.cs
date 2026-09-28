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
        var byId = changes.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var ancestors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        HashSet<string> AncestorsOf(string id)
        {
            if (ancestors.TryGetValue(id, out var known)) return known;
            var result = new HashSet<string>(StringComparer.Ordinal);
            ancestors[id] = result; // guards against a malformed cycle in base references
            foreach (var parent in byId[id].Base)
            {
                if (!byId.ContainsKey(parent)) continue; // not synced here yet
                result.Add(parent);
                result.UnionWith(AncestorsOf(parent));
            }
            return result;
        }

        if (changes.Any(c => c.Op == ChangeOp.Destroy))
            return new Entity(reference, changes, destroyed: true,
                new Dictionary<string, JsonElement>(), new Dictionary<string, IReadOnlyList<FieldHead>>());

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var conflicts = new Dictionary<string, IReadOnlyList<FieldHead>>(StringComparer.Ordinal);

        foreach (var field in changes.SelectMany(c => c.Fields.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var setters = changes.Where(c => c.Fields.ContainsKey(field)).ToList();
            var heads = setters
                .Where(c => !setters.Any(other => other.Id != c.Id && AncestorsOf(other.Id).Contains(c.Id)))
                .ToList(); // already in ascending id order

            fields[field] = heads[^1].Fields[field];
            if (heads.Count > 1)
                conflicts[field] = heads.Select(h => new FieldHead(h.Id, h.Device, h.Fields[field])).ToArray();
        }

        return new Entity(reference, changes, destroyed: false, fields, conflicts);
    }
}
