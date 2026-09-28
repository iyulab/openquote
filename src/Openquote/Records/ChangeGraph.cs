using Openquote.Vault;

namespace Openquote.Records;

/// <summary>The "has seen" relation among one entity's changes: a change has seen every change in its base, transitively.</summary>
internal sealed class ChangeGraph
{
    private readonly Dictionary<string, Change> _byId;
    private readonly Dictionary<string, HashSet<string>> _ancestors = new(StringComparer.Ordinal);

    public ChangeGraph(IEnumerable<Change> changes) =>
        _byId = changes.ToDictionary(c => c.Id, StringComparer.Ordinal);

    public bool HasSeen(string change, string other) => AncestorsOf(change).Contains(other);

    /// <summary>
    /// Of <paramref name="candidates"/>, those that no other candidate has seen — the current heads.
    /// Order is preserved.
    /// </summary>
    public List<Change> Heads(IReadOnlyList<Change> candidates) =>
        candidates.Where(c => !candidates.Any(other => other.Id != c.Id && HasSeen(other.Id, c.Id))).ToList();

    private HashSet<string> AncestorsOf(string id)
    {
        if (_ancestors.TryGetValue(id, out var known)) return known;
        var result = new HashSet<string>(StringComparer.Ordinal);
        _ancestors[id] = result; // guards against a malformed cycle in base references
        foreach (var parent in _byId[id].Base)
        {
            if (!_byId.ContainsKey(parent)) continue; // not synced here yet
            result.Add(parent);
            result.UnionWith(AncestorsOf(parent));
        }
        return result;
    }
}
