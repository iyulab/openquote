using System.Globalization;
using System.Text.Json;
using Openquote.Classification;
using Openquote.Exports;
using Openquote.Fields;
using Openquote.Labels;
using Openquote.Packs;
using Openquote.Reports;

namespace Openquote.Vault;

/// <summary>What <see cref="VaultReader.Read"/> found in a vault.</summary>
public sealed record VaultContent(
    IReadOnlyList<Change> Changes,
    IReadOnlyList<Scheme> Schemes,
    IReadOnlyList<Crosswalk> Crosswalks,
    IReadOnlyList<ReportDefinition> Reports,
    IReadOnlyList<UnreadableFile> Unreadable,
    IReadOnlyList<KeptRun> Runs,
    IReadOnlyList<ExportDefinition> Exports,
    IReadOnlyList<PackManifest> Packs,
    IReadOnlyList<LabelSet> Labels,
    IReadOnlyList<FieldSet> Fields)
{
    /// <summary>The schemes and crosswalks, ready to carry values between versions.</summary>
    public SchemeCatalog Catalog() => new(Schemes, Crosswalks);

    /// <summary>Checks the vault's packs against each other and against the definitions the vault could read.</summary>
    public IReadOnlyList<PackIssue> CheckPacks() => PackCheck.Check(Packs, DefinitionPaths());

    /// <summary>The labels of the vault's packs, resolved by what each pack builds on.</summary>
    public LabelCatalog LabelCatalog() => new(Labels, Packs);

    /// <summary>The fields of each entity type, merged from the vault's packs.</summary>
    public FieldCatalog FieldCatalog() => new(Fields, Packs);

    private IEnumerable<string> DefinitionPaths() =>
        Schemes.Select(s => $"schemes/{s.Name}/v{s.Version}.json")
            .Concat(Crosswalks.Select(c => $"schemes/{c.Scheme}/v{c.From}-v{c.To}.json"))
            .Concat(Reports.Select(r => $"reports/{r.Name}/v{r.Version}.json"))
            .Concat(Exports.Select(e => $"exports/{e.Name}/v{e.Version}.json"))
            .Concat(Labels.Select(l => $"labels/{l.Pack}/v{l.Version}.{l.Locale}.json"))
            .Concat(Fields.Select(f => $"fields/{f.Pack}/{f.Type}/v{f.Version}.json"));
}

/// <summary>
/// Reads a vault: change files, scheme versions, crosswalks, report forms, run records, export forms, pack manifests, labels
/// and field definitions. A file that cannot
/// be used never stops the read: it is reported in <see cref="VaultContent.Unreadable"/> and
/// everything else is still returned.
/// </summary>
public static partial class VaultReader
{
    internal const string ChangeFormat = "openquote.change/0";

    /// <summary>The vault format this engine reads, as a vault's declaration names it.</summary>
    public const string VaultFormat = "openquote.vault/0";

    private const string DeclarationPath = "vault.json";
    private const string VaultFormatPrefix = "openquote.vault/";
    private const int VaultFormatVersion = 0;

    /// <summary>
    /// Reads every vault file among <paramref name="files"/>. Files outside the vault layout are ignored.
    /// When the vault's declaration (<c>vault.json</c>) is among them it is checked first; a host that
    /// checks the declaration itself may leave it out.
    /// </summary>
    /// <exception cref="VaultFormatException">The declaration names a newer or an unknown format.</exception>
    public static VaultContent Read(IEnumerable<VaultFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var all = files as IReadOnlyCollection<VaultFile> ?? [.. files];
        if (all.FirstOrDefault(f => f.Path == DeclarationPath) is { } declaration) CheckDeclaration(declaration);

        var unreadable = new List<UnreadableFile>();
        var byId = new Dictionary<string, List<(Change Change, JsonElement Json)>>(StringComparer.Ordinal);
        var schemes = new List<Scheme>();
        var crosswalks = new List<Crosswalk>();
        var reports = new List<ReportDefinition>();
        var exports = new List<ExportDefinition>();
        var packs = new List<PackManifest>();
        var labels = new List<LabelSet>();
        var fieldSets = new List<FieldSet>();
        var runFiles = new List<VaultFile>();

        foreach (var file in all.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            switch (DefinitionKindOf(file.Path))
            {
                case DefinitionKind.Scheme:
                    Collect(ParseScheme(file), schemes, unreadable);
                    continue;
                case DefinitionKind.Crosswalk:
                    Collect(ParseCrosswalk(file), crosswalks, unreadable);
                    continue;
                case DefinitionKind.Report:
                    Collect(ParseReport(file), reports, unreadable);
                    continue;
                case DefinitionKind.Export:
                    Collect(ParseExport(file), exports, unreadable);
                    continue;
                case DefinitionKind.Pack:
                    Collect(ParsePack(file), packs, unreadable);
                    continue;
                case DefinitionKind.Labels:
                    Collect(ParseLabels(file), labels, unreadable);
                    continue;
                case DefinitionKind.Fields:
                    Collect(ParseFields(file), fieldSets, unreadable);
                    continue;
            }

            if (IsRunPath(file.Path))
            {
                runFiles.Add(file); // read once every report form is known
                continue;
            }

            if (!IsChangePath(file.Path))
            {
                if (IsCopyOfVaultFile(file.Path))
                    unreadable.Add(new UnreadableFile(file.Path, UnreadableReason.NameMismatch,
                        "named like a vault file with something added, as a sync client names a conflicting copy"));
                continue;
            }

            var parsed = ParseChange(file);
            if (parsed.Error is { } error)
            {
                unreadable.Add(error);
                continue;
            }

            var (change, json) = parsed.Value!.Value;
            if (!byId.TryGetValue(change.Id, out var copies)) byId[change.Id] = copies = [];
            copies.Add((change, json));
        }

        var changes = new List<Change>();
        foreach (var (id, copies) in byId)
        {
            // A sync client's conflicted copy of an unchanged file is the same change twice.
            if (copies.Skip(1).All(c => JsonElement.DeepEquals(c.Json, copies[0].Json)))
            {
                changes.Add(copies[0].Change);
                continue;
            }

            unreadable.AddRange(copies.Select(c => new UnreadableFile(
                c.Change.Path, UnreadableReason.DuplicateId, $"{copies.Count} files carry id {id} with different content")));
        }

        var runs = new List<KeptRun>();
        foreach (var file in runFiles) Collect(ParseRun(file, reports), runs, unreadable);
        runs.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        changes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        unreadable.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new VaultContent(changes, schemes, crosswalks, reports, unreadable, runs, exports, packs, labels, fieldSets);
    }

    // A per-file format this engine does not know makes that one file unreadable (§7 of the format);
    // a declaration it does not know refuses the whole vault, since every count could be wrong.
    private static void CheckDeclaration(VaultFile file)
    {
        string? declared = null;
        try
        {
            using var doc = JsonDocument.Parse(file.Content);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
                declared = format.GetString();
        }
        catch (JsonException)
        {
        }

        if (declared == VaultFormat) return;
        var newer = declared is not null && declared.StartsWith(VaultFormatPrefix, StringComparison.Ordinal)
            && int.TryParse(declared.AsSpan(VaultFormatPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            && version > VaultFormatVersion;
        throw new VaultFormatException(declared, newer);
    }

    private static readonly string[] LayoutFolders = ["schemes", "reports", "exports", "practitioners", "devices", "subjects", "groups", "runs", "packs", "labels", "fields"];

    // A sync client keeps the losing side of a conflict under the same name with something added
    // after ".json" (" (conflicted copy …)", ".sync-conflict-…", "-<computer>"). A change file's copy
    // is read like the change (identical copies count once); a copy of a scheme, form or run record
    // may differ from the file it copies, and ignoring it like a stray file would lose it without a
    // word, so it is listed for a person to look at.
    private static bool IsCopyOfVaultFile(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || !LayoutFolders.Contains(path[..slash])) return false;
        var name = path[(path.LastIndexOf('/') + 1)..];
        var json = name.IndexOf(".json", StringComparison.Ordinal);
        return json > 0 && json + ".json".Length < name.Length;
    }

    // practitioners/<file>.json, devices/<file>.json, subjects/<subject-id>/<file>.json, groups/<group-id>/<file>.json
    // — or a sync client's copy of one, with something added after ".json".
    private static bool IsChangePath(string path)
    {
        var json = path.LastIndexOf(".json", StringComparison.Ordinal);
        if (json < 0) return false;
        var after = json + ".json".Length;
        if (after < path.Length && (char.IsAsciiLetterOrDigit(path[after]) || path.IndexOf('/', after) >= 0)) return false;
        var parts = path.Split('/');
        return (parts.Length == 2 && parts[0] is "practitioners" or "devices")
            || (parts.Length == 3 && parts[0] is "subjects" or "groups");
    }

    internal readonly record struct Parsed((Change, JsonElement)? Value, UnreadableFile? Error);

    internal static Parsed Fail(VaultFile file, UnreadableReason reason, string detail) =>
        new(null, new UnreadableFile(file.Path, reason, detail));

    private static Parsed ParseChange(VaultFile file)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(file.Content);
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            return Fail(file, UnreadableReason.Malformed, e.Message);
        }

        if (root.ValueKind != JsonValueKind.Object)
            return Fail(file, UnreadableReason.Malformed, "the content is not a JSON object");
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != ChangeFormat)
            return Fail(file, UnreadableReason.UnknownFormat, $"expected format {ChangeFormat}");

        if (!TryString(root, "id", out var id) || !Guid.TryParseExact(id, "D", out _) || id.Any(char.IsAsciiLetterUpper))
            return Fail(file, UnreadableReason.Invalid, "id must be a lowercase hyphenated UUID");
        if (!TryString(root, "device", out var device) || !IsDevice(device))
            return Fail(file, UnreadableReason.Invalid, "device must be 4-16 lowercase letters or digits");
        if (!TryString(root, "at", out var atText)
            || !DateTimeOffset.TryParseExact(atText, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return Fail(file, UnreadableReason.Invalid, "at must be a timestamp with an offset");
        if (!root.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.Object
            || !TryString(entity, "type", out var entityType) || !TryString(entity, "id", out var entityId))
            return Fail(file, UnreadableReason.Invalid, "entity must name a type and an id");
        if (!TryString(root, "op", out var opText) || ParseOp(opText) is not { } op)
            return Fail(file, UnreadableReason.Invalid, "op must be create, update, reclassify or destroy");
        if (!root.TryGetProperty("base", out var baseArray) || baseArray.ValueKind != JsonValueKind.Array
            || baseArray.EnumerateArray().Any(b => b.ValueKind != JsonValueKind.String))
            return Fail(file, UnreadableReason.Invalid, "base must be an array of change ids");
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            return Fail(file, UnreadableReason.Invalid, "fields must be an object");

        var source = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("source", out var sourceObject))
        {
            if (sourceObject.ValueKind != JsonValueKind.Object)
                return Fail(file, UnreadableReason.Invalid, "source must be an object");
            foreach (var p in sourceObject.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not ("manual" or "suggestion"))
                    return Fail(file, UnreadableReason.Invalid, "source values must be manual or suggestion");
                source[p.Name] = p.Value.GetString()!;
            }
        }

        // A sync client may add to the name, before ".json" ("<id>.<device> (conflicted copy).json")
        // or after it ("<id>.<device>.json-LAPTOP"), but the name must still begin with the id and
        // device the content claims.
        var name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
        var stem = name[..name.LastIndexOf(".json", StringComparison.Ordinal)];
        var expected = $"{id}.{device}";
        if (!(stem == expected || (stem.StartsWith(expected, StringComparison.Ordinal) && !char.IsAsciiLetterOrDigit(stem[expected.Length]))))
            return Fail(file, UnreadableReason.NameMismatch, $"the name should begin with {expected}");

        var change = new Change(
            id,
            device,
            at,
            new EntityRef(entityType, entityId),
            op,
            baseArray.EnumerateArray().Select(b => b.GetString()!).ToArray(),
            fields.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal),
            source,
            file.Path);
        return new Parsed((change, root), null);
    }

    internal static bool TryString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = "";
        return false;
    }

    private static bool IsDevice(string device) =>
        device.Length is >= 4 and <= 16 && device.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

    private static ChangeOp? ParseOp(string op) => op switch
    {
        "create" => ChangeOp.Create,
        "update" => ChangeOp.Update,
        "reclassify" => ChangeOp.Reclassify,
        "destroy" => ChangeOp.Destroy,
        _ => null,
    };
}
