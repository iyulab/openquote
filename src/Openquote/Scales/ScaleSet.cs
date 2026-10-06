namespace Openquote.Scales;

/// <summary>Which way a scale's score moves when things go better: a fact of the scale, never a judgement of a person.</summary>
public enum ScaleDirection
{
    /// <summary>A lower score is better (a symptom scale, say).</summary>
    LowerIsBetter,

    /// <summary>A higher score is better (a wellbeing scale, say).</summary>
    HigherIsBetter,
}

/// <summary>Where a scale's terms of use come from: what they allow, where they are published, and the day they were read.</summary>
/// <param name="Terms">What the terms allow, in a sentence.</param>
/// <param name="Source">Where they are published, or null.</param>
/// <param name="Retrieved">The day they were read, or null.</param>
public sealed record ScaleLicence(string Terms, string? Source, DateOnly? Retrieved);

/// <summary>
/// One scale: a scale whose total score a response records. It says the range a score can take and the way a better
/// score moves; nothing in it divides people by their score.
/// </summary>
/// <param name="Code">The code the response's scale field holds.</param>
/// <param name="Direction">Which way the score moves when things go better.</param>
/// <param name="Min">The lowest score the scale gives.</param>
/// <param name="Max">The highest score the scale gives.</param>
/// <param name="Licence">Where its terms of use come from, or null when the pack does not say.</param>
public sealed record Scale(string Code, ScaleDirection Direction, decimal Min, decimal Max, ScaleLicence? Licence);

/// <summary>
/// What one version of a pack says about its scales: which entity type records a response, which of its fields names
/// the scale and which holds the score, and each scale by the code its scale field holds.
/// </summary>
/// <param name="Pack">The pack.</param>
/// <param name="Version">The version of the pack's scales file.</param>
/// <param name="Type">The entity type of a response.</param>
/// <param name="ScaleField">The response's coded field whose code names the scale.</param>
/// <param name="ScoreField">The response's number field holding the total score.</param>
/// <param name="Scales">The scales, by code.</param>
public sealed record ScaleSet(
    string Pack,
    int Version,
    string Type,
    string ScaleField,
    string ScoreField,
    IReadOnlyDictionary<string, Scale> Scales);
