using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Openquote.Fields;
using Openquote.Labels;
using Openquote.Packs;

namespace Openquote.Vault;

public static partial class VaultReader
{
    // The path patterns take any folder name, like every other definition's: what a name may be is checked by the parsers,
    // so a file under a name that cannot be valid is reported rather than silently ignored.
    [GeneratedRegex(@"^packs/(?<id>[^/]+)/v(?<version>[1-9][0-9]*)\.json\z")]
    private static partial Regex PackPath();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*\z")]
    private static partial Regex LocaleShape();

    // Why a pack id in a label or field file cannot be used, or null.
    private static string? PackIdProblem(string id) =>
        !PackManifest.IsId(id) ? $"{id} is not a pack id"
        : PackManifest.IsReserved(id) ? $"{id} is kept for the vault itself"
        : null;

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

    [GeneratedRegex(@"^labels/(?<pack>[^/]+)/v(?<version>[1-9][0-9]*)\.(?<locale>[^/]+)\.json\z")]
    private static partial Regex LabelsPath();

    private static Definition<LabelSet> ParseLabels(VaultFile file)
    {
        if (!TryRoot(file, "openquote.labels/0", out var root, out var error)) return new(null, error);
        var path = LabelsPath().Match(file.Path);

        if (!TryString(root, "pack", out var pack) || !TryInt(root, "version", out var version) || version < 1
            || !TryString(root, "locale", out var locale))
            return Bad<LabelSet>(file, UnreadableReason.Invalid, "labels need a pack, a version of 1 or more and a locale");
        if (PackIdProblem(pack) is { } packProblem) return Bad<LabelSet>(file, UnreadableReason.Invalid, packProblem);
        if (!LocaleShape().IsMatch(locale))
            return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{locale} is not a language tag such as fr or en-US");
        if (pack != path.Groups["pack"].Value || version.ToString(CultureInfo.InvariantCulture) != path.Groups["version"].Value
            || locale != path.Groups["locale"].Value)
            return Bad<LabelSet>(file, UnreadableReason.NameMismatch, $"the path should be labels/{pack}/v{version}.{locale}.json");

        var schemes = new Dictionary<SchemeLabelKey, string>();
        if (root.TryGetProperty("schemes", out var schemesObject))
        {
            if (schemesObject.ValueKind != JsonValueKind.Object)
                return Bad<LabelSet>(file, UnreadableReason.Invalid, "schemes maps scheme names to versions to codes to labels");
            foreach (var scheme in schemesObject.EnumerateObject())
            {
                if (scheme.Value.ValueKind != JsonValueKind.Object)
                    return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{scheme.Name}: map versions to codes to labels");
                foreach (var v in scheme.Value.EnumerateObject())
                {
                    if (!int.TryParse(v.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var schemeVersion) || schemeVersion < 1
                        || v.Name != schemeVersion.ToString(CultureInfo.InvariantCulture) || v.Value.ValueKind != JsonValueKind.Object)
                        return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{scheme.Name}: {v.Name} is not a version");
                    foreach (var code in v.Value.EnumerateObject())
                    {
                        if (code.Value.ValueKind != JsonValueKind.String || code.Value.GetString() is not { Length: > 0 } text)
                            return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{scheme.Name} v{schemeVersion} {code.Name}: a label is non-empty text");
                        schemes[new(scheme.Name, schemeVersion, code.Name)] = text;
                    }
                }
            }
        }

        var fields = new Dictionary<FieldLabelKey, string>();
        if (root.TryGetProperty("fields", out var fieldsObject))
        {
            if (fieldsObject.ValueKind != JsonValueKind.Object)
                return Bad<LabelSet>(file, UnreadableReason.Invalid, "fields maps entity types to fields to labels");
            foreach (var type in fieldsObject.EnumerateObject())
            {
                if (type.Value.ValueKind != JsonValueKind.Object)
                    return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{type.Name}: map fields to labels");
                foreach (var field in type.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() is not { Length: > 0 } text)
                        return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{type.Name}.{field.Name}: a label is non-empty text");
                    fields[new(type.Name, field.Name)] = text;
                }
            }
        }

        return new(new LabelSet(pack, version, locale, schemes, fields), null);
    }

    [GeneratedRegex(@"^fields/(?<pack>[^/]+)/(?<type>[^/]+)/v(?<version>[1-9][0-9]*)\.json\z")]
    private static partial Regex FieldsPath();

    private static Definition<FieldSet> ParseFields(VaultFile file)
    {
        if (!TryRoot(file, "openquote.fields/0", out var root, out var error)) return new(null, error);
        var path = FieldsPath().Match(file.Path);

        if (!TryString(root, "pack", out var pack) || !TryString(root, "type", out var type)
            || !TryInt(root, "version", out var version) || version < 1)
            return Bad<FieldSet>(file, UnreadableReason.Invalid, "field definitions need a pack, an entity type and a version of 1 or more");
        if (PackIdProblem(pack) is { } packProblem) return Bad<FieldSet>(file, UnreadableReason.Invalid, packProblem);
        if (pack != path.Groups["pack"].Value || type != path.Groups["type"].Value
            || version.ToString(CultureInfo.InvariantCulture) != path.Groups["version"].Value)
            return Bad<FieldSet>(file, UnreadableReason.NameMismatch, $"the path should be fields/{pack}/{type}/v{version}.json");

        var fields = new List<FieldDefinition>();
        if (root.TryGetProperty("fields", out var fieldsArray))
        {
            if (fieldsArray.ValueKind != JsonValueKind.Array)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "fields must be an array");
            foreach (var f in fieldsArray.EnumerateArray())
            {
                if (ParseField(f, pack) is not { } field)
                    return Bad<FieldSet>(file, UnreadableReason.Invalid,
                        "every field needs a name and a kind; coded fields name a scheme, references name a type, and nothing else does");
                if (fields.Any(x => x.Name == field.Name))
                    return Bad<FieldSet>(file, UnreadableReason.Invalid, $"field {field.Name} appears twice");
                fields.Add(field);
            }
        }

        var constraints = new List<FieldConstraint>();
        if (root.TryGetProperty("constrain", out var constrainArray))
        {
            if (constrainArray.ValueKind != JsonValueKind.Array)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "constrain must be an array");
            foreach (var c in constrainArray.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !TryString(c, "name", out var name)
                    || !TryNarrowing(c, "required", out var required) || !TryNarrowing(c, "hidden", out var hidden)
                    || !(required || hidden))
                    return Bad<FieldSet>(file, UnreadableReason.Invalid, "a constraint names a field and makes it required or hidden");
                if (fields.Any(f => f.Name == name))
                    return Bad<FieldSet>(file, UnreadableReason.Invalid, $"{name} is this pack's own field: declare it as it should be");
                constraints.Add(new FieldConstraint(name, required, hidden));
            }
        }

        return new(new FieldSet(pack, type, version, fields, constraints), null);
    }

    private static FieldDefinition? ParseField(JsonElement f, string pack)
    {
        if (f.ValueKind != JsonValueKind.Object || !TryString(f, "name", out var name) || !TryString(f, "kind", out var kindText))
            return null;
        FieldKind? kind = kindText switch
        {
            "text" => FieldKind.Text,
            "date" => FieldKind.Date,
            "number" => FieldKind.Number,
            "coded" => FieldKind.Coded,
            "reference" => FieldKind.Reference,
            "references" => FieldKind.References,
            _ => null,
        };
        if (kind is not { } k) return null;

        var scheme = TryString(f, "scheme", out var s) ? s : null;
        var refType = TryString(f, "type", out var t) ? t : null;
        if ((k == FieldKind.Coded) != (scheme is not null)) return null;
        if ((k is FieldKind.Reference or FieldKind.References) != (refType is not null)) return null;

        var tier = FieldTier.Structured;
        if (f.TryGetProperty("tier", out _))
        {
            if (!TryString(f, "tier", out var tierText) || tierText is not ("structured" or "narrative")) return null;
            tier = tierText == "narrative" ? FieldTier.Narrative : FieldTier.Structured;
        }

        if (!TryFlag(f, "required", out var required)) return null;

        string? fromSubject = null;
        if (f.TryGetProperty("default", out var d))
        {
            if (d.ValueKind != JsonValueKind.Object || !TryString(d, "subject", out var subjectField)) return null;
            fromSubject = subjectField;
        }

        string? label = null;
        if (f.TryGetProperty("label", out _))
        {
            if (!TryString(f, "label", out var l)) return null;
            label = l;
        }

        return new FieldDefinition(name, k, scheme, refType, required, Hidden: false, tier, fromSubject, label, pack);
    }

    // An optional true/false key; absent means false.
    private static bool TryFlag(JsonElement obj, string name, out bool value)
    {
        value = false;
        if (!obj.TryGetProperty(name, out var e)) return true;
        if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = e.GetBoolean();
        return true;
    }

    // A constraint key may only narrow: absent, or true.
    private static bool TryNarrowing(JsonElement obj, string name, out bool value)
    {
        value = false;
        if (!obj.TryGetProperty(name, out var e)) return true;
        if (e.ValueKind != JsonValueKind.True) return false;
        value = true;
        return true;
    }
}
