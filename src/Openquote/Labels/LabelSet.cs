namespace Openquote.Labels;

/// <summary>An item of one version of a scheme, as a label names it.</summary>
public readonly record struct SchemeLabelKey(string Scheme, int Version, string Code);

/// <summary>A field of an entity type, as a label names it.</summary>
public readonly record struct FieldLabelKey(string Type, string Field);

/// <summary>One version of a report or export form, as a label names it.</summary>
public readonly record struct FormLabelKey(string Name, int Version);

/// <summary>
/// What a label file calls one version of an export form: its name, the headings of some of its columns
/// (by index, counted from 0), or both.
/// </summary>
public sealed record ExportLabels(string? Label, IReadOnlyDictionary<int, string> Columns);

/// <summary>
/// What one version of a pack calls things in one locale: items of scheme versions, entity types and their
/// fields, report and export forms, and the other names people give a field. Labels change what people
/// read, never a code or what is counted, so a renamed item needs no new scheme version.
/// </summary>
public sealed record LabelSet(
    string Pack,
    int Version,
    string Locale,
    IReadOnlyDictionary<SchemeLabelKey, string> Schemes,
    IReadOnlyDictionary<FieldLabelKey, string> Fields)
{
    /// <summary>What entity types are called, by type.</summary>
    public IReadOnlyDictionary<string, string> Types { get; init; } = new Dictionary<string, string>();

    /// <summary>What report forms are called, by name and version.</summary>
    public IReadOnlyDictionary<FormLabelKey, string> Reports { get; init; } = new Dictionary<FormLabelKey, string>();

    /// <summary>What export forms and their columns are called, by name and version.</summary>
    public IReadOnlyDictionary<FormLabelKey, ExportLabels> Exports { get; init; } = new Dictionary<FormLabelKey, ExportLabels>();

    /// <summary>
    /// Other names a field goes by in this locale — the headings a person's own table may use for it, such as
    /// a surname column for a name field. They never label the field; a host matches them when it takes data in.
    /// </summary>
    public IReadOnlyDictionary<FieldLabelKey, IReadOnlyList<string>> Aliases { get; init; } = new Dictionary<FieldLabelKey, IReadOnlyList<string>>();
}
