using System.Text.Json;
using Openquote.Fields;

namespace Openquote.Records;

/// <summary>A record still waiting for its answer: the record, the subjects it is about, and the day it was made.</summary>
/// <param name="Record">The waiting record.</param>
/// <param name="People">The subjects it is about.</param>
/// <param name="Day">Its day — the type's dated field — or null when it has none.</param>
public sealed record WaitingRecord(Entity Record, IReadOnlyList<string> People, DateOnly? Day);

/// <summary>
/// Reads which records still wait for an answer, as the packs say each type does (<see cref="FieldCatalog.Awaits"/>):
/// a record waits while the field is blank or holds one of the codes that say no answer has come yet. Nothing is
/// stored and nothing is judged — how long a record has waited is the caller's arithmetic.
/// </summary>
public static class AwaitingReader
{
    /// <summary>
    /// The records of <paramref name="entities"/> still waiting for an answer, the oldest day first (records with no
    /// day last), each tie by id. Destroyed records are not among them.
    /// </summary>
    public static IReadOnlyList<WaitingRecord> Read(IEnumerable<Entity> entities, FieldCatalog fields)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(fields);
        var waiting = new List<WaitingRecord>();
        foreach (var entity in entities)
        {
            if (entity.Destroyed || fields.Awaits(entity.Reference.Type) is not { } awaits) continue;
            if (!Waits(entity, awaits)) continue;
            waiting.Add(new WaitingRecord(entity, entity.People, CaseReader.DayOf(entity, fields.DatedField(entity.Reference.Type))));
        }
        return
        [
            .. waiting
                .OrderBy(w => w.Day is null)
                .ThenBy(w => w.Day)
                .ThenBy(w => w.Record.Reference.Id, StringComparer.Ordinal),
        ];
    }

    // Blank, or every value it holds is a code that says no answer has come yet.
    private static bool Waits(Entity entity, Awaiting awaits)
    {
        if (!entity.HasValue(awaits.Field)) return true;
        var value = entity.Fields[awaits.Field];
        var values = value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : new List<JsonElement> { value };
        return values.All(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String && awaits.Pending.Contains(code.GetString()!));
    }
}
