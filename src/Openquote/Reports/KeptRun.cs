namespace Openquote.Reports;

/// <summary>
/// A run record kept in a vault: the run as it was produced, and who produced it when. Two kept
/// runs of the same form and period explain, through <see cref="ReportDiff.Compare"/>, why their
/// numbers differ.
/// </summary>
/// <param name="Id">The record's change id.</param>
/// <param name="Device">The device that produced it.</param>
/// <param name="At">When it was produced.</param>
/// <param name="Path">Its path in the vault.</param>
/// <param name="Run">The run it records.</param>
public sealed record KeptRun(string Id, string Device, DateTimeOffset At, string Path, ReportRun Run);
