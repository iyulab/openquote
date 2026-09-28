using System.Text.Json;

namespace Openquote.Classification;

/// <summary>One item of a classification scheme. A two-level code is a path: <c>parent/child</c>.</summary>
public sealed record SchemeItem(string Code, string Label, string? Parent, bool Suggest);

/// <summary>
/// One immutable version of a classification scheme. Editing a scheme makes a new version; a
/// renamed item keeps its code, so only the label changes.
/// </summary>
public sealed record Scheme(string Name, int Version, IReadOnlyList<SchemeItem> Items)
{
    /// <summary>True if <paramref name="code"/> is an item of this version.</summary>
    public bool Contains(string code) => Items.Any(i => i.Code == code);
}

/// <summary>
/// The links between two versions of a scheme, as (old code, new code) pairs. Whether a link is
/// 1:1, N:1, 1:N or N:M is not stored: it follows from the pairs.
/// </summary>
public sealed record Crosswalk(string Scheme, int From, int To, IReadOnlyList<(string From, string To)> Links)
{
    /// <summary>The new codes <paramref name="code"/> links to, in the order the file lists them.</summary>
    public IReadOnlyList<string> Targets(string code) =>
        Links.Where(l => l.From == code).Select(l => l.To).Distinct(StringComparer.Ordinal).ToArray();
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
