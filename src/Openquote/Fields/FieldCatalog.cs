using Openquote.Packs;

namespace Openquote.Fields;

/// <summary>What is wrong with the fields a vault's packs declare.</summary>
public enum FieldIssueKind
{
    /// <summary>Two packs declare a field of the same name for one entity type; the earlier pack's stands.</summary>
    DuplicateField,

    /// <summary>A constraint names a field no pack declares.</summary>
    ConstraintWithoutField,

    /// <summary>A constraint narrows a field of a pack it does not build on; it is not applied.</summary>
    ConstraintFromUnrelatedPack,

    /// <summary>A field ends up both required and hidden.</summary>
    HiddenRequired,
}

/// <summary>One problem with a vault's field definitions.</summary>
public sealed record FieldIssue(FieldIssueKind Kind, string Type, string Field, string Detail);

/// <summary>
/// The fields of each entity type, merged from a vault's packs: packs in the order they build on each
/// other (<see cref="PackGraph.Order"/>; packs without a manifest last), each at its highest version. A pack
/// may narrow a field of a pack it builds on — make it required or hide it — and nothing more.
/// </summary>
public sealed class FieldCatalog
{
    private readonly Dictionary<string, List<FieldDefinition>> _byType = new(StringComparer.Ordinal);

    /// <summary>A catalog with no field definitions and no packs, for a vault that declares none.</summary>
    public static FieldCatalog Empty { get; } = new([], []);

    /// <summary>Builds the catalog from a vault's field files and pack manifests.</summary>
    public FieldCatalog(IEnumerable<FieldSet> sets, IEnumerable<PackManifest> packs)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(packs);
        var graph = new PackGraph(packs);
        var order = graph.Order().ToList();
        int Rank(string pack) => order.IndexOf(pack) is var i && i >= 0 ? i : int.MaxValue;

        var latest = sets
            .GroupBy(s => (s.Pack, s.Type))
            .Select(g => g.MaxBy(s => s.Version)!)
            .OrderBy(s => Rank(s.Pack))
            .ThenBy(s => s.Pack, StringComparer.Ordinal)
            .ToList();
        var issues = new List<FieldIssue>();

        foreach (var set in latest)
        {
            if (!_byType.TryGetValue(set.Type, out var list)) _byType[set.Type] = list = [];
            foreach (var field in set.Fields)
            {
                if (list.FirstOrDefault(f => f.Name == field.Name) is { } earlier)
                    issues.Add(new(FieldIssueKind.DuplicateField, set.Type, field.Name, $"declared by {earlier.Pack} and {set.Pack}"));
                else
                    list.Add(field);
            }
        }

        foreach (var set in latest)
        {
            foreach (var constraint in set.Constraints)
            {
                var list = _byType.GetValueOrDefault(set.Type);
                var index = list?.FindIndex(f => f.Name == constraint.Name) ?? -1;
                if (index < 0)
                {
                    issues.Add(new(FieldIssueKind.ConstraintWithoutField, set.Type, constraint.Name, $"{set.Pack} narrows a field no pack declares"));
                    continue;
                }
                var field = list![index];
                if (!graph.DependsOn(set.Pack, field.Pack))
                {
                    issues.Add(new(FieldIssueKind.ConstraintFromUnrelatedPack, set.Type, constraint.Name, $"{set.Pack} does not build on {field.Pack}"));
                    continue;
                }
                list[index] = field with { Required = field.Required || constraint.Required, Hidden = field.Hidden || constraint.Hidden };
            }
        }

        foreach (var (type, list) in _byType.OrderBy(t => t.Key, StringComparer.Ordinal))
            foreach (var field in list.Where(f => f.Required && f.Hidden))
                issues.Add(new(FieldIssueKind.HiddenRequired, type, field.Name, "a required field cannot be hidden"));

        Issues = issues;
    }

    /// <summary>The problems in the merged definitions.</summary>
    public IReadOnlyList<FieldIssue> Issues { get; }

    /// <summary>The fields of <paramref name="type"/>, in pack order and then declaration order.</summary>
    public IReadOnlyList<FieldDefinition> For(string type) => _byType.GetValueOrDefault(type) ?? [];

    /// <summary>The field, or null when no pack declares it.</summary>
    public FieldDefinition? Find(string type, string name) => For(type).FirstOrDefault(f => f.Name == name);

    /// <summary>True when the field is declared as written content.</summary>
    public bool IsNarrative(string type, string field) => Find(type, field)?.Tier == FieldTier.Narrative;
}
