# Vault format, versions 0 and 1

This document specifies the plaintext layer of an Openquote vault: the folder layout, the JSON of each file kind, and the rules a reader applies. It describes what the engine in this repository reads and writes.

> Status: version 0 is frozen; version 1 is in progress. Version 0 changes only by additions: a reader that predates an addition may pass over what it adds, and counts the same numbers. Version 1 makes the changes to the structure together — what a file means or what it counts: blank and conflicted records apart (run records, format 1), report forms split by up to three dimensions and counting in the version in force (format 1), coded fields that take several values and fields that start from a fixed value (field definitions format 1), schemes that extend others (format 1), crosswalks that state relations or lead into another scheme (format 1), and export columns that fill an outside form's cells — a fixed text, a level of a hierarchy, a date in ISO 8601 basic format, a number divided, the value a group's subjects share (export forms format 1). A vault holding an extending scheme, such a crosswalk or a field that takes several values is declared `openquote.vault/1` (`VaultContent.RequiredVersion`); the others an earlier engine skips file by file without counting differently.

Every file kind has a JSON Schema (draft 2020-12) in [schema/](schema/), named after its format (`openquote.<name>/0` and `/1` are both `schema/<name>.schema.json`). A schema checks the shape of one file. Rules that compare a file with its path, with other files, or with other values in it — a name matching its path, codes unique within a version, a run's totals adding up — are checked by the reader only.

## Plaintext and encryption

The engine is encryption-neutral. It reads and produces plaintext files only; each file is a path relative to the vault root (with `/` separators) and its bytes.

A host application may store a vault encrypted, for example by wrapping each file with [age](https://age-encryption.org) file encryption. The host then decrypts every file before handing it to the engine and encrypts every new file before writing it. `vault.json` is always plaintext, because it has to be read before the vault is opened.

## Folder layout

```
<vault>/
  vault.json                                 declaration
  schemes/<scheme>/v<N>.json                 classification scheme, version N
  schemes/<scheme>/v<N>-v<M>.json            crosswalk from version N to version M
  reports/<report>/v<N>.json                 report form, version N
  exports/<export>/v<N>.json                 export form, version N
  packs/<pack>/v<N>.json                     pack manifest: a pack applied to this vault, version N
  labels/<pack>/v<N>.<locale>.json           labels a pack gives in one locale
  fields/<pack>/<type>/v<N>.json             fields a pack declares for an entity type
  suggestions/<pack>/v<N>.json               which scheme items a pack lets hosts suggest
  practitioners/<id>.<device>.json           change to a practitioner
  devices/<id>.<device>.json                 change to a device name
  subjects/<subject-id>/<id>.<device>.json   change to a subject or to an entity kept under it
  groups/<group-id>/<id>.<device>.json       change to a group or to an entity kept under it
  runs/<yyyy>/<id>.<device>.json             report run record
```

- Every file is written with create-new semantics: a write fails rather than replacing an existing file. No existing file is edited. The only removal is a destruction a person asks for (see [Destruction](#destruction)).
- Version numbers `N` and `M` are integers of 1 or more, without leading zeros.
- A path outside this layout is ignored, so sync-client temporary files and host-private files do no harm. A path inside it whose content cannot be used is reported as unreadable (see [Reading rules](#reading-rules)).

A subject's folder holds the subject and everything recorded about that subject alone, so handing a subject over means copying one folder. A record that concerns several subjects at once (for example a group session) belongs in a group's folder, never in a subject's folder.

## Identifiers

- **Change id**: a UUID in lowercase hyphenated form. The writer produces UUIDv7 (RFC 9562), whose leading 48 bits are a millisecond timestamp, so ordinal string order is time order. The reader accepts any lowercase hyphenated UUID.
- **Device id**: 4 to 16 lowercase ASCII letters or digits. It identifies the writing device and is local to that device; people see device *names* instead (see [Device names](#device-names)).
- **File name**: `<id>.<device>.json`. The device id in the name keeps two writers from ever choosing the same name.
- **Entity id**: the id of the change that created the entity.

## Declaration: `vault.json`

```json
{
  "format": "openquote.vault/0",
  "encryption": "age"
}
```

- `format` names the vault format: `openquote.vault/0` or `openquote.vault/1`. The engine reads only this key, and reads a vault by the rules of the version it declares (`VaultContent.DeclaredVersion`).
- `encryption` is written and read by the host (`"age"` or `"none"`). The engine ignores it.

When `vault.json` is among the files given to the reader, it is checked before anything else. If it names a version later than the engine reads (`openquote.vault/2` and up), names an unknown format, lacks `format`, or is not valid JSON, the whole read is refused with `VaultFormatException`. A host that does not pass `vault.json` to the reader must perform this check itself.

## Change files

```json
{
  "format": "openquote.change/0",
  "id": "0199a1d4-2b18-7e06-b3c7-8a5d4e2f1c90",
  "device": "desk01",
  "at": "2026-03-04T10:12:05+01:00",
  "entity": { "type": "session", "id": "0199a1d0-9c41-7f28-9d03-1b7e6a5c4d38" },
  "op": "update",
  "base": ["0199a1d0-9c41-7f28-9d03-1b7e6a5c4d38"],
  "fields": {
    "date": "2026-03-04",
    "topic": { "scheme": "topic", "version": 1, "code": "school/attendance" },
    "practitioner": "0199a1b0-1a02-7c44-8e21-3d9f0a1b2c03",
    "note": null
  },
  "source": { "topic": "suggestion" }
}
```

This file would live at `subjects/<subject-id>/0199a1d4-2b18-7e06-b3c7-8a5d4e2f1c90.desk01.json`.

| Key | Required | Meaning |
|---|---|---|
| `format` | yes | Exactly `openquote.change/0`. |
| `id` | yes | The change id. Must match the file name. |
| `device` | yes | The writing device. Must match the file name. |
| `at` | yes | When the change was written: `yyyy-MM-ddTHH:mm:ss±hh:mm`, whole seconds, numeric offset (`Z` is not accepted). Used for display and ordering of input, never to place a record in a reporting period. |
| `entity` | yes | `{ "type", "id" }`, both non-empty strings. |
| `op` | yes | `create`, `update`, `reclassify` or `destroy`. |
| `base` | yes | The ids of the changes to this entity the writer had seen (the heads at the time). Empty for `create`. |
| `fields` | yes | Only the fields this change sets. JSON `null` clears a field on purpose; an absent key leaves it untouched. |
| `source` | no | Per field, `manual` or `suggestion`. A field without an entry is `manual`. The writer only accepts entries for fields the change sets. |

### Entity types

The engine attaches no meaning to entity types in general; `type` is any non-empty string and `fields` hold whatever the host defines. Examples in this document (`session`, `case`, `topic`) are illustrations, not built-in concepts. The structural parts are:

- **Folders.** `subjects/<id>/` and `groups/<id>/` hold a subject or group and the entities recorded under it. `practitioners/` and `devices/` hold flat lists. An entity's subject or group is derived from the folder of its first change.
- **`attendees`.** An entity kept in a group's folder lists the subjects it concerns in the field `attendees` (an array of subject ids). An entity in a subject's folder concerns that subject. This is what head counts are based on.
- **`device` entities** (see below).

### Field values

- **Calendar dates** are `YYYY-MM-DD` strings without a time zone. Reports and exports place a record in a period by a calendar-date field.
- **Classified values** are `{ "scheme", "version", "code" }`: the scheme version in force when the value was entered, and a code of that version. The value is kept as entered; later versions are reached through crosswalks.
- A coded field that takes several values (`many`, field definitions format 1) holds a non-empty array of classified values, at most one with `"primary": true` — `[{ "scheme": "topic", "version": 1, "code": "school", "primary": true }, { "scheme": "topic", "version": 1, "code": "crisis" }]`. The array is one value of the field: changes merge it whole, and two devices writing different arrays conflict. A count places the record by the primary value, or by the only one; with several and none marked, the record waits for a person to say which comes first (`VaultWriter.Reclassify` marks it, and keeps every other value). An empty array is no value. A vault holding such a field declares format 1: an engine that reads only format 0 would leave every such record unmapped.
- **References** to other entities are entity ids as strings.

### Device names

A `device` entity, kept in `devices/`, carries a human-readable name in its `name` field. It names the device that wrote its `create` change; the device id is not repeated in the fields. If a device created several, the one with the highest entity id wins. Any device may rename it. A blank name means no name.

### Destruction

`op: destroy` records that a person destroyed the entity. It carries no field values. Once an entity has a destroy change it has no fields, takes no further changes, and is left out of reports and exports. Removing the entity's other change files is a host action; the engine never deletes anything.

## Classification schemes

```json
{
  "format": "openquote.scheme/0",
  "scheme": "topic",
  "version": 2,
  "effective": { "from": "2026-03-01" },
  "items": [
    { "code": "emotion", "label": "Emotion", "suggest": true },
    { "code": "school", "label": "School life" },
    { "code": "school/attendance", "label": "Attendance", "parent": "school" }
  ]
}
```

- Path: `schemes/<scheme>/v<version>.json`; `scheme` and `version` must match it.
- Every item has a `code` and a `label`. Codes are unique within a version. The code is the item's identity; the label is what people see.
- `parent`, if present, must be the code of another item in the same version. Writing a child code as `parent/child` is a naming convention; hierarchy comes from `parent`.
- `suggest` (default `false`) marks items a host may offer as suggestions. A pack can say otherwise without a new scheme version (see [Suggestions](#suggestions)).
- `effective` (optional): `from` (required) and `to` (optional, on or after `from`), calendar dates on which the body that issues the scheme puts this version in force. Both dates are inclusive: the version is in force on `from` and on `to`. A version without it is in force throughout. It guides which version a host offers for input (`SchemeCatalog.InForce`, the highest version in force on a given date); a report counts by it only when its form asks for the version in force (`"in-force"`, format 1) instead of naming one.
- `extends` (format 1, `openquote.scheme/1`): `{ "scheme", "version" }` — another scheme's version this one extends, such as a body's own list beside a shared one. Every item of an extending scheme then names its `anchor`, the item of the extended version it counts as. A report whose rows count the extended scheme counts a value of the extending one as its anchor, carried by the extended scheme's crosswalks like any of its values; a report whose rows count the extending scheme counts its own items. A value whose anchor the extended version does not hold is unmapped. A vault holding an extending scheme needs format 1 (`VaultContent.RequiredVersion`), since an earlier engine leaves its values uncounted.

## Crosswalks

```json
{
  "format": "openquote.crosswalk/0",
  "scheme": "topic",
  "from": 1,
  "to": 2,
  "links": [
    ["sadness", "emotion"],
    ["worry", "emotion"],
    ["attendance", "school/attendance"],
    ["relations", "peers"],
    ["relations", "family"]
  ]
}
```

- Path: `schemes/<scheme>/v<from>-v<to>.json`; `to` must be greater than `from`. A crosswalk into another scheme (format 1) names it in `into` and lives at `schemes/<scheme>/v<from>-<into>.v<to>.json`; its versions are those of the two schemes. A value of a scheme that crosswalks lead from to the rows of a report is carried along them — first from its own scheme, otherwise from the nearest scheme it extends — and the run names such a crosswalk as `<scheme>/<from>-<into>/<to>` among those applied.
- `links` are `[old code, new code]` pairs. Link types (1:1, N:1, 1:N, N:M) are not stored; they follow from the pairs.
- In `openquote.crosswalk/1`, a link may state how the old item relates to the new one as a third element: `equivalent` (the same meaning) and `narrower` (the new item holds all of the old one) carry a value without asking; `broader` (the new item holds only part of the old one) leaves it for a person to decide, even as the only link; `retired` records where a retired item went nearest and carries nothing. A link without a relation is carried as in format 0. A vault holding such a crosswalk needs format 1 (`VaultContent.RequiredVersion`): an earlier engine skips the file and leaves its values unmapped.
- There is no implicit identity crosswalk. A new version that only relabels items still needs a crosswalk linking each code to itself. A relabel that keeps every code can instead be given as labels (see [Labels](#labels)), which needs no new version.

How values are carried across versions is described in [Concepts](concepts.md#crosswalks).

## Report forms

```json
{
  "format": "openquote.report/0",
  "report": "monthly-topic",
  "version": 1,
  "label": "Sessions by topic",
  "counts": "session",
  "period": { "unit": "month", "field": "date" },
  "rows": { "field": "topic", "scheme": "topic", "version": 2 },
  "columns": { "field": "practitioner" }
}
```

- Path: `reports/<report>/v<version>.json`; `report` and `version` must match it.
- `counts`: the entity type counted. `period.unit` is `month` (format 1 adds others, below); `period.field` is the calendar-date field that places a record in the period.
- `rows`: the classified field and the scheme version the report counts in.
- `columns` (optional): a field whose string value splits the columns. Without it, or when a record has no string value there, the column is `null`.

In `openquote.report/1`, a form splits what it counts by one to three `dimensions` instead of rows and columns:

```json
{
  "format": "openquote.report/1",
  "report": "monthly-topic-level",
  "version": 1,
  "label": "Sessions by topic and school level",
  "counts": "session",
  "period": { "unit": "month", "field": "date" },
  "dimensions": [
    { "field": "topic", "scheme": "topic", "version": "in-force" },
    { "field": "level", "scheme": "school-level", "version": 1, "of": "subject" },
    { "field": "practitioner" }
  ],
  "filters": [
    { "field": "gender", "of": "subject", "in": ["f"] }
  ],
  "measures": ["records", "people", "visits", { "sum": "minutes" }]
}
```

- A dimension with a `scheme` and a `version` counts the field's classified values in that scheme version; `version` may be `"in-force"`: a run counts in the version of the scheme in force on the last day of its period (see a scheme's `effective`), so a form follows a revision without being written again. The run record names the version it counted in.
- A dimension with only a `field` splits by the field's string value, `null` when a record has none.
- A classified dimension of the record counts a field holding several values by its primary value (`"values": "primary"`, the default), or with `"values": "all"` by every one of them: a record is then in each cell its values lead to (once per cell), and in the set of a value that cannot be placed as well, so the cells add up to more than the records. Only a classified dimension of the record may count every value; a filter reads the primary value.
- With `"of": "subject"`, a dimension reads the field of the subjects a record is about rather than of the record. A record about one subject takes that subject's value — pending, unmapped, blank or conflicted as the record's own field would be. A record about several subjects takes their value when they all have the same one; otherwise, and when it is about none, its place is `null`: no single value.
- `filters` (optional): each is read as a dimension is, with `in` listing the codes or string values it lets through. A record whose value there is not listed — or is `null` — is not in the run at all. A record a filter cannot place yet (pending, unmapped, blank or conflicted there) is kept and listed with those records, so a filter never drops a record silently.
- A form counts each scheme in one version: two dimensions or filters of the same scheme name the same version.
- `period.unit` may also be `day`, `year` or `range`. A `year` starts on the first day of `startMonth` (1–12, default 1) — 3 for a school year from March; only a year has a `startMonth`. A `range` is any stretch of days a person picks. The unit is how people ask for the form (`ReportPeriod.Containing`, `ReportRunner.RunContaining`); a run still records its first and last day, and any form may be run over any days.
- `measures` (optional, default `["records", "people"]`): the numbers the form shows, each once — `records` (each record once), `people` (distinct subjects, a head count) and `visits` (for each record, the number of subjects it is about, added up). All three come from a run's records and `people`; a run record is the same whichever of them the form shows. A measure may also be `{ "sum": "<field>" }`: the numbers the counted records hold in that field, added up for each cell, set and total — the length of a session in minutes, say. A record holding no number there (an empty field, a value that is not a number, or two devices' different values) adds nothing, and the run says how many such records each number leaves out rather than counting them as zero (`ReportRun.SumOf`). Each record's number comes from the run record's `values`, so the sum goes back to its records as a count does.
- `counts` may name any entity type, `subject` included: a form counting subjects by a date of their own (such as when they registered) counts each subject once, about itself.
- A format 1 form never has `rows` or `columns`; a form that rows and a column describe is the same form either way, and its runs are the same.

## Run records

```json
{
  "format": "openquote.run/0",
  "id": "0199b2e0-3a57-7012-8c64-4f1d2e3b5a71",
  "device": "desk01",
  "at": "2026-04-02T09:00:00+01:00",
  "report": { "report": "monthly-topic", "version": 1 },
  "schemes": { "topic": { "version": 2, "crosswalks": ["1-2"] } },
  "period": { "from": "2026-03-01", "to": "2026-03-31" },
  "cells": [
    { "row": "school/attendance", "column": "0199a1b0-1a02-7c44-8e21-3d9f0a1b2c03",
      "count": 1, "records": ["0199a1d0-9c41-7f28-9d03-1b7e6a5c4d38"] }
  ],
  "pending": { "count": 1, "records": ["0199a1e2-5d60-7a19-8b4e-2f7c3d1a9e05"] },
  "unmapped": { "count": 0, "records": [] },
  "total": { "count": 2, "records": ["0199a1d0-9c41-7f28-9d03-1b7e6a5c4d38", "0199a1e2-5d60-7a19-8b4e-2f7c3d1a9e05"] },
  "people": {
    "0199a1d0-9c41-7f28-9d03-1b7e6a5c4d38": ["0199a1c0-4e21-7b3a-9c10-5f2e8d7a6b01"],
    "0199a1e2-5d60-7a19-8b4e-2f7c3d1a9e05": ["0199a1c0-4e21-7b3a-9c10-5f2e8d7a6b01"]
  }
}
```

- Path: `runs/<yyyy>/<id>.<device>.json`, where `yyyy` is the year of `at`. `id` and `device` must match the name.
- `report` names the form and version run; that form must be in the vault.
- `schemes` records, for each scheme the form counts in, the version counted in and the crosswalks applied (as `from-to`, or `<scheme>/<from>-<into>/<to>` across schemes; omitted when none were). For a form that counts in the version in force, `boundaries` (omitted when empty) lists each day within the period on which the version in force changes, with the version before and from that day (`null` when none was in force): counts on either side were entered under different versions.
- `period` gives the first and last calendar day, inclusive.
- Each cell names its `row` code, its `column` (a string or `null`) and the `records` counted in it. Cells with no records are not written.
- In `openquote.run/1`, each cell names its `key` instead: one place per dimension of the form, in its order — a code where the dimension is classified, a string or `null` where it splits by a string value, and `null` where the subjects a record is about have no single value (`"key": ["school/attendance", null]`). A run whose form only has rows and a column of the record, no filter, and no blank or conflicted records, is written in format 0, which an engine that predates format 1 reads; any other run is written in format 1.
- `pending`, `unmapped`, `blank` and `conflicted` list records not placed in any cell: a value waiting for a person to choose among codes, a value with no code in the form's version, no value to count, and concurrent values in a field the form places by (its period field or a dimension's field). A record that several classified dimensions leave out of the cells is listed once: as pending when any of them waits for a person, otherwise as unmapped when any has no code, otherwise as blank. `total` lists every record in the period and must equal the cells plus those sets, each record in exactly one of them (see `multiple` below for a form that counts every value).
- `blank` and `conflicted` are written only in `openquote.run/1`, and both always are there.
- `multiple` (`openquote.run/1`, present and `true` exactly when the form counts every value of a field): a record may be in several cells, once in each, and in at most one of `pending`, `unmapped`, `blank` and `conflicted` besides; `total` lists every record once. Without it, each record is in exactly one place. A format 0 record may still carry `blank` as a part of `unmapped`, as earlier engines wrote it; a reader takes those records out of `unmapped`.
- `count` keys are written for readability; readers derive every count from `records`.
- `people` (optional) maps each record in `total` to the subject ids it concerns. Its keys must be exactly the records in `total`. A run record without `people` has an unknown head count, not zero.
- `values` (`openquote.run/1`, present exactly when the form adds a field up): for each field the form adds up, the number each record in `total` holds there — `{ "minutes": { "<record>": 50 } }`. A record holding no number there is not listed. A run whose form adds a field up is written in format 1, since an earlier engine would drop the numbers.
- A period with no records still produces a run record, with empty `cells` and an empty `total`.

## Export forms

```json
{
  "format": "openquote.export/0",
  "export": "session-list",
  "version": 1,
  "label": "Session list",
  "rows": "session",
  "period": { "field": "date" },
  "columns": [
    { "label": "Date", "field": "date" },
    { "label": "Year", "year": "date", "startMonth": 9 },
    { "label": "Topic area", "field": "topic", "scheme": "topic", "version": 2, "part": "top" },
    { "label": "People", "people": "count" },
    { "label": "Subject", "person": "name", "all": true },
    { "label": "Practitioner", "field": "practitioner", "ref": "name" }
  ]
}
```

- Path: `exports/<export>/v<version>.json`; `export` and `version` must match it.
- `rows`: the entity type listed, one row per entity. `period.field`: the calendar-date field that places it in the period; a record whose dates were set without seeing each other is listed in each period one of them falls in.
- A cell over a field holding values set without seeing each other — of the record, of an entity it refers to, or of its subjects — is left empty, and the record is listed apart (`ExportTable.Conflicted`) beside those whose classified cell waits for a person or has no code.
- `columns`: a non-empty array. Each column has a `label` and exactly one source, recognised in this order:

| Keys | Cell |
|---|---|
| `people: "count"` | The number of distinct subjects the record concerns. |
| `person` (+ `all`) | That field of the subject the record concerns; empty when it concerns several. With `all: true`, every subject's value, joined by `", "`. |
| `year` + `startMonth` | The year the date field falls in, for a year starting in month `startMonth` (1–12). |
| `field` + `ref` | Field `ref` of the entity that `field` refers to. |
| `field` + `scheme` + `version` (+ `part`) | The label of the classified value carried to that version, as a report carries it (a value of an extending scheme by its anchor, of another scheme along its crosswalks, several values by the primary one); with `part: "top"` the label of its top-level ancestor (`part` is `top` or `item`, default `item`). |
| `field` | The field value as written. |

In `openquote.export/1`, a form fills the cells an outside form or system asks for, in the shape it asks for them:

```json
{
  "format": "openquote.export/1",
  "export": "upload",
  "version": 1,
  "label": "Upload",
  "rows": "session",
  "period": { "field": "date" },
  "columns": [
    { "label": "Kind", "value": "Counselling" },
    { "label": "Area", "field": "topic", "scheme": "topic", "version": 2, "level": 1 },
    { "label": "Group", "field": "topic", "scheme": "topic", "version": 2, "level": 2 },
    { "label": "Topic", "field": "topic", "scheme": "topic", "version": 2 },
    { "label": "Date", "field": "date", "date": "basic" },
    { "label": "Hours", "field": "minutes", "quotient": 60 },
    { "label": "Minutes", "field": "minutes", "remainder": 60 },
    { "label": "Gender", "person": "gender", "mixed": "Mixed" }
  ]
}
```

| Keys | Cell |
|---|---|
| `value` | The same text in every row, as the outside form fixes it; it may be empty. Recognised before every other source. |
| `person` + `mixed` | For a record about one subject, that subject's value. For a record about several, their value when they all have the same one, and the text `mixed` when they differ — as a report reads a field of the subjects (`"of": "subject"`). Not with `all`. |
| `field` + `scheme` + `version` + `level` | The label of the classified value's ancestor at that level, counted from 1 at the top; a value no deeper than the level shows its own label. `level: 1` is `part: "top"`; a column has one or the other. |
| `field` + `scheme` + `version` + `values: "all"` | For a field holding several values, every one of them rather than the primary one (`values: "primary"`, the default): their labels in the order the record holds them, the primary one first, each once, joined by `, `. A value that waits for a person or has no code in the version is left out of the cell and the record is listed apart, as a single value would be; the others still show. With `level` or `part`, each value's label at that level. |
| `field` + `date` | A calendar-date field written in ISO 8601 `basic` format (`20260310`) or `extended` format (`2026-03-10`, as the vault writes dates). Empty when the field holds no date. |
| `field` + `quotient` or `remainder` | A whole-number field divided by a whole number from 1 up: the quotient rounded down, or the remainder, from 0 up to the divisor — minutes as hours and minutes, for example. Empty when the field holds no whole number. A column has one of the two. |

A format 0 form whose column uses any of these keys is unreadable rather than read without them, and so is a column with a key its source does not take (`level` without a scheme, `mixed` with `all`). An engine that reads only format 0 lists a format 1 form as a format it does not know. Export forms count nothing, so a vault that holds one keeps its declared format.

## Packs

A pack is a bundle of definition files (schemes, crosswalks, forms, labels and field definitions) that one party maintains and many vaults apply. Applying a pack copies its files into the vault; from then on the vault holds them like any other definition. See [Concepts](concepts.md#packs-and-layers).

```json
{
  "format": "openquote.pack/0",
  "pack": "care.school",
  "version": 1,
  "label": "School counseling",
  "depends": { "care": 1 },
  "provides": [ "schemes/school-level/v1.json" ]
}
```

- Path: `packs/<pack>/v<version>.json`; `pack` and `version` must match it.
- `pack` is lowercase ASCII letters and digits in words joined by `.` or `-` (for example `care`, `care.school`, `org-x.y2`). `local` and `oq` are reserved for the vault itself and cannot name a pack. Writing a pack's own names as `<pack>.<name>` is recommended, not required.
- `version` is an integer of 1 or more. `label` is required: text a person reads.
- `depends` (optional) maps the ids of other packs to a minimum version, an integer of 1 or more. A pack never depends on itself. A pack only adds, so a later version holds everything an earlier one did and a minimum version is all a dependency needs.
- `provides` lists the definition files this version added, as vault paths: schemes, crosswalks, report and export forms, labels, field definitions and suggestion files. It is required, and may be empty. It is what says which pack owns which definition. A path to a JSON file in a folder outside the layout this engine knows names a kind of definition a later engine reads: the manifest is read without that line, as such a file in the vault is ignored. Any other path that is no definition file makes the manifest unreadable.
- A manifest in the vault is the record that the pack was applied; there is no separate record. Of several versions of a pack, the highest one counts.

`VaultContent.CheckPacks` compares the packs with each other and with the definition files the vault could read, and reports each problem with the pack it concerns (`PackIssue`):

| Kind | When |
|---|---|
| `MissingDependency` | A pack builds on a pack the vault does not hold. |
| `OlderDependency` | A pack needs a later version of a pack than the vault holds. |
| `MissingFile` | A manifest lists a definition file the vault does not hold. |
| `FileNotRead` | A manifest lists a definition file the vault holds but could not read — often one in a format a later engine reads; why is in `VaultContent.Unreadable`. A host can say the app needs updating rather than that a file is missing. |
| `SharedFile` | More than one pack lists the same definition file. |
| `DependencyCycle` | A pack builds on itself through other packs. |

Reading never stops on these; a host decides what to do with the list.

## Labels

```json
{
  "format": "openquote.labels/0",
  "pack": "region-a",
  "version": 1,
  "locale": "fr",
  "schemes": { "care.topic": { "1": { "sleep": "Sommeil", "school": "École" } } },
  "fields": { "session": { "date": "Date" }, "subject": { "name": "Nom" } },
  "aliases": { "subject": { "name": [ "Nom de famille", "Élève" ] } },
  "reports": { "monthly": { "1": "Séances par thème" } },
  "exports": { "session-list": { "1": { "label": "Liste des séances", "columns": { "0": "Jour", "2": "Thème" } } } }
}
```

- Path: `labels/<pack>/v<version>.<locale>.json`; `pack`, `version` and `locale` must match it. `pack` is a pack id as for a manifest (and not `local` or `oq`), and `locale` is a language tag such as `fr` or `en-US`; a file whose pack or locale cannot be valid is reported as unreadable.
- `schemes` (optional) maps a scheme name to a scheme version to item codes to labels. `fields` (optional) maps an entity type to field names to labels. Every label is non-empty text.
- `reports` (optional) maps a report form's name to a form version to its label. `exports` (optional) maps an export form's name to a form version to an object with a `label`, `columns`, or both; `columns` maps a column's position in the form, counted from 0, to its heading. A form version never changes, so neither do its column positions. Columns not listed keep the form's own heading.
- `aliases` (optional) maps an entity type to field names to a non-empty list of other names the field goes by — headings a person's own table may use for it. They do not label the field; a host matches them when it takes data in (`LabelCatalog.FieldAliases`). Aliases never conflict: for the first locale asked for where any pack gives some, every pack's aliases are combined.
- Labels change what people read, never a code or what is counted, so a renamed item needs no new scheme version.
- When several packs label the same thing in one locale, a pack that another of them builds on is set aside, so the pack that builds on the others wins. If several packs are left, none building on another, they agree when they give the same text (for example two packs that each build on a third and relabel alike); when they give different labels it is a conflict (`LabelCatalog.Conflicts`), and none of their labels is used for that locale.
- A host asks for labels by a list of locales in order of preference (`LabelCatalog.SchemeLabel`, `FieldLabel`, `ReportLabel`, `ExportLabel`, `ExportColumnLabel`). Each locale falls back to its language alone (`fr-CA`, then `fr`), and tags are compared without regard to case; if no locale gives a label, the answer is `null` and the host shows the definition's own `label` (the scheme item's, the field's, the form's or the column's). Of several versions of a pack's labels in one locale, the highest counts.

## Suggestions

```json
{
  "format": "openquote.suggestions/0",
  "pack": "care.school",
  "version": 2,
  "schemes": { "topic": { "1": { "crisis": "confirm", "self-harm": "confirm", "other": "off" } } }
}
```

- Path: `suggestions/<pack>/v<version>.json`; `pack` and `version` must match it. `pack` is a pack id as for a manifest (and not `local` or `oq`).
- `schemes` (optional) maps a scheme name to a scheme version to item codes to one of:
  - `off` — a host never offers the item as a suggestion;
  - `offer` — a host may offer it;
  - `confirm` — a host may offer it set apart from the other suggestions, and never fills it in until a person confirms it.
- An item no pack says anything about is offered when its scheme marks it `suggest`, and never otherwise. Whether an item is suggested never changes a code or what is counted, so a pack changes it with a later version of its own, not a new scheme version: of several versions of a pack's suggestion file, the highest counts.
- When several packs say something about the same item, a pack that another of them builds on is set aside, as for labels. If the packs left say the same, that holds; if they disagree it is a conflict (`SuggestionCatalog.Conflicts`), and the scheme's own `suggest` holds for that item.
- A host asks per item (`SuggestionCatalog.For`).

## Field definitions

```json
{
  "format": "openquote.fields/0",
  "pack": "care",
  "type": "session",
  "version": 1,
  "fields": [
    { "name": "date", "kind": "date", "required": true, "label": "Date" },
    { "name": "method", "kind": "coded", "scheme": "care.method" },
    { "name": "practitioner", "kind": "reference", "type": "practitioner" },
    { "name": "note", "kind": "text", "tier": "narrative" }
  ]
}
```

A second pack that builds on the first can add fields and narrow the first pack's:

```json
{
  "format": "openquote.fields/0",
  "pack": "care.school",
  "type": "session",
  "version": 1,
  "fields": [ { "name": "grade", "kind": "text", "default": { "subject": "grade" } } ],
  "constrain": [ { "name": "method", "required": true } ]
}
```

Format 1 (`openquote.fields/1`) also lets a field start from a fixed value — most sessions are with the client, and last fifty minutes:

```json
{
  "format": "openquote.fields/1",
  "pack": "care.school",
  "type": "session",
  "version": 2,
  "fields": [
    { "name": "with", "kind": "coded", "scheme": "with", "default": { "value": "client" } },
    { "name": "minutes", "kind": "number", "default": { "value": 50 } }
  ]
}
```

Format 1 also says how a value taken from the subject ages — a school grade counts up each school year, up to the last grade of the subject's school level, and a class does not carry over:

```json
{
  "format": "openquote.fields/1",
  "pack": "care.school",
  "type": "session",
  "version": 3,
  "fields": [
    {
      "name": "grade", "kind": "text",
      "default": { "subject": "grade",
        "year": { "startMonth": 3, "then": "advance", "max": { "subject": "level", "by": { "elementary": 6, "middle": 3, "high": 3 } } } }
    },
    { "name": "class", "kind": "text", "default": { "subject": "class", "year": { "startMonth": 3, "then": "drop" } } }
  ]
}
```

- Path: `fields/<pack>/<type>/v<version>.json`; `pack`, `type` and `version` must match it. `pack` is a pack id as for a manifest (and not `local` or `oq`), and `type` is the entity type the fields belong to.
- `fields` (optional) declares fields. A field has a `name`, unique within the file, and a `kind`, one of `text`, `date`, `number`, `coded`, `reference` and `references`.
- A `coded` field names the `scheme` its values are classified in, and no other kind has one. A `reference` or `references` field names the entity `type` it refers to, and no other kind has one.
- `tier` is `structured` (the default) or `narrative`. A narrative field is written content: `ExportRunner.Run` leaves every column that would carry it empty — its text, a year taken from it, the label of its code — and names the column in `ExportTable.Withheld`, which lists the columns whose cells were withheld in the rows produced (a period with no rows names none). Callers pass `content.FieldCatalog()` (required; `FieldCatalog.Empty` for a vault without field definitions).
- `required` (default `false`) says a value must be entered.
- `many` (format 1, `openquote.fields/1`, coded fields only; default `false`) says the field takes several values, one of them primary (see [Change files](#change-files)). A host asks for the primary value whenever more than one is entered.
- `default` (optional) is the value a host offers when the record is written, from one source: `{ "subject": "<field>" }`, the value of that field of the record's subject; or, in format 1, `{ "value": … }`, a fixed value — a non-empty string for a `text` field, a number for a `number` field, a code for a `coded` field (offered only when the version of its scheme in force that day holds the code). A `date` or reference field has no fixed value. Either way the record keeps the value as entered, and a host offers nothing for a hidden field.
- `year` (format 1, beside `subject` only) says how long a value taken from the subject holds. A year starts on the first day of `startMonth` (1–12, default 1). Once a year start has passed since the subject's value was written (by the clock of the device that wrote it), the value is *stale* for the record: with `then: "advance"` a whole number counts up by one for each year passed, and with `then: "drop"` nothing is offered. `max` (advance only) bounds the count — a number, or `{ "subject": "<field>", "by": { "<code>": n } }`, the bound for each code of another field of the subject; a value that would pass its bound is not offered. When that field has no value the bound is not known, and a count is offered only if it stays within every bound the current value is within. `SubjectDefaults.Carry` gives the value, when it was written, the years passed and what to offer; a host shows a stale value to a person, who corrects the subject, rather than writing a value nobody entered. An engine that predates `year` ignores it and offers the subject's value as it stands.
- `label` (optional) is what people read for the field; [Labels](#labels) can give it per locale.
- `constrain` (optional) narrows fields other packs declared, by `name`: `required` and `hidden` may each be set to `true`, and at least one must be. A key set to `false` is invalid, because a constraint only narrows. A pack does not constrain its own fields; it declares them as they should be.

`VaultContent.FieldCatalog` merges the field files of the vault's packs, each pack at its highest version, packs in the order they build on each other (a pack is placed as soon as the packs it builds on are placed, taking ids in order; packs without a manifest come last). A field is kept from the first pack that declares it; then each constraint is applied. `FieldCatalog.Issues` lists what does not fit (`FieldIssue`):

| Kind | When |
|---|---|
| `DuplicateField` | Two packs declare a field of the same name for one entity type; the earlier pack's declaration stands. |
| `ConstraintWithoutField` | A constraint names a field no pack declares. |
| `ConstraintFromUnrelatedPack` | A constraint narrows a field of a pack it does not build on; it is not applied. |
| `HiddenRequired` | A field ends up both required and hidden. |

## Reading rules

The reader never stops on a bad file. A file whose path matches the layout but cannot be used is listed as unreadable with a reason and what it was for — a subject's or group's records, a scheme version, a crosswalk, a form, a pack, labels, field definitions, suggestions or a run record, read from the path alone (`VaultFileKind.Of`, which also reads a sync client's copy by the start of its name) — and every other file is still read:

| Reason | When |
|---|---|
| `Malformed` | Not valid JSON, including a file cut off while being written. |
| `UnknownFormat` | `format` is missing or is not the format this engine reads for that path. |
| `Invalid` | A required key is missing or has the wrong type or value; a run record whose report form is not in the vault or whose totals do not add up. |
| `NameMismatch` | The file name or path disagrees with the id, device, name or version inside. |
| `DuplicateId` | Two change files carry the same id with different content. |

A sync client's conflicted copy of a change file is read as a valid input: its name may add, before or after `.json`, anything that begins with a character other than a letter or digit (for example `<id>.<device> (conflicted copy).json` or `<id>.<device>.json-LAPTOP`). Copies with identical content count once. A copy of any other vault file — a scheme, crosswalk, form, pack manifest, label file, field file, suggestion file or run record named with something added after `.json` — is listed as `NameMismatch` rather than ignored, since it may differ from the file it copies and should be looked at by a person.

The only condition that refuses the whole read is the declaration check described under [Declaration](#declaration-vaultjson).

## Compatibility

- A change a previous engine could ignore without producing a wrong number (a new optional key) is additive and keeps the declared version.
- A change that would make a previous engine count wrongly without noticing (a new folder of records that reports count, a key that changes what is counted) raises the declared vault format, so that previous engines refuse the vault instead of reading it. Folders a previous engine ignores without changing any count — `packs/`, `labels/`, `fields/`, `suggestions/` — are additive.
- A new folder of records that no report counts (for example appointments kept for scheduling) is additive too: a previous engine skips paths outside the layout above, and every count stays the same. The format version whose report forms first count records in such a folder raises the declared vault format, since a previous engine would run those reports without the records and give a smaller number.
- Version 1 file formats are new format versions of their file kinds (`openquote.scheme/1`, `openquote.crosswalk/1`, `openquote.report/1`, `openquote.run/1`, `openquote.fields/1`, `openquote.export/1`), so an engine that reads only version 0 reports each such file as a format it does not know rather than misreading it. What it would then count differently — values of an extending scheme, values a crosswalk it skipped would have carried — is why a vault holding them declares version 1: an earlier engine refuses the vault before counting anything. A version 1 engine reads a version 0 vault as it is, and counts blank and conflicted records apart in it too.
- A pack manifest may name a definition file in a folder a previous engine does not know; that engine reads the manifest without the line. Engines before `suggestions/` was added (package versions up to 0.6) read a manifest that names a suggestion file as unreadable instead, and keep using the pack's earlier version.

`VaultWriter` produces indented UTF-8 JSON with `\n` line endings and a trailing newline, stamps `at` in the device's local time with its offset, and gives every file a fresh id, so its path never names an existing file.
