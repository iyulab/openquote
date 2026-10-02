using System.Text.Json;
using Corvus.Text.Json.Validator;

namespace Openquote.Tests;

public class SchemaTests
{
    // Every reference is inside the schema itself and the standard metaschemas are built in, so nothing is fetched.
    private static readonly JsonSchema.Options Offline = new(allowFileSystemAndHttpResolution: false);

    private static JsonSchema Schema(string name) =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", $"{name}.schema.json")), options: Offline);

    private static bool Valid(string name, string json) => Schema(name).Validate(json);

    [Theory]
    [InlineData("vault", """{ "format": "openquote.vault/0", "encryption": "age" }""")]
    [InlineData("scheme", """{ "format": "openquote.scheme/0", "scheme": "kind", "version": 1, "effective": { "from": "2026-03-01" }, "items": [ { "code": "a", "label": "A", "suggest": true }, { "code": "a/b", "label": "B", "parent": "a" } ] }""")]
    [InlineData("crosswalk", """{ "format": "openquote.crosswalk/0", "scheme": "kind", "from": 1, "to": 2, "links": [ ["a", "x"] ] }""")]
    [InlineData("report", """{ "format": "openquote.report/0", "report": "monthly", "version": 1, "label": "Monthly", "counts": "session", "period": { "unit": "month", "field": "date" }, "rows": { "field": "kind", "scheme": "kind", "version": 1 }, "columns": { "field": "practitioner" } }""")]
    [InlineData("report", """{ "format": "openquote.report/1", "report": "monthly", "version": 1, "label": "Monthly", "counts": "session", "period": { "unit": "month", "field": "date" }, "rows": { "field": "kind", "scheme": "kind", "version": "in-force" } }""")]
    [InlineData("export", """{ "format": "openquote.export/0", "export": "list", "version": 1, "label": "List", "rows": "session", "period": { "field": "date" }, "columns": [ { "label": "Date", "field": "date" }, { "label": "Year", "year": "date", "startMonth": 3 }, { "label": "People", "people": "count" } ] }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "care.school", "version": 1, "label": "School", "depends": { "care": 1 }, "provides": [ "schemes/school-level/v1.json" ] }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "region-a", "version": 1, "locale": "fr", "schemes": { "care.method": { "1": { "interview": "Entretien" } } }, "fields": { "session": { "date": "Date" } } }""")]
    [InlineData("fields", """{ "format": "openquote.fields/0", "pack": "care", "type": "session", "version": 1, "fields": [ { "name": "date", "kind": "date", "required": true }, { "name": "note", "kind": "text", "tier": "narrative" }, { "name": "method", "kind": "coded", "scheme": "care.method" } ], "constrain": [] }""")]
    [InlineData("change", """{ "format": "openquote.change/0", "id": "0199a1d4-2b18-7e06-b3c7-8a5d4e2f1c90", "device": "desk01", "at": "2026-03-04T10:12:05+01:00", "entity": { "type": "session", "id": "0199a1d0-9c41-7f28-9d03-1b7e6a5c4d38" }, "op": "update", "base": [], "fields": { "date": "2026-03-04" } }""")]
    [InlineData("run", """{ "format": "openquote.run/0", "id": "0199b2e0-3a57-7012-8c64-4f1d2e3b5a71", "device": "desk01", "at": "2026-04-02T09:00:00+01:00", "report": { "report": "monthly", "version": 1 }, "schemes": { "kind": { "version": 1 } }, "period": { "from": "2026-03-01", "to": "2026-03-31" }, "cells": [], "pending": { "count": 0, "records": [] }, "unmapped": { "count": 0, "records": [] }, "total": { "count": 0, "records": [] } }""")]
    [InlineData("run", """{ "format": "openquote.run/0", "id": "0199b2e0-3a57-7012-8c64-4f1d2e3b5a71", "device": "desk01", "at": "2026-04-02T09:00:00+01:00", "report": { "report": "monthly", "version": 1 }, "schemes": { "kind": { "version": 1 } }, "period": { "from": "2026-03-01", "to": "2026-03-31" }, "cells": [], "pending": { "records": [] }, "unmapped": { "records": [ "r1" ] }, "blank": { "count": 1, "records": [ "r1" ] }, "total": { "records": [ "r1" ] } }""")]
    [InlineData("run", """{ "format": "openquote.run/1", "id": "0199b2e0-3a57-7012-8c64-4f1d2e3b5a71", "device": "desk01", "at": "2026-04-02T09:00:00+01:00", "report": { "report": "monthly", "version": 1 }, "schemes": { "kind": { "version": 1 } }, "period": { "from": "2026-03-01", "to": "2026-03-31" }, "cells": [], "pending": { "records": [] }, "unmapped": { "records": [] }, "blank": { "records": [ "r1" ] }, "conflicted": { "records": [ "r2" ] }, "total": { "records": [ "r1", "r2" ] } }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "Care", "provides": [ "schemes/topic/v1-v2.json", "reports/monthly/v1.json", "exports/list/v2.json", "labels/care/v1.en-US.json", "fields/care/session/v1.json" ] }""")]
    [InlineData("fields", """{ "format": "openquote.fields/0", "pack": "care.school", "type": "session", "version": 1, "constrain": [ { "name": "method", "hidden": true } ] }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "region-a", "version": 1, "locale": "fr", "aliases": { "subject": { "name": [ "nom" ] } }, "reports": { "monthly": { "1": "Mensuel" } }, "exports": { "list": { "1": { "label": "Liste", "columns": { "0": "Jour" } }, "2": { "columns": { "3": "Sujet" } } } } }""")]
    [InlineData("suggestions", """{ "format": "openquote.suggestions/0", "pack": "care.school", "version": 2, "schemes": { "topic": { "1": { "crisis": "confirm", "other": "off", "stress": "offer" } } } }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "care", "version": 2, "label": "Care", "provides": [ "suggestions/care/v2.json", "guidance/care/v2.json" ] }""")]
    public void The_documented_shape_is_accepted(string schema, string json) => Assert.True(Valid(schema, json));

    [Theory]
    [InlineData("scheme", """{ "format": "openquote.scheme/0", "scheme": "kind", "version": 0, "items": [] }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "Care", "version": 1, "label": "C", "provides": [] }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "schemes": { "k": { "1": { "a": "" } } } }""")]
    [InlineData("fields", """{ "format": "openquote.fields/0", "pack": "care", "type": "session", "version": 1, "fields": [ { "name": "m", "kind": "coded" } ] }""")]
    [InlineData("fields", """{ "format": "openquote.fields/0", "pack": "care", "type": "session", "version": 1, "constrain": [ { "name": "m", "required": false } ] }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "local", "version": 1, "label": "L", "provides": [] }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "C", "provides": [ "schemes/topic" ] }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "C", "provides": [ "packs/care/v1.json" ] }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "oq", "version": 1, "locale": "fr" }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "aliases": { "subject": { "name": [] } } }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "aliases": { "subject": { "name": [ "" ] } } }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "reports": { "monthly": { "01": "M" } } }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "exports": { "list": { "1": {} } } }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "exports": { "list": { "1": { "columns": {} } } } }""")]
    [InlineData("labels", """{ "format": "openquote.labels/0", "pack": "kr", "version": 1, "locale": "ko", "exports": { "list": { "1": { "columns": { "01": "A" } } } } }""")]
    [InlineData("fields", """{ "format": "openquote.fields/0", "pack": "local", "type": "session", "version": 1 }""")]
    [InlineData("export", """{ "format": "openquote.export/0", "export": "list", "version": 1, "label": "List", "rows": "session", "period": { "field": "date" }, "columns": [ { "label": "Topic", "field": "topic", "scheme": "topic" } ] }""")]
    [InlineData("export", """{ "format": "openquote.export/0", "export": "list", "version": 1, "label": "List", "rows": "session", "period": { "field": "date" }, "columns": [ { "label": "Year", "year": "date", "startMonth": 13 } ] }""")]
    [InlineData("run", """{ "format": "openquote.run/0", "id": "0199b2e0-3a57-7012-8c64-4f1d2e3b5a71", "device": "desk01", "at": "2026-04-02T09:00:00+01:00", "report": { "report": "monthly", "version": 1 }, "schemes": { "kind": { "version": 1 } }, "period": { "from": "2026-03-01", "to": "2026-03-31" }, "cells": [ { "row": "", "column": null, "records": [] } ], "pending": { "records": [] }, "unmapped": { "records": [] }, "total": { "records": [] } }""")]
    [InlineData("report", """{ "format": "openquote.report/0", "report": "monthly", "version": 1, "label": "Monthly", "counts": "session", "period": { "unit": "month", "field": "date" }, "rows": { "field": "kind", "scheme": "kind", "version": "in-force" } }""")]
    [InlineData("run", """{ "format": "openquote.run/1", "id": "0199b2e0-3a57-7012-8c64-4f1d2e3b5a71", "device": "desk01", "at": "2026-04-02T09:00:00+01:00", "report": { "report": "monthly", "version": 1 }, "schemes": { "kind": { "version": 1 } }, "period": { "from": "2026-03-01", "to": "2026-03-31" }, "cells": [], "pending": { "records": [] }, "unmapped": { "records": [] }, "blank": { "records": [ "r1" ] }, "total": { "records": [ "r1" ] } }""")]
    [InlineData("run", """{ "format": "openquote.run/0", "id": "0199b2e0-3a57-7012-8c64-4f1d2e3b5a71", "device": "desk01", "at": "2026-04-02T09:00:00+01:00", "report": { "report": "monthly", "version": 1 }, "schemes": { "kind": { "version": 1 } }, "period": { "from": "2026-03-01", "to": "2026-03-31" }, "cells": [], "pending": { "records": [] }, "unmapped": { "records": [] }, "conflicted": { "records": [ "r2" ] }, "total": { "records": [ "r2" ] } }""")]
    [InlineData("change", """{ "format": "openquote.change/0", "id": "0199A1D4-2B18-7E06-B3C7-8A5D4E2F1C90", "device": "desk01", "at": "2026-03-04T10:12:05+01:00", "entity": { "type": "session", "id": "x" }, "op": "update", "base": [], "fields": {} }""")]
    [InlineData("change", """{ "format": "openquote.change/0", "id": "0199a1d4-2b18-7e06-b3c7-8a5d4e2f1c90", "device": "desk01", "at": "2026-03-04T10:12:05Z", "entity": { "type": "session", "id": "x" }, "op": "update", "base": [], "fields": {} }""")]
    [InlineData("suggestions", """{ "format": "openquote.suggestions/0", "pack": "care", "version": 1, "schemes": { "topic": { "1": { "crisis": "always" } } } }""")]
    [InlineData("suggestions", """{ "format": "openquote.suggestions/0", "pack": "care", "version": 1, "schemes": { "topic": { "01": { "crisis": "off" } } } }""")]
    [InlineData("suggestions", """{ "format": "openquote.suggestions/0", "pack": "local", "version": 1 }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "C", "provides": [ "vault.json" ] }""")]
    [InlineData("pack", """{ "format": "openquote.pack/0", "pack": "care", "version": 1, "label": "C", "provides": [ "suggestions/care.json" ] }""")]
    public void A_shape_the_reader_refuses_is_refused(string schema, string json) => Assert.False(Valid(schema, json));

    // Every ```json block in the format document, with the schema its "format" names (openquote.<name>/0).
    private static IEnumerable<(string Schema, string Json)> Examples()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "format.md"));
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] != "```json") continue;
            var end = Array.IndexOf(lines, "```", i + 1);
            var json = string.Join('\n', lines[(i + 1)..end]);
            using var doc = JsonDocument.Parse(json);
            var format = doc.RootElement.GetProperty("format").GetString()!;
            yield return (format["openquote.".Length..format.IndexOf('/', StringComparison.Ordinal)], json);
            i = end;
        }
    }

    public static TheoryData<string, string> FormatExamples()
    {
        var data = new TheoryData<string, string>();
        foreach (var (schema, json) in Examples()) data.Add(schema, json);
        return data;
    }

    [Theory]
    [MemberData(nameof(FormatExamples))]
    public void Every_example_in_the_format_document_is_accepted(string schema, string json) => Assert.True(Valid(schema, json));

    [Fact]
    public void The_format_document_has_an_example_of_every_file_format()
    {
        var shown = Examples().Select(e => e.Schema).Distinct().Order(StringComparer.Ordinal);
        var schemas = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "schema"), "*.schema.json")
            .Select(p => Path.GetFileName(p)[..^".schema.json".Length])
            .Order(StringComparer.Ordinal);
        Assert.Equal(schemas, shown);
    }
}
