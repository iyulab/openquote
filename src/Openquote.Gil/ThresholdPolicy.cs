using Gil;
using Gil.Forms;
using Gil.Memory;

namespace Openquote.Gil;

/// <summary>
/// How a suggester chooses each judged field's thresholds — the similarity at which a similar settled record
/// answers, and the score at which a value settled alongside the draft's values does. Once a field has
/// <see cref="MinimumAnswered"/> settled values both are chosen by replaying the settled records in the order
/// they were settled (<see cref="ThresholdSelection.SelectLayersAsync"/>), down to where the answers still
/// reach <see cref="TargetPrecision"/>; a layer that reaches it nowhere does not answer on its own. With
/// fewer, a replay rests on too few answers to choose by, and only a record nearly the same as the draft
/// answers (<see cref="FewRecordsMemoryThreshold"/>).
/// </summary>
/// <remarks>The similarity scale is the lexical memory's: a threshold chosen for it means nothing for another memory.</remarks>
internal sealed record ThresholdPolicy(double TargetPrecision, int MinimumAnswered, double? FewRecordsMemoryThreshold)
{
    /// <summary>The policy every suggester uses.</summary>
    public static ThresholdPolicy Default { get; } = new(TargetPrecision: 0.7, MinimumAnswered: 30, FewRecordsMemoryThreshold: 0.5);

    /// <summary>No thresholds at all: every layer only guesses, the field's most frequent code first.</summary>
    public static ThresholdPolicy None { get; } = new(TargetPrecision: 1, MinimumAnswered: int.MaxValue, FewRecordsMemoryThreshold: null);

    /// <summary>The key and memory thresholds of <paramref name="field"/>.</summary>
    public async Task<(double? Key, double? Memory)> ChooseAsync(FormDefinition form, string field, IReadOnlyList<SettledDocument> settled,
        CancellationToken cancellationToken)
    {
        if (settled.Count(d => d.Values.ContainsKey(field)) < MinimumAnswered)
        {
            return (null, FewRecordsMemoryThreshold);
        }
        var replay = await ThresholdSelection.SelectLayersAsync(new FieldMemory(), new LexicalMemory(), form, field, settled, TargetPrecision, MinimumAnswered,
            cancellationToken).ConfigureAwait(false);
        return (replay.Key.Chosen?.Threshold, replay.Memory.Chosen?.Threshold);
    }
}
