using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Openquote.Reports;

/// <summary>
/// Writes a <see cref="ReportRun"/> as a run record file: <c>openquote.run/1</c> when it has blank or
/// conflicted records, which only that format lists apart, and <c>openquote.run/0</c> otherwise, which
/// an engine that predates them reads with the same total.
/// </summary>
public static class ReportRunJson
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The run record for <paramref name="run"/>, written by <paramref name="device"/> at
    /// <paramref name="at"/> under change id <paramref name="id"/>. Once written, a run record is
    /// never edited: a later run of the same report is a new record.
    /// </summary>
    public static byte[] Write(ReportRun run, string id, string device, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(run);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            var sets = run.Blank.Count > 0 || run.Conflicted.Count > 0;
            w.WriteString("format", sets ? "openquote.run/1" : "openquote.run/0");
            w.WriteString("id", id);
            w.WriteString("device", device);
            w.WriteString("at", at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));

            w.WriteStartObject("report");
            w.WriteString("report", run.Report.Name);
            w.WriteNumber("version", run.Report.Version);
            w.WriteEndObject();

            w.WriteStartObject("schemes");
            w.WriteStartObject(run.Report.RowScheme);
            w.WriteNumber("version", RowVersion(run));
            if (run.Crosswalks.Count > 0)
            {
                w.WriteStartArray("crosswalks");
                foreach (var c in run.Crosswalks) w.WriteStringValue(c);
                w.WriteEndArray();
            }
            if (run.Boundaries.Count > 0)
            {
                w.WriteStartArray("boundaries");
                foreach (var b in run.Boundaries)
                {
                    w.WriteStartObject();
                    w.WriteString("date", b.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (b.From is { } before) w.WriteNumber("from", before);
                    else w.WriteNull("from");
                    if (b.To is { } after) w.WriteNumber("to", after);
                    else w.WriteNull("to");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("period");
            w.WriteString("from", run.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteString("to", run.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteEndObject();

            w.WriteStartArray("cells");
            foreach (var cell in run.Cells)
            {
                w.WriteStartObject();
                w.WriteString("row", cell.Row);
                if (cell.Column is null) w.WriteNull("column");
                else w.WriteString("column", cell.Column);
                w.WriteNumber("count", cell.Count);
                WriteIds(w, "records", cell.Records);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            WriteSet(w, "pending", run.Pending);
            WriteSet(w, "unmapped", run.Unmapped);
            if (sets)
            {
                WriteSet(w, "blank", run.Blank);
                WriteSet(w, "conflicted", run.Conflicted);
            }
            WriteSet(w, "total", run.Total);

            if (run.People is { } people)
            {
                w.WriteStartObject("people");
                foreach (var record in run.Total)
                    WriteIds(w, record, people.TryGetValue(record, out var subjects) ? subjects : []);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static int RowVersion(ReportRun run) =>
        run.Report.RowVersion ?? throw new ArgumentException("a run counts in a scheme version its form names or a run chose", nameof(run));

    private static void WriteSet(Utf8JsonWriter w, string name, IReadOnlyList<string> ids)
    {
        w.WriteStartObject(name);
        w.WriteNumber("count", ids.Count);
        WriteIds(w, "records", ids);
        w.WriteEndObject();
    }

    private static void WriteIds(Utf8JsonWriter w, string name, IReadOnlyList<string> ids)
    {
        w.WriteStartArray(name);
        foreach (var id in ids) w.WriteStringValue(id);
        w.WriteEndArray();
    }
}
