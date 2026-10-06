using Gil;
using Gil.Forms;
using Gil.Memory;

namespace Openquote.Gil;

/// <summary>
/// How a suggester chooses each judged field's thresholds — the similarity at which a similar settled record
/// answers, and the score at which a value settled alongside the draft's values does. Once a field has
/// <see cref="MinimumAnswered"/> settled values both are chosen by replaying the settled records in the order
/// they were settled (<see cref="ThresholdSelection.SelectLayersAsync"/>), down to where the answers still
/// reach <see cref="TargetPrecision"/>. Where no similarity threshold is chosen — too few settled values to
/// replay, or no band of answers reaching the target — a record nearly the same as the draft still answers
/// (<see cref="NearlySameMemoryThreshold"/>): such a record is evidence however the rest of the memory does,
/// and without it a code to confirm would never be offered, however plainly a settled record holds it. A
/// key layer reaching the target nowhere does not answer on its own.
/// </summary>
/// <remarks>
/// A replayed threshold is chosen on the score the similar records' vote gives (<see cref="Votes"/> of them), so
/// it is compared with that score. <see cref="NearlySameMemoryThreshold"/> is a similarity — how alike the nearest record reads — so
/// where it applies the nearest record decides alone (<see cref="FieldDefinition.SimilarDocumentVotes"/> 1):
/// compared with a vote, it would let many records that only share a field outvote the one that reads like
/// the draft, and the code chosen most often would answer whatever the draft says. The similarity scale is
/// the lexical memory's: a threshold chosen for it means nothing for another memory.
/// </remarks>
internal sealed record ThresholdPolicy(double TargetPrecision, int MinimumAnswered, double? NearlySameMemoryThreshold)
{
    /// <summary>
    /// How many similar records vote where a threshold is replayed — the nearest alone by default. A vote's
    /// score is how far its winner leads, however weakly the voters read like the draft: a draft in words
    /// no settled record uses still gets a decided vote among records that share only their other fields, and
    /// a threshold replayed on records written in the usual words then answers it wrongly. Synthetic sessions
    /// in other words for the same topic, thresholds replayed for a precision of 0.7: ten voters answered
    /// wrongly 37–58% of them from 240 settled records on, the nearest alone never.
    /// </summary>
    public int Votes { get; init; } = NearestAlone;

    /// <summary>The policy every suggester uses.</summary>
    public static ThresholdPolicy Default { get; } = new(TargetPrecision: 0.7, MinimumAnswered: 30, NearlySameMemoryThreshold: 0.5);

    /// <summary>No thresholds at all: every layer only guesses, the field's most frequent code first.</summary>
    public static ThresholdPolicy None { get; } = new(TargetPrecision: 1, MinimumAnswered: int.MaxValue, NearlySameMemoryThreshold: null);

    /// <summary>
    /// The key and memory thresholds of <paramref name="field"/>, and how many similar records vote. A field
    /// that takes several values has a key threshold only: the similar record layer does not answer it.
    /// </summary>
    public async Task<FieldThresholds> ChooseAsync(FormDefinition form, FieldDefinition field, IReadOnlyList<SettledDocument> settled,
        CancellationToken cancellationToken)
    {
        if (field.Multiple)
        {
            return settled.Count(d => d.Sets.TryGetValue(field.Name, out var set) && set.Count > 0) < MinimumAnswered
                ? new(null, null, Votes)
                : new(ThresholdSelection.SelectKeyThreshold(new FieldMemory(), form, field.Name, settled, TargetPrecision, MinimumAnswered).Chosen?.Threshold, null, Votes);
        }
        if (settled.Count(d => d.Values.ContainsKey(field.Name)) < MinimumAnswered)
        {
            return NearlySame(null);
        }
        if (field.SimilarDocumentVotes != Votes)
        {
            field = field with { SimilarDocumentVotes = Votes };
            form = new FormDefinition(form.Name, [.. form.Fields.Select(f => f.Name == field.Name ? field : f)], form.Language);
        }
        var replay = await ThresholdSelection.SelectLayersAsync(new FieldMemory(), new LexicalMemory(), form, field.Name, settled, TargetPrecision, MinimumAnswered,
            cancellationToken).ConfigureAwait(false);
        var key = replay.Key.Chosen?.Threshold;
        return replay.Memory.Chosen is { } memory ? new(key, memory.Threshold, field.SimilarDocumentVotes) : NearlySame(key);
    }

    /// <summary>The nearest record answering alone, nearly the same as the draft — or never, with no such threshold.</summary>
    private FieldThresholds NearlySame(double? key) => new(key, NearlySameMemoryThreshold, NearestAlone);

    /// <summary>The similar record layer's vote with a single voter: the nearest record, on its similarity.</summary>
    private const int NearestAlone = 1;
}

/// <summary>A judged field's thresholds, and how many similar records vote — the scale its memory threshold is on.</summary>
internal sealed record FieldThresholds(double? Key, double? Memory, int SimilarDocumentVotes);
