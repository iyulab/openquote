using Openquote.Fields;

namespace Openquote.Records;

/// <summary>A subject's next meeting: its day, and the record that set it.</summary>
/// <param name="Subject">The subject it is with.</param>
/// <param name="Day">The day it was set for.</param>
/// <param name="Record">The record that set it — the subject's latest record of a type that sets one.</param>
public sealed record PlannedMeeting(string Subject, DateOnly Day, Entity Record);

/// <summary>
/// Reads each subject's next meeting, as the packs say a type's records set one (<see cref="FieldCatalog.Plans"/>):
/// the day the subject's latest such record — by its own day — names. A later record takes the place of what an
/// earlier one set, so a latest record naming no day leaves the subject with none. Nothing is stored and nothing is
/// judged — whether the day is today, or has passed, is the caller's arithmetic.
/// </summary>
public static class PlannedReader
{
    /// <summary>
    /// The next meeting of each subject <paramref name="entities"/> are about, by day and then subject. A record of a
    /// group is about each subject it names. Records with no day of their own, and destroyed records, set nothing.
    /// </summary>
    public static IReadOnlyList<PlannedMeeting> Read(IEnumerable<Entity> entities, FieldCatalog fields)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(fields);
        var latest = new Dictionary<string, (DateOnly Day, Entity Record)>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var type = entity.Reference.Type;
            if (entity.Destroyed || fields.Plans(type) is null) continue;
            if (CaseReader.DayOf(entity, fields.DatedField(type)) is not { } day) continue;
            foreach (var person in entity.People)
            {
                if (latest.TryGetValue(person, out var held)
                    && (held.Day > day || (held.Day == day && string.CompareOrdinal(held.Record.Reference.Id, entity.Reference.Id) > 0)))
                    continue;
                latest[person] = (day, entity);
            }
        }
        return
        [
            .. latest
                .Select(p => (Subject: p.Key, p.Value.Record, Next: CaseReader.DayOf(p.Value.Record, fields.Plans(p.Value.Record.Reference.Type)!)))
                .Where(p => p.Next is not null)
                .Select(p => new PlannedMeeting(p.Subject, p.Next!.Value, p.Record))
                .OrderBy(p => p.Day)
                .ThenBy(p => p.Subject, StringComparer.Ordinal),
        ];
    }
}
