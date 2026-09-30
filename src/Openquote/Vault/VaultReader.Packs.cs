using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Openquote.Packs;

namespace Openquote.Vault;

public static partial class VaultReader
{
    private const string PackIdPattern = @"[a-z0-9]+(?:[.-][a-z0-9]+)*";

    [GeneratedRegex(@"^packs/(?<id>" + PackIdPattern + @")/v(?<version>[1-9][0-9]*)\.json$")]
    private static partial Regex PackPath();

    private static Definition<PackManifest> ParsePack(VaultFile file)
    {
        if (!TryRoot(file, "openquote.pack/0", out var root, out var error)) return new(null, error);
        var path = PackPath().Match(file.Path);

        if (!TryString(root, "pack", out var id) || !PackManifest.IsId(id) || !TryInt(root, "version", out var version) || version < 1
            || !TryString(root, "label", out var label))
            return Bad<PackManifest>(file, UnreadableReason.Invalid, "a pack needs an id, a version of 1 or more and a label");
        if (PackManifest.IsReserved(id))
            return Bad<PackManifest>(file, UnreadableReason.Invalid, $"{id} is kept for the vault itself");
        if (id != path.Groups["id"].Value || version.ToString(CultureInfo.InvariantCulture) != path.Groups["version"].Value)
            return Bad<PackManifest>(file, UnreadableReason.NameMismatch, $"the path should be packs/{id}/v{version}.json");

        var depends = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("depends", out var dependsObject))
        {
            if (dependsObject.ValueKind != JsonValueKind.Object)
                return Bad<PackManifest>(file, UnreadableReason.Invalid, "depends must map pack ids to minimum versions");
            foreach (var d in dependsObject.EnumerateObject())
            {
                if (!PackManifest.IsId(d.Name) || d.Name == id
                    || d.Value.ValueKind != JsonValueKind.Number || !d.Value.TryGetInt32(out var min) || min < 1)
                    return Bad<PackManifest>(file, UnreadableReason.Invalid, $"depends: {d.Name} must be another pack with a minimum version of 1 or more");
                depends[d.Name] = min;
            }
        }

        if (!root.TryGetProperty("provides", out var providesArray) || providesArray.ValueKind != JsonValueKind.Array)
            return Bad<PackManifest>(file, UnreadableReason.Invalid, "provides must list the definition files this version adds");
        var provides = new List<string>();
        foreach (var p in providesArray.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.String || DefinitionKindOf(p.GetString()!) is DefinitionKind.None or DefinitionKind.Pack)
                return Bad<PackManifest>(file, UnreadableReason.Invalid, "provides lists definition files: schemes, crosswalks, forms, labels or fields");
            provides.Add(p.GetString()!);
        }

        return new(new PackManifest(id, version, label, depends, provides), null);
    }
}
