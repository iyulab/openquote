namespace Openquote.Vault;

/// <summary>
/// One plaintext file of a vault. <see cref="Path"/> is relative to the vault root and uses
/// <c>/</c> separators. The engine never touches encryption: a host that stores the vault encrypted
/// decrypts each file before handing it over.
/// </summary>
public sealed record VaultFile(string Path, ReadOnlyMemory<byte> Content);

/// <summary>Sources of <see cref="VaultFile"/>s.</summary>
public static class VaultFiles
{
    /// <summary>Every file under <paramref name="root"/>, as plaintext.</summary>
    public static IEnumerable<VaultFile> FromDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var full = System.IO.Path.GetFullPath(root);
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(full, file).Replace('\\', '/');
            yield return new VaultFile(relative, File.ReadAllBytes(file));
        }
    }
}
