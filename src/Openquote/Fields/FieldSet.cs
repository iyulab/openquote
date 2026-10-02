namespace Openquote.Fields;

/// <summary>What a field holds.</summary>
public enum FieldKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>A calendar date, <c>YYYY-MM-DD</c>.</summary>
    Date,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A classified value of the scheme the field is bound to.</summary>
    Coded,

    /// <summary>The id of one entity of the referenced type.</summary>
    Reference,

    /// <summary>The ids of entities of the referenced type.</summary>
    References,
}

/// <summary>Whether a field's value may leave the record in a count or an export.</summary>
public enum FieldTier
{
    /// <summary>A value statistics and exports may use.</summary>
    Structured,

    /// <summary>Written content: exports leave it out by construction.</summary>
    Narrative,
}

/// <summary>
/// One field of an entity type as a pack declares it. <see cref="Scheme"/> is set for coded fields and
/// binds the field to a scheme by name; the version is the one in force when a value is entered.
/// <see cref="DefaultFromSubject"/> names a field of the record's subject whose value a host offers when
/// the record is written — kept as entered, so the record shows it as it was then.
/// </summary>
public sealed record FieldDefinition(
    string Name,
    FieldKind Kind,
    string? Scheme,
    string? RefType,
    bool Required,
    bool Hidden,
    FieldTier Tier,
    string? DefaultFromSubject,
    string? Label,
    string Pack)
{
    /// <summary>
    /// For a coded field, true when it takes several values, one of them marked primary (see
    /// <see cref="Classification.CodedValues"/>). A count places a record by the primary value.
    /// </summary>
    public bool Many { get; init; }
}

/// <summary>A narrowing of a field another pack declared: it may make the field required or hide it, nothing else.</summary>
public sealed record FieldConstraint(string Name, bool Required, bool Hidden);

/// <summary>The fields one version of a pack declares for one entity type, and the fields of other packs it narrows.</summary>
public sealed record FieldSet(
    string Pack,
    string Type,
    int Version,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<FieldConstraint> Constraints);
