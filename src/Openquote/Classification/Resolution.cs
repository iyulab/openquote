namespace Openquote.Classification;

/// <summary>How a record's value lands in a target version of its scheme.</summary>
public enum ResolutionKind
{
    /// <summary>Exactly one code in the target version: placed there, no guess involved.</summary>
    Assigned,

    /// <summary>More than one candidate code: a person has to choose. Never split or estimated.</summary>
    Pending,

    /// <summary>
    /// No code in the target version: a crosswalk has no link for the value, no crosswalk path
    /// exists, or the code is not an item of its own version.
    /// </summary>
    Unmapped,
}

/// <summary>The result of carrying one value to a target version.</summary>
/// <param name="Kind">Where the value lands.</param>
/// <param name="Code">The target code when <see cref="ResolutionKind.Assigned"/>.</param>
/// <param name="Candidates">The target codes a person chooses from when <see cref="ResolutionKind.Pending"/>.</param>
/// <param name="Crosswalks">The crosswalks applied, as <c>from-to</c>, in order.</param>
public sealed record Resolution(ResolutionKind Kind, string? Code, IReadOnlyList<string> Candidates, IReadOnlyList<string> Crosswalks);
