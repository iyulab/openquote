using Openquote.Classification;
using Openquote.Reports;

namespace Openquote.Tests;

/// <summary>
/// Forms with a classified row dimension and an optional string column dimension — the shape most
/// tests count in — and views of their cells and runs in those terms.
/// </summary>
internal static class TestReports
{
    public static ReportDefinition Form(string name, int version, string label, string counts, string periodField,
        string rowField, string rowScheme, int? rowVersion, string? columnField) =>
        new(name, version, label, counts, periodField,
            columnField is null
                ? [new ReportDimension(rowField, rowScheme, rowVersion)]
                : [new ReportDimension(rowField, rowScheme, rowVersion), new ReportDimension(columnField)]);

    public static ReportCell Cell(string row, string? column, IReadOnlyList<string> records) => new([row, column], records);

    public static ReportCell Cell(string row, IReadOnlyList<string> records) => new([row], records);

    extension(ReportCell cell)
    {
        public string Row => cell.Key[0]!;
        public string? Column => cell.Key.Count > 1 ? cell.Key[1] : null;
    }

    extension(ReportDefinition form)
    {
        public int? RowVersion => form.Dimensions[0].Version;

        public ReportDefinition WithRowVersion(int? version) =>
            form with { Dimensions = [form.Dimensions[0] with { Version = version }, .. form.Dimensions.Skip(1)] };
    }

    extension(ReportRun run)
    {
        public IReadOnlyList<string> Crosswalks => [.. run.Schemes.SelectMany(s => s.Crosswalks)];
        public IReadOnlyList<SchemeBoundary> Boundaries => [.. run.Schemes.SelectMany(s => s.Boundaries)];
    }
}
