namespace Openquote.Labels;

/// <summary>An item of one version of a scheme, as a label names it.</summary>
public readonly record struct SchemeLabelKey(string Scheme, int Version, string Code);

/// <summary>A field of an entity type, as a label names it.</summary>
public readonly record struct FieldLabelKey(string Type, string Field);

/// <summary>
/// What one version of a pack calls things in one locale: items of scheme versions and fields of
/// entity types. Labels change what people read, never a code or what is counted, so a renamed item
/// needs no new scheme version.
/// </summary>
public sealed record LabelSet(
    string Pack,
    int Version,
    string Locale,
    IReadOnlyDictionary<SchemeLabelKey, string> Schemes,
    IReadOnlyDictionary<FieldLabelKey, string> Fields);
