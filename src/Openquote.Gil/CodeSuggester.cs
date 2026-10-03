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
/// One suggested code, with its score, why it is suggested (<paramref name="Basis"/>) and the settled
/// records it rests on (<paramref name="Similar"/>, entity ids) — the evidence a host shows for the
/// suggestion. <paramref name="Field"/> names the field whose value the record being entered shares
/// with those records, for <see cref="SuggestionBasis.SameValue"/> only. A code to
/// <paramref name="Confirm"/> is set apart from the others (<see cref="Suggestion.Confirm"/>).
/// </summary>
public sealed record SuggestedCode(string Code, double Score, IReadOnlyList<string> Similar, bool Confirm, SuggestionBasis Basis, string? Field)
{
    /// <summary>Whether the layer that ranked it answered — its threshold was met — rather than guessed.</summary>
    internal bool Trusted { get; init; }

    /// <summary>Whether the settled record nearest to the one being entered holds it.</summary>
    internal bool Nearest { get; init; }
}

/// <summary>Why a code is suggested: what the settled records it rests on have in common with the record being entered.</summary>
public enum SuggestionBasis
{
    /// <summary>The settled records closest to the one being entered hold it; <see cref="SuggestedCode.Similar"/> names them, nearest first.</summary>
    SimilarRecords,

    /// <summary>
    /// It was chosen most in settled records that have the same value as the record being entered in
    /// <see cref="SuggestedCode.Field"/>; <see cref="SuggestedCode.Similar"/> names the latest of them.
    /// </summary>
    SameValue,

    /// <summary>It is among the codes chosen most often in the settled records; no particular record is its evidence, and <see cref="SuggestedCode.Similar"/> is empty.</summary>
    Frequent,
}

/// <summary>
/// Suggests codes for the coded fields of one entity type, learned from the vault's settled records with
/// Gil's form resolver and a lexical memory — no model, nothing written anywhere: everything is built in
/// memory from the vault each time.
/// </summary>
/// <remarks>
/// <para>
/// Each field's thresholds are chosen by replaying the settled records once there are enough of them, so
/// the codes of records close to the one being entered, or of a value settled alongside its values often enough,
/// lead; the codes chosen most often follow as guesses. A code a person has to confirm is offered only on the
/// evidence of settled records like the one being entered: the nearest holds it, or they are close enough to answer.
/// </para>
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
    private readonly IReadOnlyList<SettledDocument> _settled;

    private CodeSuggester(FormDefinition? form, FormResolver? resolver, IReadOnlyDictionary<string, Target> targets,
        IReadOnlyList<VaultField> fields, SchemeCatalog catalog, DateOnly date, IReadOnlyList<SettledDocument> settled)
    {
        _form = form;
        _resolver = resolver;
        _targets = targets;
        _fields = fields;
        _catalog = catalog;
        _date = date;
        _settled = settled;
    }

    /// <summary>How many settled records the suggestions are learned from; none when there is nothing to suggest for.</summary>
    public int Remembered => _settled.Count;

    /// <summary>The coded fields this suggester suggests codes for.</summary>
    public IReadOnlyCollection<string> Fields => [.. _targets.Keys];

    /// <summary>
    /// Builds a suggester for records of <paramref name="type"/> entered on <paramref name="date"/>,
    /// learning from the settled records in <paramref name="content"/>.
    /// </summary>
    public static Task<CodeSuggester> BuildAsync(VaultContent content, string type, DateOnly date, CancellationToken cancellationToken = default) =>
        BuildAsync(content, type, date, ThresholdPolicy.Default, cancellationToken);

    /// <summary>As <see cref="BuildAsync(VaultContent, string, DateOnly, CancellationToken)"/>, with the thresholds chosen by <paramref name="thresholds"/>.</summary>
    internal static async Task<CodeSuggester> BuildAsync(VaultContent content, string type, DateOnly date, ThresholdPolicy thresholds,
        CancellationToken cancellationToken = default)
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
            return new CodeSuggester(null, null, targets, fields, catalog, date, []);
        }
        var settled = EntityMerger.Merge(content.Changes).Values
            .Where(e => e.Reference.Type == type && !e.Destroyed && e.Heads.Count == 1)
            .Select(e => new SettledDocument(e.Reference.Id, Values(e.Fields, fields, catalog, date, e), e.Changes.Max(c => c.At)))
            .ToList();
        var form = await WithThresholdsAsync(type, formFields, settled, thresholds, cancellationToken).ConfigureAwait(false);
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), similarDocumentCount: SimilarCount);
        await resolver.RebuildAsync(form, settled, cancellationToken).ConfigureAwait(false);
        return new CodeSuggester(form, resolver, targets, fields, catalog, date, settled);
    }

    /// <summary>
    /// The form with each judged field's thresholds: chosen by replaying the settled records once there are
    /// enough of them (<see cref="ThresholdPolicy"/>), so that a similar record or a value settled alongside
    /// the draft's values answers before the guesses — the field's most frequent code above all. Without
    /// thresholds every layer only guesses, and the most frequent code leads whatever the record says.
    /// </summary>
    private static async Task<FormDefinition> WithThresholdsAsync(string type, List<GilField> formFields, List<SettledDocument> settled,
        ThresholdPolicy policy, CancellationToken cancellationToken)
    {
        var form = new FormDefinition(type, formFields, PromptLanguage.English);
        var fields = new List<GilField>(formFields.Count);
        foreach (var field in formFields)
        {
            if (field.Role != FieldRole.Judged)
            {
                fields.Add(field);
                continue;
            }
            var (key, memory) = await policy.ChooseAsync(form, field.Name, settled, cancellationToken).ConfigureAwait(false);
            fields.Add(new GilField(field.Name, field.Role) { Candidates = field.Candidates, KeyThreshold = key, MemoryThreshold = memory });
        }
        return new FormDefinition(type, fields, PromptLanguage.English);
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
            // The field's candidates are exactly the items that may be suggested, so every value offered is one of them.
            // A code to confirm is offered only on settled records like this one: the nearest of them holds it, or they
            // are close enough to answer — never because it is chosen often or alongside a shared value, which says
            // nothing about this record. The nearest counts below the threshold: a record's other fields dilute how
            // alike two records read, and a code to confirm is the one a person must not miss.
            var nearest = suggestion.SimilarDocuments.Count > 0 ? suggestion.SimilarDocuments[0].Answer : null;
            var codes = suggestion.Candidates.Select(c => Suggested(c, suggestion, values, target) with { Trusted = c.Trusted, Nearest = c.Value == nearest })
                .Where(c => !c.Confirm || ((c.Trusted || c.Nearest) && c.Basis == SuggestionBasis.SimilarRecords))
                .ToList();
            if (codes.Count > 0)
            {
                result.Add(new FieldSuggestions(suggestion.Field, target.Scheme.Name, target.Scheme.Version, codes));
            }
        }
        return result;
    }

    /// <summary>
    /// A candidate as suggested, with the evidence of the layer that ranked it: the similar records holding
    /// it, the settled records sharing the value it was chosen alongside, or none for a code chosen often.
    /// </summary>
    private SuggestedCode Suggested(FieldCandidate candidate, FieldSuggestion suggestion, Dictionary<string, string> values, Target target)
    {
        var confirm = target.Codes[candidate.Value] == Suggestion.Confirm;
        // A guess the nearest similar record holds is offered on that record, whichever guess put it in the list:
        // that is truer evidence than being chosen often.
        var nearest = suggestion.SimilarDocuments.Count > 0 && suggestion.SimilarDocuments[0].Answer == candidate.Value;
        switch (candidate.Source)
        {
            case FieldSource.SimilarDocument:
            case FieldSource.SettledFieldMemory when !candidate.Trusted && nearest:
                return new SuggestedCode(candidate.Value, candidate.Score,
                    [.. suggestion.SimilarDocuments.Where(m => m.Answer == candidate.Value).Select(m => m.Source)], confirm, SuggestionBasis.SimilarRecords, null);
            case FieldSource.SettledFieldMemory when KeyField(candidate.Evidence) is { } field && values.TryGetValue(field, out var shared):
                var alongside = _settled
                    .Where(d => d.Values.TryGetValue(suggestion.Field, out var code) && code == candidate.Value
                        && d.Values.TryGetValue(field, out var value) && SameValue(value, shared))
                    .OrderByDescending(d => d.SettledAt)
                    .ThenBy(d => d.DocumentId, StringComparer.Ordinal)
                    .Take(SimilarCount)
                    .Select(d => d.DocumentId);
                return new SuggestedCode(candidate.Value, candidate.Score, [.. alongside], confirm, SuggestionBasis.SameValue, field);
            case FieldSource.SettledFieldMemory:
                return new SuggestedCode(candidate.Value, candidate.Score, [], confirm, SuggestionBasis.Frequent, null);
            default:
                // Only the two memories are consulted here: a candidate from anywhere else would be shown with the wrong reason.
                throw new InvalidOperationException($"A suggestion came from {candidate.Source}, which this suggester does not consult.");
        }
    }

    /// <summary>The field of a field memory's evidence — <c>field: value</c>, the value as the memory normalised it.</summary>
    private string? KeyField(string? evidence) =>
        evidence?.IndexOf(": ", StringComparison.Ordinal) is > 0 and var at && _fields.Any(f => f.Name == evidence[..at]) ? evidence[..at] : null;

    private static bool SameValue(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

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
