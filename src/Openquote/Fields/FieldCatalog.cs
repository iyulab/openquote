using Openquote.Packs;
using Openquote.Records;

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

    /// <summary>
    /// Packs say an entity type is kept in folders that have none in common; the earlier packs' answer stands.
    /// <see cref="FieldIssue.Field"/> is empty.
    /// </summary>
    KeptNowhere,

    /// <summary>
    /// The field a pack says dates a record of the type is not a date field of the type; the field called
    /// <c>date</c> dates it instead.
    /// </summary>
    DatedNotADate,

    /// <summary>
    /// A pack gives a role in a case to an entity type kept on its own (a subject, a group, a practitioner or a
    /// device), which is no record of a case; the role is not applied.
    /// </summary>
    RoleOnItsOwn,
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
    private readonly List<string> _types = [];
    private readonly Dictionary<string, string> _typeLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _under = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _order = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CaseRole> _role = new(StringComparer.Ordinal);

    /// <summary>The folders an entity can be kept under, in the order <see cref="KeptUnder"/> lists them.</summary>
    public static IReadOnlyList<string> Holders { get; } = ["subject", "group"];

    /// <summary>True for the entity types that are kept on their own rather than under a subject or a group.</summary>
    public static bool IsKeptOnItsOwn(string type) => type is "subject" or "group" or "practitioner" or DeviceNames.EntityType;

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
            .ThenBy(s => s.Type, StringComparer.Ordinal)
            .ToList();
        var issues = new List<FieldIssue>();

        foreach (var set in latest)
        {
            if (!_byType.TryGetValue(set.Type, out var list))
            {
                _byType[set.Type] = list = [];
                _types.Add(set.Type);
            }
            if (set.Label is { } typeLabel) _typeLabels.TryAdd(set.Type, typeLabel);
            if (set.Order is { } place) _order.TryAdd(set.Type, place);
            if (set.Dated is { } dated) _dated.TryAdd(set.Type, dated);
            if (set.Role is { } role)
            {
                if (IsKeptOnItsOwn(set.Type))
                    issues.Add(new(FieldIssueKind.RoleOnItsOwn, set.Type, "", $"{set.Pack} says a {set.Type} {(role == CaseRole.Opens ? "opens" : "closes")} a case"));
                else
                    _role.TryAdd(set.Type, role);
            }
            if (set.Under is { } under)
            {
                var kept = _under.GetValueOrDefault(set.Type) ?? Holders;
                var narrowed = kept.Where(under.Contains).ToList();
                if (narrowed.Count == 0)
                    issues.Add(new(FieldIssueKind.KeptNowhere, set.Type, "", $"{set.Pack} keeps it under {string.Join(" and ", under)}, earlier packs under {string.Join(" and ", kept)}"));
                else
                    _under[set.Type] = narrowed;
            }
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

        foreach (var (type, dated) in _dated.OrderBy(t => t.Key, StringComparer.Ordinal).ToList())
        {
            if (Find(type, dated)?.Kind == FieldKind.Date) continue;
            issues.Add(new(FieldIssueKind.DatedNotADate, type, dated, $"{dated} is not a date field of {type}"));
            _dated.Remove(type);
        }

        Issues = issues;
    }

    /// <summary>The problems in the merged definitions.</summary>
    public IReadOnlyList<FieldIssue> Issues { get; }

    /// <summary>
    /// The entity types the packs declare fields for, in the order of the first pack that declares each (packs in
    /// the order they build on each other, a pack's types by name).
    /// </summary>
    public IReadOnlyList<string> Types => _types;

    /// <summary>What people read for <paramref name="type"/>, from the first pack that names it; null when none does.</summary>
    public string? TypeLabel(string type) => _typeLabels.GetValueOrDefault(type);

    /// <summary>
    /// The folders an entity of <paramref name="type"/> is kept in: every folder the packs that say allow, or both a
    /// subject's and a group's when none says. Empty for the types kept on their own (<see cref="IsKeptOnItsOwn"/>).
    /// </summary>
    public IReadOnlyList<string> KeptUnder(string type) =>
        IsKeptOnItsOwn(type) ? [] : _under.GetValueOrDefault(type) ?? Holders;

    /// <summary>Where a host places <paramref name="type"/> among the others, from the first pack that says; null when none does.</summary>
    public int? TypeOrder(string type) => _order.TryGetValue(type, out var place) ? place : null;

    /// <summary>
    /// The date field that says when a record of <paramref name="type"/> happened: the one the first pack that says
    /// names, or the field called <c>date</c>.
    /// </summary>
    public string DatedField(string type) => _dated.GetValueOrDefault(type) ?? "date";

    /// <summary>
    /// Whether a record of <paramref name="type"/> opens or closes a subject's case, from the first pack that says;
    /// null when none does.
    /// </summary>
    public CaseRole? TypeRole(string type) => _role.TryGetValue(type, out var role) ? role : null;

    /// <summary>The fields of <paramref name="type"/>, in pack order and then declaration order.</summary>
    public IReadOnlyList<FieldDefinition> For(string type) => _byType.GetValueOrDefault(type) ?? [];

    /// <summary>The field, or null when no pack declares it.</summary>
    public FieldDefinition? Find(string type, string name) => For(type).FirstOrDefault(f => f.Name == name);

    /// <summary>True when the field is declared as written content.</summary>
    public bool IsNarrative(string type, string field) => Find(type, field)?.Tier == FieldTier.Narrative;
}
