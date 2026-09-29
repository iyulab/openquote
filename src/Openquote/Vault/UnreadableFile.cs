namespace Openquote.Vault;

/// <summary>Why a file that looks like part of the vault could not be used.</summary>
public enum UnreadableReason
{
    /// <summary>The content is not valid JSON — for example a file cut off while it was being written.</summary>
    Malformed,

    /// <summary>The <c>format</c> is missing or is one this version of the engine does not know.</summary>
    UnknownFormat,

    /// <summary>A required key is missing or has the wrong type.</summary>
    Invalid,

    /// <summary>
    /// The file name does not match the id, device or version inside it — or it is named like a vault
    /// file with something added, as a sync client names the losing side of a conflict.
    /// </summary>
    NameMismatch,

    /// <summary>Another file carries the same id with different content.</summary>
    DuplicateId,
}

/// <summary>
/// A file the reader skipped. The rest of the vault is still read; these are shown to a person
/// rather than silently dropped.
/// </summary>
public sealed record UnreadableFile(string Path, UnreadableReason Reason, string Detail);
