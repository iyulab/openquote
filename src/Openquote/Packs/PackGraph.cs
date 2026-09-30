namespace Openquote.Packs;

/// <summary>The packs of a vault, each at the highest version the vault holds, and which builds on which.</summary>
public sealed class PackGraph
{
    private readonly Dictionary<string, PackManifest> _latest;

    /// <summary>Builds the graph from every manifest a vault holds.</summary>
    public PackGraph(IEnumerable<PackManifest> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _latest = packs
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.MaxBy(p => p.Version)!, StringComparer.Ordinal);
    }

    /// <summary>Each pack at the highest version the vault holds.</summary>
    public IReadOnlyCollection<PackManifest> Latest => _latest.Values;

    /// <summary>The pack at the highest version the vault holds, or null.</summary>
    public PackManifest? Find(string id) => _latest.GetValueOrDefault(id);

    /// <summary>True when <paramref name="pack"/> builds on <paramref name="other"/>, directly or through other packs.</summary>
    public bool DependsOn(string pack, string other)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([pack]);
        while (stack.Count > 0)
        {
            if (Find(stack.Pop()) is not { } current) continue;
            foreach (var dependency in current.Depends.Keys)
            {
                if (dependency == other) return true;
                if (seen.Add(dependency)) stack.Push(dependency);
            }
        }
        return false;
    }

    /// <summary>
    /// Pack ids, each after the packs it builds on. A pack is placed as soon as everything it builds on
    /// is placed, taking ids in order, so a pack can come before one whose id sorts earlier if that one
    /// still waits on something. Packs caught in a cycle come last, in id order (<see cref="PackCheck"/>
    /// reports the cycle).
    /// </summary>
    public IReadOnlyList<string> Order()
    {
        var order = new List<string>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = _latest.Keys.Order(StringComparer.Ordinal).ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(id =>
                _latest[id].Depends.Keys.All(d => placed.Contains(d) || !_latest.ContainsKey(d)));
            if (next is null)
            {
                order.AddRange(remaining);
                break;
            }
            order.Add(next);
            placed.Add(next);
            remaining.Remove(next);
        }
        return order;
    }
}
