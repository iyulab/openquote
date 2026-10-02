namespace Openquote.Suggestions;

/// <summary>Whether a host may offer an item of a scheme version as a suggestion, and how.</summary>
public enum Suggestion
{
    /// <summary>Never offered.</summary>
    Off,

    /// <summary>Offered among the suggestions.</summary>
    Offer,

    /// <summary>
    /// Offered, set apart from the other suggestions: a person has to confirm it, and a host never fills it
    /// in on its own.
    /// </summary>
    Confirm,
}

/// <summary>An item of one version of a scheme.</summary>
public readonly record struct SchemeItemKey(string Scheme, int Version, string Code);

/// <summary>
/// What one version of a pack says about suggesting items of scheme versions, apart from the schemes
/// themselves. Whether an item is suggested never changes a code or what is counted, so it needs no new
/// scheme version: a pack's later version says it again, and the highest version of a pack counts.
/// </summary>
public sealed record SuggestionSet(string Pack, int Version, IReadOnlyDictionary<SchemeItemKey, Suggestion> Items);
