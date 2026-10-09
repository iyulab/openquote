using System.Globalization;
using System.Text.Json;
using Openquote.Fields;

namespace Openquote.Records;

/// <summary>
/// One stretch of work with a subject: from the record that opened it, or the first record when none did, to the
/// record that closed it. Nothing marks a record as belonging to a case — <see cref="CaseReader"/> reads the cases
/// from the records' dates and their types' roles each time.
/// </summary>
public sealed class SubjectCase
{
    private readonly List<Entity> _records = [];
    private readonly List<Entity> _afterClosing = [];

    internal SubjectCase(Entity first, DateOnly start, bool opened)
    {
        Opening = opened ? first : null;
        Start = start;
        _records.Add(first);
    }

    /// <summary>The record that opened the case, or null when it began without one (records came before any opening).</summary>
    public Entity? Opening { get; }

    /// <summary>The record that closed the case, or null when none has.</summary>
    public Entity? Closing { get; private set; }

    /// <summary>The day of the case's first record.</summary>
    public DateOnly Start { get; }

    /// <summary>The day of the record that closed the case, or null when none has.</summary>
    public DateOnly? End { get; private set; }

    /// <summary>
    /// The case's records in time order, from the first (the opening, when there is one) to the closing, both included,
    /// and the records of the closing's own day written after it: the day a case closes is the case's.
    /// </summary>
    public IReadOnlyList<Entity> Records => _records;

    /// <summary>
    /// The records dated after the closing's day and before the next opening, in time order — a follow-up, say — and a
    /// closing that confirms the first, on any day. They do not reopen the case or start another.
    /// </summary>
    public IReadOnlyList<Entity> AfterClosing => _afterClosing;

    /// <summary>
    /// True when another opening came while this case was still open: the case ended there without a record closing
    /// it. It is not counted as closed.
    /// </summary>
    public bool FollowedByOpening { get; private set; }

    /// <summary>True while no record has closed the case and no later opening has followed it.</summary>
    public bool IsOpen => Closing is null && !FollowedByOpening;

    /// <summary>
    /// The subject's first record on a day after the closing's — the first of <see cref="AfterClosing"/> on such a day,
    /// or the record that began the next case — or null when none has come or the case has not closed. A record of the
    /// closing's own day is not a follow-up.
    /// </summary>
    public Entity? FirstAfterClosing { get; private set; }

    /// <summary>The day of <see cref="FirstAfterClosing"/>, or null when there is none.</summary>
    public DateOnly? FirstAfterClosingDay { get; private set; }

    /// <summary>
    /// The last day a follow-up is expected by: the closing's day and the days its type gives
    /// (<see cref="FieldCatalog.FollowUpDays"/>). Null when the case has not closed or its closing expects none.
    /// </summary>
    public DateOnly? FollowUpDue { get; private set; }

    /// <summary>
    /// Where the follow-up after the closing stands on <paramref name="day"/> — any record of the subject after the
    /// closing counts as one. Nothing is judged about the subject; the host lists what it is asked to.
    /// </summary>
    public FollowUp FollowUpOn(DateOnly day)
    {
        if (FollowUpDue is not { } due) return FollowUp.NotExpected;
        if (FirstAfterClosingDay is { } came && came <= due) return FollowUp.Done;
        if (FirstAfterClosingDay is { } late && late <= day) return FollowUp.Late;
        return day <= due ? FollowUp.Waiting : FollowUp.Overdue;
    }

    internal void Add(Entity record) => _records.Add(record);

    internal void Close(Entity record, DateOnly day, int? followUpDays)
    {
        if (!ReferenceEquals(_records[^1], record)) _records.Add(record);
        Closing = record;
        End = day;
        FollowUpDue = followUpDays is { } days ? day.AddDays(days) : null;
    }

    internal void Follow(Entity record, DateOnly day) => (FirstAfterClosing, FirstAfterClosingDay) = (record, day);

    internal void AddAfterClosing(Entity record) => _afterClosing.Add(record);

    internal void EndByOpening() => FollowedByOpening = true;
}

/// <summary>Where the follow-up after a case's closing stands on a day (<see cref="SubjectCase.FollowUpOn"/>).</summary>
public enum FollowUp
{
    /// <summary>The case has not closed, or its closing expects no follow-up.</summary>
    NotExpected,

    /// <summary>A record of the subject came after the closing, by the day it was due.</summary>
    Done,

    /// <summary>The first record of the subject after the closing came after the day it was due.</summary>
    Late,

    /// <summary>No record of the subject has come after the closing yet, and the day it is due has not passed.</summary>
    Waiting,

    /// <summary>No record of the subject came after the closing, and the day it was due has passed.</summary>
    Overdue,
}

/// <summary>A subject's cases, oldest first, and the records that could not be placed in time.</summary>
/// <param name="Cases">The cases in the order they began.</param>
/// <param name="Undated">
/// The subject's records whose dating field (<see cref="FieldCatalog.DatedField"/>) holds no date, by id. They are in
/// no case; a host shows them apart rather than losing them.
/// </param>
public sealed record SubjectCases(IReadOnlyList<SubjectCase> Cases, IReadOnlyList<Entity> Undated);

/// <summary>
/// Reads a subject's cases from its records. A pack says which entity types open and close a case
/// (<see cref="FieldCatalog.TypeRole"/>) and which field dates a record (<see cref="FieldCatalog.DatedField"/>);
/// the engine knows no type by name.
/// </summary>
public static class CaseReader
{
    /// <summary>
    /// The cases of <paramref name="subject"/> among <paramref name="entities"/>. The subject's records — those kept in
    /// its folder, and a group's records that list it among their attendees (<see cref="Entity.People"/>) — are read
    /// by their dating field, in day order and then in the order they were written (by id). Destroyed records and the
    /// types kept on their own are left out. In that order:
    /// <list type="number">
    /// <item>A record of a type that opens a case starts a new case. A case still open then ends there, followed by
    /// the opening (<see cref="SubjectCase.FollowedByOpening"/>) and not counted as closed.</item>
    /// <item>A record of a type that closes a case closes the open case. With none open, a case that began without an
    /// opening holds just that closing; after a closed case, it is one of that case's
    /// <see cref="SubjectCase.AfterClosing"/>.</item>
    /// <item>Any other record joins the open case. With none open it begins a case without an opening — unless a case
    /// has closed, whose <see cref="SubjectCase.AfterClosing"/> it then joins.</item>
    /// </list>
    /// </summary>
    public static SubjectCases Read(string subject, IEnumerable<Entity> entities, FieldCatalog fields)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(fields);

        var dated = new List<(DateOnly Day, Entity Record)>();
        var undated = new List<Entity>();
        foreach (var entity in entities)
        {
            var type = entity.Reference.Type;
            if (entity.Destroyed || FieldCatalog.IsKeptOnItsOwn(type) || !entity.People.Contains(subject, StringComparer.Ordinal))
                continue;
            if (DayOf(entity, fields.DatedField(type)) is { } day) dated.Add((day, entity));
            else undated.Add(entity);
        }

        var cases = new List<SubjectCase>();
        SubjectCase? open = null;
        SubjectCase? closed = null;
        // The case closed last whose first record on a later day than the closing has not come yet.
        SubjectCase? following = null;
        foreach (var (day, record) in dated.OrderBy(r => r.Day).ThenBy(r => r.Record.Reference.Id, StringComparer.Ordinal))
        {
            if (following is not null && day > following.End)
            {
                following.Follow(record, day);
                following = null;
            }
            var followUpDays = fields.FollowUpDays(record.Reference.Type);
            switch (fields.TypeRole(record.Reference.Type))
            {
                case CaseRole.Opens:
                    open?.EndByOpening();
                    open = new SubjectCase(record, day, opened: true);
                    cases.Add(open);
                    closed = null;
                    break;
                case CaseRole.Closes when open is not null:
                    open.Close(record, day, followUpDays);
                    (closed, open, following) = (open, null, open);
                    break;
                case CaseRole.Closes when closed is not null:
                    closed.AddAfterClosing(record);
                    break;
                case CaseRole.Closes:
                    var unopened = new SubjectCase(record, day, opened: false);
                    unopened.Close(record, day, followUpDays);
                    cases.Add(unopened);
                    closed = following = unopened;
                    break;
                default:
                    // The day a case closes is the case's own: a record of that day joins it, whichever was written first.
                    if (open is not null) open.Add(record);
                    else if (closed is not null && day == closed.End) closed.Add(record);
                    else if (closed is not null) closed.AddAfterClosing(record);
                    else cases.Add(open = new SubjectCase(record, day, opened: false));
                    break;
            }
        }

        return new SubjectCases(cases, [.. undated.OrderBy(e => e.Reference.Id, StringComparer.Ordinal)]);
    }

    internal static DateOnly? DayOf(Entity entity, string field) =>
        entity.Fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
}
