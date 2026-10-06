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
/// A host offers a first value when the record is written from one of two sources: <see cref="DefaultFromSubject"/>
/// names a field of the record's subject, and <see cref="DefaultValue"/> is a fixed value. Either is kept as entered,
/// so the record shows it as it was then.
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

    /// <summary>
    /// The fixed value a host offers for the field when a record is written (field definitions format 1): the
    /// text of a text field, the number of a number field as the file writes it, or a code of a coded field's
    /// scheme — offered only when the version in force that day holds it. Null when the field gives none.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// How the value taken from the subject (<see cref="DefaultFromSubject"/>) ages (field definitions format 1):
    /// a value written in one year no longer describes a later one. Null when the value holds whatever the year.
    /// See <see cref="SubjectDefaults.Carry"/>.
    /// </summary>
    public YearRule? DefaultYear { get; init; }
}

/// <summary>What a value taken from the subject becomes once the year it was written in is over.</summary>
public enum YearStep
{
    /// <summary>A whole number counts up by one for each year passed — a school grade, say.</summary>
    Advance,

    /// <summary>The value no longer holds, and nothing is offered in its place — a class, say.</summary>
    Drop,
}

/// <summary>
/// The years a value taken from the subject is good for. A year starts on the first day of
/// <paramref name="StartMonth"/> (1–12; 3 for a school year from March). <paramref name="Max"/> bounds an
/// advancing value: a value that would pass it is not offered.
/// </summary>
public sealed record YearRule(int StartMonth, YearStep Then, YearCap? Max);

/// <summary>
/// The highest value an advancing value may reach: a fixed number, or a number per code of another field of the
/// subject (<paramref name="SubjectField"/>, read through <paramref name="ByCode"/>) — a school level, say, whose
/// last grade differs. Exactly one of <paramref name="Fixed"/> and <paramref name="SubjectField"/> is set.
/// </summary>
public sealed record YearCap(int? Fixed, string? SubjectField, IReadOnlyDictionary<string, int> ByCode);

/// <summary>A narrowing of a field another pack declared: it may make the field required or hide it, nothing else.</summary>
public sealed record FieldConstraint(string Name, bool Required, bool Hidden);

/// <summary>The fields one version of a pack declares for one entity type, and the fields of other packs it narrows.</summary>
public sealed record FieldSet(
    string Pack,
    string Type,
    int Version,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<FieldConstraint> Constraints)
{
    /// <summary>What people read for the entity type itself, or null when the file gives none.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The folders an entity of the type is kept in — <c>subject</c>, <c>group</c> or both — or null when the
    /// file does not say. Subjects, groups, practitioners and devices are not kept under anything and never say.
    /// </summary>
    public IReadOnlyList<string>? Under { get; init; }

    /// <summary>
    /// Where a host places the entity type among the others — a smaller number first — or null when the file does
    /// not say. It orders what a host shows; nothing counted depends on it.
    /// </summary>
    public int? Order { get; init; }

    /// <summary>
    /// The date field that says when a record of the type happened, or null when the file does not say (the field
    /// called <c>date</c> then does).
    /// </summary>
    public string? Dated { get; init; }

    /// <summary>
    /// Whether a record of the type opens or closes a subject's case — or null when the file does not say, and the
    /// record falls in whatever case its date places it. See <see cref="Records.CaseReader"/>.
    /// </summary>
    public CaseRole? Role { get; init; }
}

/// <summary>What a record of an entity type does to a subject's case (<see cref="Records.CaseReader"/>).</summary>
public enum CaseRole
{
    /// <summary>A record of the type opens a case — an intake, say.</summary>
    Opens,

    /// <summary>A record of the type closes the open case — a closing, say.</summary>
    Closes,
}
