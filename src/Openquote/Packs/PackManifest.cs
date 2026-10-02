using System.Text.RegularExpressions;

namespace Openquote.Packs;

/// <summary>
/// One version of a data pack as a vault keeps it: the pack, its version, the packs it builds on (each
/// at a minimum version) and the definition files this version added. A pack only ever adds, so a later
/// version holds everything an earlier one did, and a minimum version is all a dependency needs. The
/// manifest's presence in a vault is the record that the pack was applied.
/// </summary>
public sealed partial record PackManifest(
    string Id,
    int Version,
    string Label,
    IReadOnlyDictionary<string, int> Depends,
    IReadOnlyList<string> Provides)
{
    // Ids a vault keeps for itself: "local" for what people add in the app, "oq" for the engine.
    private static readonly string[] Reserved = ["local", "oq"];

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*\z")]
    private static partial Regex IdPattern();

    /// <summary>True if <paramref name="id"/> is lowercase letters and digits in words joined by <c>.</c> or <c>-</c>.</summary>
    internal static bool IsId(string id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>True if <paramref name="id"/> is kept for the vault itself and cannot name a pack.</summary>
    internal static bool IsReserved(string id) => Reserved.Contains(id, StringComparer.Ordinal);
}
