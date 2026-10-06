using System.Text.Json;
using Openquote.Fields;
using Openquote.Records;

namespace Openquote.Scales;

/// <summary>Why a response gives no score to read.</summary>
public enum UnusableReason
{
    /// <summary>Its scale field holds no single code.</summary>
    NoScale,

    /// <summary>Its scale field names a code no pack gives as a scale of its type.</summary>
    UnknownScale,

    /// <summary>Its score field holds no number.</summary>
    NoScore,

    /// <summary>Its score is outside the range the scale gives.</summary>
    OutOfRange,

    /// <summary>Devices recorded different values in its scale or score field, not yet settled.</summary>
    Conflicted,
}

/// <summary>A response in a case that gives no score to read, and why.</summary>
public sealed record UnusableResponse(Entity Record, UnusableReason Reason);

/// <summary>A score a response gives: the record, the day it is dated, and the score.</summary>
public sealed record ScaleScore(Entity Record, DateOnly Day, decimal Score);

/// <summary>
/// One scale over one case: the case's first score (its baseline) and its last available score up to the closing —
/// or up to now while it is open — and how many responses gave one. Both come from records already kept; nothing is
/// stored.
/// </summary>
/// <param name="Scale">The scale.</param>
/// <param name="Baseline">The case's first score.</param>
/// <param name="Last">The case's last score up to its closing.</param>
/// <param name="Responses">The responses that gave a score, the baseline and the last among them.</param>
public sealed record CaseScale(Scale Scale, ScaleScore Baseline, ScaleScore Last, int Responses)
{
    /// <summary>The case has scores on two different days, so there is a change to read.</summary>
    public bool Paired => Last.Day != Baseline.Day;

    /// <summary>The last score less the baseline, when the case is paired; null otherwise. Its sign is only arithmetic.</summary>
    public decimal? Change => Paired ? Last.Score - Baseline.Score : null;
}

/// <summary>A case and its scales, with the responses in it that give no score.</summary>
public sealed record CaseScales(SubjectCase Case, IReadOnlyList<CaseScale> Scales, IReadOnlyList<UnusableResponse> Unusable);

/// <summary>
/// The scales of the cases closed in a stretch of days, by scale.
/// </summary>
/// <param name="Scale">The scale.</param>
/// <param name="Cases">The closed cases with a score of it.</param>
public sealed record ScaleTally(Scale Scale, IReadOnlyList<CaseScale> Cases)
{
    /// <summary>The closed cases with a score of the scale.</summary>
    public int Scored => Cases.Count;

    /// <summary>Those with scores on two different days.</summary>
    public int Paired => Cases.Count(c => c.Paired);
}

/// <summary>
/// What the cases closed in a stretch of days hold of each scale. Every closed case is counted somewhere: a case with
/// no score of any scale is <see cref="Unscored"/>, so a case without a change to read is never left out unseen.
/// </summary>
/// <param name="Closed">The cases closed in the days.</param>
/// <param name="Unscored">Those with no score of any scale.</param>
/// <param name="Scales">Each scale some closed case has a score of, by code.</param>
public sealed record ScaleSummary(int Closed, int Unscored, IReadOnlyList<ScaleTally> Scales);

/// <summary>
/// Reads what a subject's responses to scales say over each of its cases. A pack says which entity type is a
/// response and which fields name the scale and hold the score (<see cref="ScaleSet"/>); the engine knows no
/// scale by name, and it judges no score.
/// </summary>
public static class ScaleReader
{
    /// <summary>
    /// The scales of each of <paramref name="cases"/>, in the cases' order. A case's responses are its records of a
    /// response type (<see cref="ScaleCatalog.ResponsesOf"/>), from its first record to its closing; the records after
    /// a closing are not part of it. For each scale a response names, the first score is the baseline and the last is
    /// the last available score. A response whose scale or score cannot be read is listed apart with the reason.
    /// </summary>
    public static IReadOnlyList<CaseScales> Read(SubjectCases cases, FieldCatalog fields, ScaleCatalog scales)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(scales);
        return [.. cases.Cases.Select(c => ReadCase(c, fields, scales))];
    }

    /// <summary>
    /// What the cases closed from <paramref name="from"/> to <paramref name="to"/> (both included) hold of each scale:
    /// a case counts on the day its closing is dated. Pass the scales of every subject's cases.
    /// </summary>
    public static ScaleSummary Summarize(IEnumerable<CaseScales> cases, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var closed = cases.Where(c => c.Case.Closing is not null && c.Case.End is { } end && end >= from && end <= to).ToList();
        var tallies = closed.SelectMany(c => c.Scales)
            .GroupBy(m => m.Scale.Code, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ScaleTally(g.First().Scale, [.. g]))
            .ToList();
        return new ScaleSummary(closed.Count, closed.Count(c => c.Scales.Count == 0), tallies);
    }

    private static CaseScales ReadCase(SubjectCase subjectCase, FieldCatalog fields, ScaleCatalog scales)
    {
        var scores = new List<(Scale Scale, ScaleScore Score)>();
        var unusable = new List<UnusableResponse>();
        foreach (var record in subjectCase.Records)
        {
            var type = record.Reference.Type;
            if (scales.ResponsesOf(type) is not { } set) continue;
            if (record.Conflicts.ContainsKey(set.ScaleField) || record.Conflicts.ContainsKey(set.ScoreField))
            {
                unusable.Add(new(record, UnusableReason.Conflicted));
                continue;
            }

            if (CodeOf(record, set.ScaleField) is not { } code)
            {
                unusable.Add(new(record, UnusableReason.NoScale));
                continue;
            }

            if (scales.Find(type, code) is not { } scale)
            {
                unusable.Add(new(record, UnusableReason.UnknownScale));
                continue;
            }

            if (!record.Fields.TryGetValue(set.ScoreField, out var value) || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDecimal(out var score))
            {
                unusable.Add(new(record, UnusableReason.NoScore));
                continue;
            }

            if (score < scale.Min || score > scale.Max)
            {
                unusable.Add(new(record, UnusableReason.OutOfRange));
                continue;
            }

            // A case's records are dated: CaseReader leaves an undated record out of every case.
            scores.Add((scale, new ScaleScore(record, CaseReader.DayOf(record, fields.DatedField(type))!.Value, score)));
        }

        var byScale = scores
            .GroupBy(s => s.Scale.Code, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CaseScale(g.First().Scale, g.First().Score, g.Last().Score, g.Count()))
            .ToList();
        return new CaseScales(subjectCase, byScale, unusable);
    }

    // The code of a coded field holding one value ({ "scheme", "version", "code" }); null for none or several.
    private static string? CodeOf(Entity record, string field) =>
        record.Fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;
}
