namespace Openquote.Packs;

/// <summary>What is wrong with the packs a vault holds.</summary>
public enum PackIssueKind
{
    /// <summary>A pack builds on a pack the vault does not hold.</summary>
    MissingDependency,

    /// <summary>A pack needs a later version of a pack than the vault holds.</summary>
    OlderDependency,

    /// <summary>A manifest lists a definition file the vault does not hold in a readable form.</summary>
    MissingFile,

    /// <summary>More than one pack claims the same definition file.</summary>
    SharedFile,

    /// <summary>A pack builds on itself through other packs.</summary>
    DependencyCycle,
}

/// <summary>One problem with a vault's packs, named by the pack (or packs) it concerns.</summary>
public sealed record PackIssue(PackIssueKind Kind, string Pack, string Detail);

/// <summary>
/// Checks the packs a vault holds against each other and against the files it holds. Reached through
/// <see cref="Vault.VaultContent.CheckPacks"/>.
/// </summary>
internal static class PackCheck
{
    /// <summary>
    /// Checks <paramref name="packs"/> against <paramref name="paths"/>, the definition files the vault
    /// holds. Issues come per pack in id order: dependencies first, then files; shared files and cycles last.
    /// </summary>
    public static IReadOnlyList<PackIssue> Check(IEnumerable<PackManifest> packs, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(paths);
        var all = packs.ToList();
        var present = new HashSet<string>(paths, StringComparer.Ordinal);
        var graph = new PackGraph(all);
        var issues = new List<PackIssue>();

        foreach (var pack in graph.Latest.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            foreach (var (dependency, min) in pack.Depends.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                if (graph.Find(dependency) is not { } held)
                    issues.Add(new(PackIssueKind.MissingDependency, pack.Id, $"needs {dependency} v{min} or later"));
                else if (held.Version < min)
                    issues.Add(new(PackIssueKind.OlderDependency, pack.Id, $"needs {dependency} v{min} or later; the vault holds v{held.Version}"));
            }
        }

        foreach (var pack in all.OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Version))
            foreach (var path in pack.Provides.Where(p => !present.Contains(p)))
                issues.Add(new(PackIssueKind.MissingFile, pack.Id, $"v{pack.Version} lists {path}, which the vault does not hold in a readable form"));

        var claims = all.SelectMany(p => p.Provides.Select(path => (Path: path, p.Id)))
            .GroupBy(c => c.Path, StringComparer.Ordinal)
            .Select(g => (Path: g.Key, Ids: g.Select(c => c.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()))
            .Where(c => c.Ids.Count > 1)
            .OrderBy(c => c.Path, StringComparer.Ordinal);
        foreach (var (path, ids) in claims)
            issues.Add(new(PackIssueKind.SharedFile, string.Join(", ", ids), $"{path} is claimed by more than one pack"));

        foreach (var pack in graph.Latest.OrderBy(p => p.Id, StringComparer.Ordinal).Where(p => graph.DependsOn(p.Id, p.Id)))
            issues.Add(new(PackIssueKind.DependencyCycle, pack.Id, "builds on itself through other packs"));

        return issues;
    }
}
