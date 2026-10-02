namespace Openquote.Reports;

/// <summary>The stretch of days a report form is run over, as people ask for it.</summary>
public enum PeriodUnit
{
    /// <summary>One calendar day.</summary>
    Day,

    /// <summary>One calendar month.</summary>
    Month,

    /// <summary>One year, starting on the first day of <see cref="ReportPeriod.StartMonth"/> — a calendar year when that is January.</summary>
    Year,

    /// <summary>Any stretch of days a person picks.</summary>
    Range,
}

/// <summary>
/// How a report form places records in time: the calendar-date field that puts a record on a day,
/// and the unit of the periods the form is run over.
/// </summary>
/// <param name="Field">The calendar-date field that places a record in a period.</param>
/// <param name="Unit">The unit the form is run over.</param>
/// <param name="StartMonth">For <see cref="PeriodUnit.Year"/>, the month (1–12) a year starts in — 3 for a school year from March; 1 otherwise.</param>
public sealed record ReportPeriod(string Field, PeriodUnit Unit = PeriodUnit.Month, int StartMonth = 1)
{
    /// <summary>
    /// The first and last day of the period of this unit that holds <paramref name="day"/>; null for
    /// <see cref="PeriodUnit.Range"/>, whose days a person picks.
    /// </summary>
    public (DateOnly From, DateOnly To)? Containing(DateOnly day)
    {
        switch (Unit)
        {
            case PeriodUnit.Day:
                return (day, day);
            case PeriodUnit.Month:
                var month = new DateOnly(day.Year, day.Month, 1);
                return (month, month.AddMonths(1).AddDays(-1));
            case PeriodUnit.Year:
                var start = new DateOnly(day.Month >= StartMonth ? day.Year : day.Year - 1, StartMonth, 1);
                return (start, start.AddYears(1).AddDays(-1));
            default:
                return null;
        }
    }

    /// <summary>Why the period cannot be used — a start month outside 1–12, or given for a unit other than a year — or null.</summary>
    public string? Problem() =>
        StartMonth is < 1 or > 12 ? "a year starts in a month from 1 to 12"
        : Unit != PeriodUnit.Year && StartMonth != 1 ? "only a year has a start month"
        : null;
}
