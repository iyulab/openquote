using System.Globalization;
using System.Text.Json;
using Openquote.Records;

namespace Openquote.Fields;

/// <summary>
/// A value a record takes from its subject (<see cref="FieldDefinition.DefaultFromSubject"/>), as it stands for the
/// record's date.
/// </summary>
/// <param name="Field">The record's field.</param>
/// <param name="Value">The subject's value as written, as text (the code of a coded value); null when it has none.</param>
/// <param name="WrittenOn">The day the subject's value was written, by the writing device's clock; null with no value.</param>
/// <param name="YearsPassed">
/// How many year starts lie after <paramref name="WrittenOn"/> up to the record's date — 0 within the same year, for a
/// record dated before the value was written, and for a field without a <see cref="FieldDefinition.DefaultYear"/>.
/// </param>
/// <param name="Offer">
/// What the record would take: the value itself while it holds; once years have passed, the value advanced by them, or
/// null when nothing can be offered — the value drops, it is not a whole number, it would pass its cap, or the cap is
/// not known and the advanced value would pass one of the caps the value could be under.
/// </param>
public sealed record SubjectCarry(string Field, string? Value, DateOnly? WrittenOn, int YearsPassed, string? Offer)
{
    /// <summary>
    /// True when the subject's value was written in an earlier year than the record's date: a person should look at the
    /// subject's value before the record takes it.
    /// </summary>
    public bool Stale => YearsPassed > 0;
}

/// <summary>How a value taken from the subject carries into a record of a given date.</summary>
public static class SubjectDefaults
{
    /// <summary>
    /// The value <paramref name="field"/> takes from <paramref name="subject"/> for a record dated
    /// <paramref name="recordDate"/>, and whether the year it was written in is over (see <see cref="SubjectCarry"/>).
    /// Nothing is written: a host shows a stale value to a person, who corrects the subject.
    /// </summary>
    /// <exception cref="ArgumentException">The field takes nothing from the subject.</exception>
    public static SubjectCarry Carry(FieldDefinition field, Entity subject, DateOnly recordDate)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(subject);
        if (field.DefaultFromSubject is not { } from)
            throw new ArgumentException($"Field '{field.Name}' takes nothing from the subject.", nameof(field));

        var value = Text(subject, from);
        if (value is null) return new SubjectCarry(field.Name, null, null, 0, null);
        var written = subject.LastWriteOf(from) is { } change ? DateOnly.FromDateTime(change.At.DateTime) : (DateOnly?)null;
        if (field.DefaultYear is not { } rule || written is not { } on) return new SubjectCarry(field.Name, value, written, 0, value);

        var years = YearOf(recordDate, rule.StartMonth) - YearOf(on, rule.StartMonth);
        if (years <= 0) return new SubjectCarry(field.Name, value, on, 0, value);
        var offer = rule.Then == YearStep.Advance ? Advance(value, years, rule.Max, subject) : null;
        return new SubjectCarry(field.Name, value, on, years, offer);
    }

    // The year a day belongs to, named by the calendar year it starts in.
    private static int YearOf(DateOnly day, int startMonth) => day.Month >= startMonth ? day.Year : day.Year - 1;

    private static string? Advance(string value, int years, YearCap? cap, Entity subject)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var current) || current < 1) return null;
        var next = current + years;
        if (cap is null) return Offer(next);
        if (cap.Fixed is { } fixedCap) return next <= fixedCap ? Offer(next) : null;
        if (Text(subject, cap.SubjectField!) is { } level && cap.ByCode.TryGetValue(level, out var known))
            return next <= known ? Offer(next) : null;
        // The cap is not known: offer only what holds under every cap the current value could be under.
        var possible = cap.ByCode.Values.Where(c => current <= c).ToList();
        return possible.Count > 0 && next <= possible.Min() ? Offer(next) : null;
    }

    private static string Offer(int value) => value.ToString(CultureInfo.InvariantCulture);

    // A field's current value as text: a string, a number as written, or the code of a coded value. Empty is none.
    private static string? Text(Entity subject, string field)
    {
        if (!subject.Fields.TryGetValue(field, out var v)) return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Object when v.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String => code.GetString(),
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
