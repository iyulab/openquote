using System.Globalization;
using System.Text.Json;
using Gil;
using Gil.Forms;
using Gil.Memory;
using Openquote.Classification;
using Openquote.Fields;
using Openquote.Records;
using Openquote.Suggestions;
using Openquote.Vault;
using GilField = Gil.FieldDefinition;
using VaultField = Openquote.Fields.FieldDefinition;

namespace Openquote.Gil;

/// <summary>
/// The codes suggested for one coded field of a record being entered: the field, the scheme version
/// they belong to, and the codes in the order they are suggested.
/// </summary>
public sealed record FieldSuggestions(string Field, string Scheme, int Version, IReadOnlyList<SuggestedCode> Codes);

/// <summary>
/// One suggested code, with its score and the settled records closest to the one being entered that
/// hold it (entity ids, nearest first) — the evidence a host shows for the suggestion. A code to
/// <paramref name="Confirm"/> is set apart from the others (<see cref="Suggestion.Confirm"/>).
/// </summary>
public sealed record SuggestedCode(string Code, double Score, IReadOnlyList<string> Similar, bool Confirm);

/// <summary>
/// Suggests codes for the coded fields of one entity type, learned from the vault's settled records with
/// Gil's form resolver and a lexical memory — no model, nothing written anywhere: everything is built in
/// memory from the vault each time.
/// </summary>
/// <remarks>
/// <para>
/// A coded field is suggested for when the scheme version in force on the given date has items that may be
/// suggested (<see cref="SuggestionCatalog"/>: as the scheme marks them, or as the vault's packs say over
/// it), and only those items are ever suggested. The version is the one a host offers for
/// input on that date (<see cref="SchemeCatalog.InForce"/>); a settled record's value is carried to it
/// the way reports place records (<see cref="Entity.Classify"/>), and a value that does not land on
/// exactly one code there is left out.
/// </para>
/// <para>
/// Settled records are the entities of the type that are not destroyed and have a single head: a record
/// changed on two devices at once has values no one has settled yet. Reference fields — who the record
/// is about or who wrote it — are never used as evidence, so a person's identity does not decide a
/// suggestion. Every other field, written content included, is. A suggestion is a candidate for a
/// person to take or leave; nothing is filled in.
/// </para>
/// <para>An instance is not safe for concurrent use.</para>
/// </remarks>
public sealed class CodeSuggester
{
    private const string Draft = "draft";
    private const int SimilarCount = 5;

    private readonly FormDefinition? _form;
    private readonly FormResolver? _resolver;
    private readonly IReadOnlyDictionary<string, Target> _targets;
    private readonly IReadOnlyList<VaultField> _fields;
    private readonly SchemeCatalog _catalog;
    private readonly DateOnly _date;

    private CodeSuggester(FormDefinition? form, FormResolver? resolver, IReadOnlyDictionary<string, Target> targets,
        IReadOnlyList<VaultField> fields, SchemeCatalog catalog, DateOnly date, int remembered)
    {
        _form = form;
        _resolver = resolver;
        _targets = targets;
        _fields = fields;
        _catalog = catalog;
        _date = date;
        Remembered = remembered;
    }

    /// <summary>How many settled records the suggestions are learned from; none when there is nothing to suggest for.</summary>
    public int Remembered { get; }

    /// <summary>The coded fields this suggester suggests codes for.</summary>
    public IReadOnlyCollection<string> Fields => [.. _targets.Keys];

    /// <summary>
    /// Builds a suggester for records of <paramref name="type"/> entered on <paramref name="date"/>,
    /// learning from the settled records in <paramref name="content"/>.
    /// </summary>
    public static async Task<CodeSuggester> BuildAsync(VaultContent content, string type, DateOnly date, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(type);
        var catalog = content.Catalog();
        var suggestible = content.SuggestionCatalog();
        var fields = content.FieldCatalog().For(type).Where(f => !f.Hidden).ToList();
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        var formFields = new List<GilField>();
        foreach (var field in fields)
        {
            if (field.Kind == FieldKind.Coded && field.Scheme is { } name && catalog.InForce(name, date) is { } scheme
                && scheme.Items.Select(i => (i.Code, Suggestion: suggestible.For(name, scheme.Version, i))).Where(i => i.Suggestion != Suggestion.Off)
                    .ToDictionary(i => i.Code, i => i.Suggestion, StringComparer.Ordinal) is { Count: > 0 } codes)
            {
                targets[field.Name] = new Target(scheme, codes);
                formFields.Add(new GilField(field.Name, FieldRole.Judged) { Candidates = [.. codes.Keys] });
            }
            else
            {
                var evidence = field.Kind is not (FieldKind.Reference or FieldKind.References);
                formFields.Add(new GilField(field.Name, FieldRole.Observed) { UseAsEvidence = evidence });
            }
        }
        if (targets.Count == 0)
        {
            // Nothing to suggest for: no settled record is read.
            return new CodeSuggester(null, null, targets, fields, catalog, date, 0);
        }
        var form = new FormDefinition(type, formFields, PromptLanguage.English);
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), similarDocumentCount: SimilarCount);
        var settled = EntityMerger.Merge(content.Changes).Values
            .Where(e => e.Reference.Type == type && !e.Destroyed && e.Heads.Count == 1)
            .Select(e => new SettledDocument(e.Reference.Id, Values(e.Fields, fields, catalog, date, e), e.Changes.Max(c => c.At)))
            .ToList();
        await resolver.RebuildAsync(form, settled, cancellationToken).ConfigureAwait(false);
        return new CodeSuggester(form, resolver, targets, fields, catalog, date, settled.Count);
    }

    /// <summary>
    /// Suggests codes for the coded fields <paramref name="draft"/> has no value for yet, from the values
    /// it has. Fields with nothing to suggest are left out.
    /// </summary>
    public async Task<IReadOnlyList<FieldSuggestions>> SuggestAsync(IReadOnlyDictionary<string, JsonElement> draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (_form is null || _resolver is null)
        {
            return [];
        }
        var values = Values(draft, _fields, _catalog, _date, entity: null);
        var suggestions = await _resolver.SuggestAsync(_form, Draft, values, cancellationToken).ConfigureAwait(false);
        var result = new List<FieldSuggestions>();
        foreach (var suggestion in suggestions)
        {
            if (!_targets.TryGetValue(suggestion.Field, out var target) || values.ContainsKey(suggestion.Field))
            {
                continue;
            }
            // The memory offers any value a settled record holds; only the items that may be suggested are kept.
            var codes = suggestion.Candidates
                .Where(c => target.Codes.ContainsKey(c.Value))
                .Select(c => new SuggestedCode(c.Value, c.Score, [.. suggestion.SimilarDocuments.Where(m => m.Answer == c.Value).Select(m => m.Source)],
                    target.Codes[c.Value] == Suggestion.Confirm))
                .ToList();
            if (codes.Count > 0)
            {
                result.Add(new FieldSuggestions(suggestion.Field, target.Scheme.Name, target.Scheme.Version, codes));
            }
        }
        return result;
    }

    /// <summary>
    /// A record's values as the form reads them: a coded value as its code in the version in force on
    /// <paramref name="date"/>, a reference as its id, anything else as written.
    /// </summary>
    private static Dictionary<string, string> Values(IReadOnlyDictionary<string, JsonElement> fields, IReadOnlyList<VaultField> declared,
        SchemeCatalog catalog, DateOnly date, Entity? entity)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in declared)
        {
            if (!fields.TryGetValue(field.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }
            var text = field.Kind == FieldKind.Coded ? Code(field, value, catalog, date, entity) : Text(value);
            if (!string.IsNullOrEmpty(text))
            {
                values[field.Name] = text;
            }
        }
        return values;
    }

    private static string? Code(VaultField field, JsonElement value, SchemeCatalog catalog, DateOnly date, Entity? entity)
    {
        if (field.Scheme is not { } name || catalog.InForce(name, date) is not { } scheme)
        {
            return null;
        }
        if (entity is not null)
        {
            var resolution = entity.Classify(field.Name, name, scheme.Version, catalog);
            return resolution.Kind == ResolutionKind.Assigned ? resolution.Code : null;
        }
        return CodedValue.From(value) is { } coded && coded.Scheme == name && coded.Version == scheme.Version ? coded.Code : null;
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(' ', value.EnumerateArray().Select(Text).Where(t => !string.IsNullOrEmpty(t))),
        _ => null,
    };

    private sealed record Target(Scheme Scheme, Dictionary<string, Suggestion> Codes);
}
