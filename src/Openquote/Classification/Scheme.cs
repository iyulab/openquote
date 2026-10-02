using System.Text.Json;
using System.Text.Json.Nodes;

namespace Openquote.Classification;

/// <summary>One item of a classification scheme. A two-level code is a path: <c>parent/child</c>.</summary>
public sealed record SchemeItem(string Code, string Label, string? Parent, bool Suggest)
{
    /// <summary>
    /// In a scheme that extends another (see <see cref="Scheme.Extends"/>), the code of the item of
    /// the extended version this item counts as wherever that scheme is counted; null otherwise.
    /// </summary>
    public string? Anchor { get; init; }
}

/// <summary>A version of a scheme, named by the scheme and the version.</summary>
public sealed record SchemeVersion(string Scheme, int Version);

/// <summary>
/// One immutable version of a classification scheme. Editing a scheme makes a new version; a
/// renamed item keeps its code, so only the label changes. <paramref name="EffectiveFrom"/> and
/// <paramref name="EffectiveTo"/> say when the version is in force, as the body that issues the scheme
/// announces it; a version without them is in force throughout. They guide what a person enters, and
/// decide what a report counts only when its form asks for the version in force rather than naming one.
/// </summary>
public sealed record Scheme(string Name, int Version, IReadOnlyList<SchemeItem> Items, DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null)
{
    /// <summary>
    /// The scheme version this one extends, or null. Every item of an extending scheme names the item
    /// of that version it counts as (<see cref="SchemeItem.Anchor"/>), so a list kept by one body adds
    /// detail beside a shared scheme without changing what the shared scheme counts.
    /// </summary>
    public SchemeVersion? Extends { get; init; }

    /// <summary>True if <paramref name="code"/> is an item of this version.</summary>
    public bool Contains(string code) => Items.Any(i => i.Code == code);
}

/// <summary>How an old item relates to the new item a crosswalk links it to.</summary>
public enum LinkRelation
{
    /// <summary>The same meaning: a value is carried without asking.</summary>
    Equivalent,

    /// <summary>The old item is narrower than the new one, which holds all of it: carried without asking.</summary>
    Narrower,

    /// <summary>The old item is broader than the new one, which holds only part of it: a person decides.</summary>
    Broader,

    /// <summary>The old item was retired; the new item is only where it went nearest. Nothing is carried.</summary>
    Retired,
}

/// <summary>
/// The links between two versions of a scheme — or from a version of one scheme to a version of
/// another (<see cref="Into"/>) — as (old code, new code) pairs. Whether a link is 1:1, N:1, 1:N or
/// N:M is not stored: it follows from the pairs.
/// </summary>
public sealed record Crosswalk(string Scheme, int From, int To, IReadOnlyList<(string From, string To)> Links)
{
    /// <summary>
    /// The scheme the links lead into: another scheme for a crosswalk across schemes (format 1), which
    /// then leads from version <see cref="From"/> of <see cref="Scheme"/> to version <see cref="To"/> of
    /// this one; null for a crosswalk between two versions of one scheme.
    /// </summary>
    public string? Into { get; init; }

    /// <summary>The scheme the links lead into: <see cref="Into"/>, or <see cref="Scheme"/> itself.</summary>
    public string TargetScheme => Into ?? Scheme;

    /// <summary>
    /// How a run names this crosswalk among those applied: <c>from-to</c> within a scheme,
    /// <c>scheme/from-scheme/to</c> across schemes.
    /// </summary>
    public string Name => Into is null ? $"{From}-{To}" : $"{Scheme}/{From}-{Into}/{To}";

    /// <summary>
    /// The relation each link states, when the crosswalk states any (format 1). A link with no stated
    /// relation is carried as every link was before relations: one link assigns, several wait for a person.
    /// </summary>
    public IReadOnlyDictionary<(string From, string To), LinkRelation> Relations { get; init; } =
        new Dictionary<(string From, string To), LinkRelation>();

    /// <summary>The new codes <paramref name="code"/> links to, in the order the file lists them.</summary>
    public IReadOnlyList<string> Targets(string code) =>
        Links.Where(l => l.From == code).Select(l => l.To).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Where <paramref name="code"/> goes in the new version: the codes a value may be carried to, and
    /// whether a person has to decide even when only one is left — a broader old item does not fit any
    /// one narrower new item on its own. Retired links carry nothing.
    /// </summary>
    public (IReadOnlyList<string> Codes, bool Review) Carry(string code)
    {
        var codes = new List<string>();
        var review = false;
        foreach (var (from, to) in Links)
        {
            if (from != code || codes.Contains(to, StringComparer.Ordinal)) continue;
            var relation = Relations.TryGetValue((from, to), out var stated) ? stated : (LinkRelation?)null;
            if (relation == LinkRelation.Retired) continue;
            if (relation == LinkRelation.Broader) review = true;
            codes.Add(to);
        }
        return (codes, review);
    }
}

/// <summary>A classified value as a record stores it: the scheme, the version in force when it was entered, and the code.</summary>
public sealed record CodedValue(string Scheme, int Version, string Code)
{
    /// <summary>Reads <c>{"scheme", "version", "code"}</c>; null for anything else, including JSON null.</summary>
    public static CodedValue? From(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("scheme", out var s) && s.ValueKind == JsonValueKind.String
        && value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var version)
        && value.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? new CodedValue(s.GetString()!, version, c.GetString()!)
            : null;
}

/// <summary>
/// What a classified field holds: one coded value, or — in a field that takes several — a list of
/// them with at most one marked primary. A single value is its own primary.
/// </summary>
/// <param name="Values">The values, in the order they were entered; never empty.</param>
/// <param name="Marked">The position of the value marked primary, or null when none is.</param>
public sealed record CodedValues(IReadOnlyList<CodedValue> Values, int? Marked)
{
    /// <summary>
    /// The value a count places the record by: the one marked primary, or the only one. Null when
    /// there are several and none is marked — a person has to say which comes first.
    /// </summary>
    public CodedValue? Primary => Marked is { } i ? Values[i] : Values.Count == 1 ? Values[0] : null;

    /// <summary>True when the field holds several values.</summary>
    public bool Many => Values.Count > 1;

    /// <summary>
    /// Reads <c>{"scheme", "version", "code"}</c>, or a non-empty array of them where at most one has
    /// <c>"primary": true</c>; null for anything else, including JSON null and an empty array.
    /// </summary>
    public static CodedValues? From(JsonElement value)
    {
        if (CodedValue.From(value) is { } one) return new([one], null);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return null;
        var values = new List<CodedValue>();
        int? marked = null;
        foreach (var item in value.EnumerateArray())
        {
            if (CodedValue.From(item) is not { } coded) return null;
            if (item.TryGetProperty("primary", out var primary))
            {
                if (primary.ValueKind != JsonValueKind.True || marked is not null) return null;
                marked = values.Count;
            }
            values.Add(coded);
        }
        return new(values, marked);
    }

    /// <summary>The JSON a record stores for these values: an object for a single unmarked value, an array otherwise.</summary>
    public JsonNode ToJson()
    {
        static JsonObject Object(CodedValue v) => new() { ["scheme"] = v.Scheme, ["version"] = v.Version, ["code"] = v.Code };
        if (Values.Count == 1 && Marked is null) return Object(Values[0]);
        var array = new JsonArray();
        for (var i = 0; i < Values.Count; i++)
        {
            var o = Object(Values[i]);
            if (i == Marked) o["primary"] = true;
            array.Add((JsonNode)o); // the JsonNode overload: the generic one needs code generated at run time
        }
        return array;
    }
}
