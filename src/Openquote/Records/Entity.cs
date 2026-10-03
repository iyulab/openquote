using System.Text.Json;
using Openquote.Classification;
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

    /// <summary>
    /// The subject whose folder holds this entity: its own id for a subject, the subject it was
    /// recorded under for any record kept in that folder, and null otherwise. Moving a subject's
    /// folder moves everything recorded under it.
    /// </summary>
    public string? Subject
    {
        get
        {
            var parts = Changes[0].Path.Split('/');
            return parts is ["subjects", var subject, _] ? subject : null;
        }
    }

    /// <summary>
    /// The group whose folder holds this entity: its own id for a group, the group it was recorded
    /// under for any record kept in that folder, and null otherwise. A session held by a group names the
    /// subjects who took part in its <c>attendees</c> field.
    /// </summary>
    public string? Group
    {
        get
        {
            var parts = Changes[0].Path.Split('/');
            return parts is ["groups", var group, _] ? group : null;
        }
    }

    /// <summary>
    /// The subjects this entity is about: the subject itself, the subject whose folder holds it,
    /// or — for an entity held by a group — the subjects listed in its <c>attendees</c> field.
    /// Distinct and ordered; empty when none are known.
    /// </summary>
    public IReadOnlyList<string> People
    {
        get
        {
            if (Subject is { } subject) return [subject];
            if (Group is null || !Fields.TryGetValue(AttendeesField, out var attendees) || attendees.ValueKind != JsonValueKind.Array) return [];
            return [.. attendees.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 })
                .Select(a => a.GetString()!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The field of a group-held entity that lists the subjects who took part.</summary>
    public const string AttendeesField = "attendees";

    /// <summary>The entity's changes, oldest id first.</summary>
    public IReadOnlyList<Change> Changes { get; }

    /// <summary>
    /// The changes no other change of this entity has seen, in ascending id order. A new change
    /// that names all of them as its base has seen everything, and so settles any conflict.
    /// </summary>
    public IReadOnlyList<string> Heads => _heads ??= [.. _graph.Heads(Changes).Select(c => c.Id)];

    private IReadOnlyList<string>? _heads;

    /// <summary>
    /// The changes this entity's changes name in their base that this device does not hold —
    /// written elsewhere and not synced here yet, or unreadable here. Until they arrive, a change
    /// cannot be seen to have seen what came before them, so a field may show as in
    /// <see cref="Conflicts"/> that is only waiting for a missing link. Ascending id order; empty
    /// when every link is present.
    /// </summary>
    public IReadOnlyList<string> MissingBase => _missingBase ??= [.. Changes
        .SelectMany(c => c.Base)
        .Where(id => !Changes.Any(c => c.Id == id))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)];

    private IReadOnlyList<string>? _missingBase;

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

    /// <summary>
    /// The change that wrote <paramref name="field"/>'s current value — the latest of the changes that set it,
    /// ties going to the highest id as in <see cref="Fields"/>. Null when no change set it or the entity is destroyed.
    /// </summary>
    internal Change? LastWriteOf(string field)
    {
        if (Destroyed) return null;
        var setters = Changes.Where(c => c.Fields.ContainsKey(field)).ToList();
        return setters.Count == 0 ? null : _graph.Heads(setters)[^1];
    }

    /// <summary>
    /// Whether <paramref name="field"/> holds a value now: it has been set and not cleared. A record
    /// without one is <em>blank</em> in that field — a report run lists it in
    /// <see cref="Reports.ReportRun.Blank"/> and an export leaves its cell empty.
    /// </summary>
    public bool HasValue(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Fields.TryGetValue(field, out var current) && current.ValueKind != JsonValueKind.Null
            && !(current.ValueKind == JsonValueKind.Array && current.GetArrayLength() == 0);
    }

    /// <summary>
    /// Where this entity's <paramref name="field"/> lands in <paramref name="version"/> of
    /// <paramref name="scheme"/>: its most recent value entered in that scheme at that version or
    /// an earlier one — or in a scheme that extends it, counted as the item it is anchored to —
    /// carried forward by <paramref name="catalog"/>. In a field holding several values, the value
    /// marked primary (or the only one) is the one carried; with several and none marked, the result
    /// is pending among the codes they carry to, until a person says which comes first. A pending
    /// result lists the codes a person chooses from — the only values
    /// <see cref="VaultWriter.Reclassify"/> accepts. Unmapped when the field holds no value of that
    /// scheme or the entity is destroyed. Report runs place records by the same rule.
    /// </summary>
    public Resolution Classify(string field, string scheme, int version, SchemeCatalog catalog)
    {
        if (ClassifiedValues(field, scheme, version, catalog) is not { } values)
            return new Resolution(ResolutionKind.Unmapped, null, [], []);
        if (values.Primary is { } primary)
            return catalog.Reaches(primary, scheme, version)
                ? catalog.Resolve(primary, scheme, version)
                : new Resolution(ResolutionKind.Unmapped, null, [], []);
        // Several values and none marked: a person picks among the codes they carry to — unless they
        // all carry to one code, which then needs no choice, or to none.
        var each = values.Values.Where(v => catalog.Reaches(v, scheme, version)).Select(v => catalog.Resolve(v, scheme, version)).ToList();
        string[] candidates = [.. each.SelectMany(r => r.Kind == ResolutionKind.Assigned ? [r.Code!] : r.Candidates)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] crosswalks = [.. each.SelectMany(r => r.Crosswalks).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (candidates.Length == 0) return new Resolution(ResolutionKind.Unmapped, null, [], crosswalks);
        if (candidates.Length == 1 && each.All(r => r.Kind == ResolutionKind.Assigned))
            return new Resolution(ResolutionKind.Assigned, candidates[0], [], crosswalks);
        return new Resolution(ResolutionKind.Pending, null, candidates, crosswalks);
    }

    /// <summary>
    /// The values <see cref="Classify"/> reads: the most recent value of <paramref name="field"/> that
    /// holds a value reaching <paramref name="version"/> of <paramref name="scheme"/>. Null when there
    /// is none or the entity is destroyed.
    /// </summary>
    public CodedValues? ClassifiedValues(string field, string scheme, int version, SchemeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(catalog);
        var value = LatestValue(field, v => CodedValues.From(v) is { } c && c.Values.Any(x => catalog.Reaches(x, scheme, version)));
        return value is { } v ? CodedValues.From(v) : null;
    }
}
