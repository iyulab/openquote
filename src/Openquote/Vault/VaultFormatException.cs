namespace Openquote.Vault;

/// <summary>
/// The vault's declaration (<c>vault.json</c>) names a format this version of the engine does not
/// read. Nothing of the vault is read: a newer format may lay records out in a way this engine
/// would count wrongly without noticing, so refusing the whole vault is the only safe answer.
/// </summary>
public sealed class VaultFormatException : Exception
{
    internal VaultFormatException(string? declared, bool newer)
        : base(newer
            ? $"the vault is declared as {declared}, newer than the {VaultReader.VaultFormat} this engine reads"
            : $"the vault declaration does not name a vault format this engine knows ({declared ?? "none"})")
    {
        Declared = declared;
        IsNewer = newer;
    }

    /// <summary>The format the declaration names, or null when it names none.</summary>
    public string? Declared { get; }

    /// <summary>Whether the declaration names a later version of the vault format.</summary>
    public bool IsNewer { get; }
}
