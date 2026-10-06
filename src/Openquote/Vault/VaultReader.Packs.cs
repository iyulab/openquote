using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Openquote.Fields;
using Openquote.Labels;
using Openquote.Packs;
using Openquote.Suggestions;

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
            if (p.ValueKind != JsonValueKind.String)
                return Bad<PackManifest>(file, UnreadableReason.Invalid, "provides lists definition files by their vault paths");
            var provided = p.GetString()!;
            if (DefinitionKindOf(provided) is not (DefinitionKind.None or DefinitionKind.Pack))
                provides.Add(provided);
            else if (!IsLaterDefinition(provided))
                return Bad<PackManifest>(file, UnreadableReason.Invalid, "provides lists definition files: schemes, crosswalks, forms, labels, fields or suggestions");
        }

        return new(new PackManifest(id, version, label, depends, provides), null);
    }

    // A definition file of a kind a later engine reads: a JSON file in a folder outside the layout this engine
    // knows. A manifest naming one is still read, without it, as a vault file of that kind would be ignored.
    private static bool IsLaterDefinition(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && !LayoutFolders.Contains(path[..slash]) && path.EndsWith(".json", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^labels/(?<pack>[^/]+)/v(?<version>[1-9][0-9]*)\.(?<locale>[^/]+)\.json\z")]
    private static partial Regex LabelsPath();

    [GeneratedRegex(@"^suggestions/(?<pack>[^/]+)/v(?<version>[1-9][0-9]*)\.json\z")]
    private static partial Regex SuggestionsPath();

    private static Definition<SuggestionSet> ParseSuggestions(VaultFile file)
    {
        if (!TryRoot(file, "openquote.suggestions/0", out var root, out var error)) return new(null, error);
        var path = SuggestionsPath().Match(file.Path);

        if (!TryString(root, "pack", out var pack) || !TryInt(root, "version", out var version) || version < 1)
            return Bad<SuggestionSet>(file, UnreadableReason.Invalid, "suggestions need a pack and a version of 1 or more");
        if (PackIdProblem(pack) is { } packProblem) return Bad<SuggestionSet>(file, UnreadableReason.Invalid, packProblem);
        if (pack != path.Groups["pack"].Value || version.ToString(CultureInfo.InvariantCulture) != path.Groups["version"].Value)
            return Bad<SuggestionSet>(file, UnreadableReason.NameMismatch, $"the path should be suggestions/{pack}/v{version}.json");

        var items = new Dictionary<SchemeItemKey, Suggestion>();
        if (root.TryGetProperty("schemes", out var schemesObject))
        {
            if (schemesObject.ValueKind != JsonValueKind.Object)
                return Bad<SuggestionSet>(file, UnreadableReason.Invalid, "schemes maps scheme names to versions to codes to off, offer or confirm");
            foreach (var scheme in schemesObject.EnumerateObject())
            {
                if (scheme.Value.ValueKind != JsonValueKind.Object)
                    return Bad<SuggestionSet>(file, UnreadableReason.Invalid, $"{scheme.Name}: map versions to codes to off, offer or confirm");
                foreach (var v in scheme.Value.EnumerateObject())
                {
                    if (!int.TryParse(v.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var schemeVersion) || schemeVersion < 1
                        || v.Name != schemeVersion.ToString(CultureInfo.InvariantCulture) || v.Value.ValueKind != JsonValueKind.Object)
                        return Bad<SuggestionSet>(file, UnreadableReason.Invalid, $"{scheme.Name}: {v.Name} is not a version");
                    foreach (var code in v.Value.EnumerateObject())
                    {
                        Suggestion? suggestion = code.Value.ValueKind != JsonValueKind.String ? null : code.Value.GetString() switch
                        {
                            "off" => Suggestion.Off,
                            "offer" => Suggestion.Offer,
                            "confirm" => Suggestion.Confirm,
                            _ => null,
                        };
                        if (suggestion is null)
                            return Bad<SuggestionSet>(file, UnreadableReason.Invalid, $"{scheme.Name} v{schemeVersion} {code.Name}: off, offer or confirm");
                        items[new(scheme.Name, schemeVersion, code.Name)] = suggestion.Value;
                    }
                }
            }
        }

        return new(new SuggestionSet(pack, version, items), null);
    }

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

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("types", out var typesObject))
        {
            if (typesObject.ValueKind != JsonValueKind.Object)
                return Bad<LabelSet>(file, UnreadableReason.Invalid, "types maps entity types to labels");
            foreach (var type in typesObject.EnumerateObject())
            {
                if (type.Value.ValueKind != JsonValueKind.String || type.Value.GetString() is not { Length: > 0 } text)
                    return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{type.Name}: a label is non-empty text");
                types[type.Name] = text;
            }
        }

        var aliases = new Dictionary<FieldLabelKey, IReadOnlyList<string>>();
        if (root.TryGetProperty("aliases", out var aliasesObject))
        {
            if (aliasesObject.ValueKind != JsonValueKind.Object)
                return Bad<LabelSet>(file, UnreadableReason.Invalid, "aliases maps entity types to fields to lists of names");
            foreach (var type in aliasesObject.EnumerateObject())
            {
                if (type.Value.ValueKind != JsonValueKind.Object)
                    return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{type.Name}: map fields to lists of names");
                foreach (var field in type.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() == 0
                        || field.Value.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.String || n.GetString() is not { Length: > 0 }))
                        return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{type.Name}.{field.Name}: aliases are a non-empty list of non-empty text");
                    aliases[new(type.Name, field.Name)] = [.. field.Value.EnumerateArray().Select(n => n.GetString()!)];
                }
            }
        }

        var reports = new Dictionary<FormLabelKey, string>();
        if (root.TryGetProperty("reports", out var reportsObject))
        {
            if (reportsObject.ValueKind != JsonValueKind.Object)
                return Bad<LabelSet>(file, UnreadableReason.Invalid, "reports maps report forms to versions to labels");
            foreach (var form in reportsObject.EnumerateObject())
            {
                if (form.Value.ValueKind != JsonValueKind.Object)
                    return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{form.Name}: map versions to labels");
                foreach (var v in form.Value.EnumerateObject())
                {
                    if (Counted(v.Name, 1) is not { } formVersion)
                        return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{form.Name}: {v.Name} is not a version");
                    if (v.Value.ValueKind != JsonValueKind.String || v.Value.GetString() is not { Length: > 0 } text)
                        return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{form.Name} v{formVersion}: a label is non-empty text");
                    reports[new(form.Name, formVersion)] = text;
                }
            }
        }

        var exports = new Dictionary<FormLabelKey, ExportLabels>();
        if (root.TryGetProperty("exports", out var exportsObject))
        {
            if (exportsObject.ValueKind != JsonValueKind.Object)
                return Bad<LabelSet>(file, UnreadableReason.Invalid, "exports maps export forms to versions to a label and column headings");
            foreach (var form in exportsObject.EnumerateObject())
            {
                if (form.Value.ValueKind != JsonValueKind.Object)
                    return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{form.Name}: map versions to a label and column headings");
                foreach (var v in form.Value.EnumerateObject())
                {
                    if (Counted(v.Name, 1) is not { } formVersion || v.Value.ValueKind != JsonValueKind.Object)
                        return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{form.Name}: {v.Name} is not a version");
                    var at = $"{form.Name} v{formVersion}";
                    string? label = null;
                    if (v.Value.TryGetProperty("label", out var labelValue))
                    {
                        if (labelValue.ValueKind != JsonValueKind.String || labelValue.GetString() is not { Length: > 0 } text)
                            return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{at}: a label is non-empty text");
                        label = text;
                    }
                    var columns = new Dictionary<int, string>();
                    if (v.Value.TryGetProperty("columns", out var columnsObject))
                    {
                        if (columnsObject.ValueKind != JsonValueKind.Object)
                            return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{at}: columns maps column numbers, counted from 0, to headings");
                        foreach (var column in columnsObject.EnumerateObject())
                        {
                            if (Counted(column.Name, 0) is not { } index)
                                return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{at}: {column.Name} is not a column number counted from 0");
                            if (column.Value.ValueKind != JsonValueKind.String || column.Value.GetString() is not { Length: > 0 } text)
                                return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{at} column {index}: a heading is non-empty text");
                            columns[index] = text;
                        }
                    }
                    if (label is null && columns.Count == 0)
                        return Bad<LabelSet>(file, UnreadableReason.Invalid, $"{at}: give a label, column headings or both");
                    exports[new(form.Name, formVersion)] = new(label, columns);
                }
            }
        }

        return new(new LabelSet(pack, version, locale, schemes, fields) { Types = types, Aliases = aliases, Reports = reports, Exports = exports }, null);
    }

    // A whole number written plainly (no sign, no leading zero) that is at least min, or null.
    private static int? Counted(string text, int min) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min
            && text == n.ToString(CultureInfo.InvariantCulture) ? n : null;

    [GeneratedRegex(@"^fields/(?<pack>[^/]+)/(?<type>[^/]+)/v(?<version>[1-9][0-9]*)\.json\z")]
    private static partial Regex FieldsPath();

    private static Definition<FieldSet> ParseFields(VaultFile file)
    {
        // Format 1 lets a coded field take several values.
        if (!TryRoot(file, FieldsFormats, out var root, out var error)) return new(null, error);
        var v1 = TryString(root, "format", out var format) && format == "openquote.fields/1";
        var path = FieldsPath().Match(file.Path);

        if (!TryString(root, "pack", out var pack) || !TryString(root, "type", out var type)
            || !TryInt(root, "version", out var version) || version < 1)
            return Bad<FieldSet>(file, UnreadableReason.Invalid, "field definitions need a pack, an entity type and a version of 1 or more");
        if (PackIdProblem(pack) is { } packProblem) return Bad<FieldSet>(file, UnreadableReason.Invalid, packProblem);
        if (pack != path.Groups["pack"].Value || type != path.Groups["type"].Value
            || version.ToString(CultureInfo.InvariantCulture) != path.Groups["version"].Value)
            return Bad<FieldSet>(file, UnreadableReason.NameMismatch, $"the path should be fields/{pack}/{type}/v{version}.json");

        string? typeLabel = null;
        if (root.TryGetProperty("label", out var labelValue))
        {
            if (labelValue.ValueKind != JsonValueKind.String || labelValue.GetString() is not { Length: > 0 } text)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "the label of an entity type is non-empty text");
            typeLabel = text;
        }

        List<string>? under = null;
        if (root.TryGetProperty("under", out var underValue))
        {
            if (FieldCatalog.IsKeptOnItsOwn(type))
                return Bad<FieldSet>(file, UnreadableReason.Invalid, $"a {type} is not kept under anything");
            if (underValue.ValueKind != JsonValueKind.Array || underValue.GetArrayLength() == 0
                || underValue.EnumerateArray().Any(h => h.ValueKind != JsonValueKind.String || !FieldCatalog.Holders.Contains(h.GetString()!)))
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "under lists where an entity of the type is kept: subject, group or both");
            under = [.. underValue.EnumerateArray().Select(h => h.GetString()!)];
            if (under.Distinct(StringComparer.Ordinal).Count() != under.Count)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "under names each folder once");
        }

        int? order = null;
        if (root.TryGetProperty("order", out var orderValue))
        {
            if (orderValue.ValueKind != JsonValueKind.Number || !orderValue.TryGetInt32(out var place) || place < 0)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "the order of an entity type is a whole number of 0 or more");
            order = place;
        }

        string? dated = null;
        if (root.TryGetProperty("dated", out var datedValue))
        {
            if (datedValue.ValueKind != JsonValueKind.String || datedValue.GetString() is not { Length: > 0 } name)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "dated names the date field that says when a record of the type happened");
            dated = name;
        }

        CaseRole? role = null;
        if (root.TryGetProperty("role", out var roleValue))
        {
            role = roleValue.ValueKind == JsonValueKind.String ? roleValue.GetString() switch
            {
                "opens" => CaseRole.Opens,
                "closes" => CaseRole.Closes,
                _ => null,
            } : null;
            if (role is null)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "the role of an entity type is opens or closes");
        }

        int? followUpDays = null;
        if (root.TryGetProperty("followUpDays", out var followUpValue))
        {
            if (followUpValue.ValueKind != JsonValueKind.Number || !followUpValue.TryGetInt32(out var days) || days < 1)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "followUpDays is a whole number of days, 1 or more");
            followUpDays = days;
        }

        var fields = new List<FieldDefinition>();
        if (root.TryGetProperty("fields", out var fieldsArray))
        {
            if (fieldsArray.ValueKind != JsonValueKind.Array)
                return Bad<FieldSet>(file, UnreadableReason.Invalid, "fields must be an array");
            foreach (var f in fieldsArray.EnumerateArray())
            {
                if (ParseField(f, pack, v1) is not { } field)
                    return Bad<FieldSet>(file, UnreadableReason.Invalid,
                        "every field needs a name and a kind; coded fields name a scheme, references name a type, and nothing else does; only a coded field in format 1 may take many values; a default is a field of the subject or, in format 1, a fixed text, number or code");
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

        return new(new FieldSet(pack, type, version, fields, constraints) { Label = typeLabel, Under = under, Order = order, Dated = dated, Role = role, FollowUpDays = followUpDays }, null);
    }

    private static readonly string[] FieldsFormats = ["openquote.fields/0", "openquote.fields/1"];

    private static FieldDefinition? ParseField(JsonElement f, string pack, bool v1)
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
        if (!TryFlag(f, "many", out var many) || (many && (!v1 || k != FieldKind.Coded))) return null;

        string? fromSubject = null;
        string? fixedValue = null;
        YearRule? yearRule = null;
        if (f.TryGetProperty("default", out var d))
        {
            // One source: a field of the record's subject, or (format 1) a fixed value of the field's kind.
            if (d.ValueKind != JsonValueKind.Object) return null;
            if (d.TryGetProperty("value", out var value))
            {
                if (!v1 || d.TryGetProperty("subject", out _) || FixedValue(value, k) is not { } text) return null;
                fixedValue = text;
            }
            else if (TryString(d, "subject", out var subjectField)) fromSubject = subjectField;
            else return null;
            // Format 1: how a value taken from the subject ages, only beside a subject source.
            if (d.TryGetProperty("year", out var year))
            {
                if (!v1 || fromSubject is null || ParseYearRule(year) is not { } rule) return null;
                yearRule = rule;
            }
        }

        string? label = null;
        if (f.TryGetProperty("label", out _))
        {
            if (!TryString(f, "label", out var l)) return null;
            label = l;
        }

        return new FieldDefinition(name, k, scheme, refType, required, Hidden: false, tier, fromSubject, label, pack)
        {
            Many = many,
            DefaultValue = fixedValue,
            DefaultYear = yearRule,
        };
    }

    // `{ "startMonth": 1-12 (default 1), "then": "advance" | "drop", "max": n | { "subject": field, "by": { code: n } } }`;
    // a cap only for an advancing value, every number a whole number from 1.
    private static YearRule? ParseYearRule(JsonElement year)
    {
        if (year.ValueKind != JsonValueKind.Object) return null;
        var startMonth = 1;
        if (year.TryGetProperty("startMonth", out _) && (!TryInt(year, "startMonth", out startMonth) || startMonth is < 1 or > 12)) return null;
        if (!TryString(year, "then", out var thenText)) return null;
        YearStep? then = thenText switch { "advance" => YearStep.Advance, "drop" => YearStep.Drop, _ => null };
        if (then is not { } step) return null;
        YearCap? max = null;
        if (year.TryGetProperty("max", out var m))
        {
            if (step != YearStep.Advance) return null;
            if (m.ValueKind == JsonValueKind.Number)
            {
                if (!m.TryGetInt32(out var n) || n < 1) return null;
                max = new YearCap(n, null, new Dictionary<string, int>());
            }
            else if (m.ValueKind == JsonValueKind.Object && TryString(m, "subject", out var levelField)
                && m.TryGetProperty("by", out var by) && by.ValueKind == JsonValueKind.Object)
            {
                var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var entry in by.EnumerateObject())
                {
                    if (entry.Name.Length == 0 || entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetInt32(out var n) || n < 1) return null;
                    byCode[entry.Name] = n;
                }
                if (byCode.Count == 0) return null;
                max = new YearCap(null, levelField, byCode);
            }
            else return null;
        }
        return new YearRule(startMonth, step, max);
    }

    // A fixed first value as text: a non-empty string for a text field or a code for a coded one, a number
    // as the file writes it for a number field. A date or a reference has none: no fixed one fits every record.
    private static string? FixedValue(JsonElement value, FieldKind kind) => kind switch
    {
        FieldKind.Text or FieldKind.Coded when value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s => s,
        FieldKind.Number when value.ValueKind == JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

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
