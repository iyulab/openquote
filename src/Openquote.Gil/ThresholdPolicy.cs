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
/// <remarks>The similarity scale is the lexical memory's: a threshold chosen for it means nothing for another memory.</remarks>
internal sealed record ThresholdPolicy(double TargetPrecision, int MinimumAnswered, double? NearlySameMemoryThreshold)
{
    /// <summary>The policy every suggester uses.</summary>
    public static ThresholdPolicy Default { get; } = new(TargetPrecision: 0.7, MinimumAnswered: 30, NearlySameMemoryThreshold: 0.5);

    /// <summary>No thresholds at all: every layer only guesses, the field's most frequent code first.</summary>
    public static ThresholdPolicy None { get; } = new(TargetPrecision: 1, MinimumAnswered: int.MaxValue, NearlySameMemoryThreshold: null);

    /// <summary>
    /// The key and memory thresholds of <paramref name="field"/>. A field that takes several values has a key
    /// threshold only: the similar record layer does not answer it.
    /// </summary>
    public async Task<(double? Key, double? Memory)> ChooseAsync(FormDefinition form, FieldDefinition field, IReadOnlyList<SettledDocument> settled,
        CancellationToken cancellationToken)
    {
        if (field.Multiple)
        {
            return settled.Count(d => d.Sets.TryGetValue(field.Name, out var set) && set.Count > 0) < MinimumAnswered
                ? (null, null)
                : (ThresholdSelection.SelectKeyThreshold(new FieldMemory(), form, field.Name, settled, TargetPrecision, MinimumAnswered).Chosen?.Threshold, null);
        }
        if (settled.Count(d => d.Values.ContainsKey(field.Name)) < MinimumAnswered)
        {
            return (null, NearlySameMemoryThreshold);
        }
        var replay = await ThresholdSelection.SelectLayersAsync(new FieldMemory(), new LexicalMemory(), form, field.Name, settled, TargetPrecision, MinimumAnswered,
            cancellationToken).ConfigureAwait(false);
        return (replay.Key.Chosen?.Threshold, replay.Memory.Chosen?.Threshold ?? NearlySameMemoryThreshold);
    }
}
