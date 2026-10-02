using System.Globalization;
using System.Text.RegularExpressions;

namespace Openquote.Vault;

/// <summary>
/// What a vault path holds, read from the folder layout alone — so a file that cannot be read can
/// still be named by what it was for. Only the fields that apply to <see cref="Kind"/> are set.
/// </summary>
/// <param name="Kind">
/// <c>subject</c>, <c>group</c>, <c>practitioners</c>, <c>devices</c>, <c>scheme</c>, <c>crosswalk</c>,
/// <c>report</c>, <c>export</c>, <c>pack</c>, <c>labels</c>, <c>fields</c>, <c>suggestions</c>, <c>run</c> or <c>other</c>.
/// </param>
/// <param name="Id">The subject's or group's id.</param>
/// <param name="Name">The scheme, report form or export form; the pack, for a pack manifest, labels, fields or suggestions.</param>
/// <param name="Version">The scheme's, form's or pack's version.</param>
/// <param name="From">The earlier scheme version a crosswalk leads from.</param>
/// <param name="To">The later scheme version a crosswalk leads to.</param>
/// <param name="Year">The year of a run record.</param>
/// <param name="Type">The entity type a field definition file declares fields for.</param>
/// <param name="Into">The other scheme a crosswalk across schemes leads into.</param>
public sealed partial record VaultFileKind(
    string Kind,
    string? Id = null,
    string? Name = null,
    int? Version = null,
    int? From = null,
    int? To = null,
    int? Year = null,
    string? Type = null,
    string? Into = null)
{
    // Only the start of the file name is read: a sync client's copy of a file keeps it
    // ("v2 (conflicted copy).json", "v2.json.sync-conflict-…"), and that copy is the file most
    // likely to need naming.
    [GeneratedRegex(@"^v(?<from>[1-9][0-9]*)(-v(?<to>[1-9][0-9]*))?(?![0-9-])")]
    private static partial Regex VersionName();

    // A crosswalk across schemes: v<N>-<other scheme>.v<M>, read the same way.
    [GeneratedRegex(@"^v(?<from>[1-9][0-9]*)-(?<into>[^/]+?)\.v(?<to>[1-9][0-9]*)(?![0-9])")]
    private static partial Regex AcrossName();

    /// <summary>Reads <paramref name="path"/> (relative to the vault root, <c>/</c>-separated) as what it holds.</summary>
    public static VaultFileKind Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split('/');
        var version = parts.Length == 3 ? VersionName().Match(parts[2]) : Match.Empty;
        int Number(string group) => int.Parse(version.Groups[group].Value, CultureInfo.InvariantCulture);

        return parts switch
        {
            ["subjects", var id, _] => new("subject", Id: id),
            ["groups", var id, _] => new("group", Id: id),
            ["practitioners", _] => new("practitioners"),
            ["devices", _] => new("devices"),
            ["schemes", var name, var file] when !version.Success && AcrossName().Match(file) is { Success: true } across =>
                new("crosswalk", Name: name, From: int.Parse(across.Groups["from"].Value, CultureInfo.InvariantCulture),
                    To: int.Parse(across.Groups["to"].Value, CultureInfo.InvariantCulture), Into: across.Groups["into"].Value),
            ["schemes", var name, _] when version.Success && version.Groups["to"].Success =>
                new("crosswalk", Name: name, From: Number("from"), To: Number("to")),
            ["schemes", var name, _] when version.Success => new("scheme", Name: name, Version: Number("from")),
            ["reports", var name, _] when version.Success && !version.Groups["to"].Success =>
                new("report", Name: name, Version: Number("from")),
            ["exports", var name, _] when version.Success && !version.Groups["to"].Success =>
                new("export", Name: name, Version: Number("from")),
            ["packs", var name, _] when version.Success && !version.Groups["to"].Success =>
                new("pack", Name: name, Version: Number("from")),
            ["labels", var name, _] when version.Success && !version.Groups["to"].Success =>
                new("labels", Name: name, Version: Number("from")),
            ["suggestions", var name, _] when version.Success && !version.Groups["to"].Success =>
                new("suggestions", Name: name, Version: Number("from")),
            ["fields", var name, var type, var file] when VersionName().Match(file) is { Success: true } v && !v.Groups["to"].Success =>
                new("fields", Name: name, Version: int.Parse(v.Groups["from"].Value, CultureInfo.InvariantCulture), Type: type),
            ["runs", var year, _] when year.Length == 4 && year.All(char.IsAsciiDigit) =>
                new("run", Year: int.Parse(year, CultureInfo.InvariantCulture)),
            _ => new("other"),
        };
    }
}
