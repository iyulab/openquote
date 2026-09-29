# Vault format, version 0

This document specifies the plaintext layer of an Openquote vault: the folder layout, the JSON of each file kind, and the rules a reader applies. It describes what the engine in this repository reads and writes.

> Status: version 0 is provisional. It may still change before the first release.

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

- `format` names the vault format. The engine reads only this key.
- `encryption` is written and read by the host (`"age"` or `"none"`). The engine ignores it.

When `vault.json` is among the files given to the reader, it is checked before anything else. If it names a later version (`openquote.vault/1` and up), names an unknown format, lacks `format`, or is not valid JSON, the whole read is refused with `VaultFormatException`. A host that does not pass `vault.json` to the reader must perform this check itself.

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
- `suggest` (default `false`) marks items a host may offer as suggestions.

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

- Path: `schemes/<scheme>/v<from>-v<to>.json`; `to` must be greater than `from`.
- `links` are `[old code, new code]` pairs. Link types (1:1, N:1, 1:N, N:M) are not stored; they follow from the pairs.
- There is no implicit identity crosswalk. A new version that only relabels items still needs a crosswalk linking each code to itself.

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
- `counts`: the entity type counted. `period.unit` is `month`; `period.field` is the calendar-date field that places a record in the period.
- `rows`: the classified field and the scheme version the report counts in.
- `columns` (optional): a field whose string value splits the columns. Without it, or when a record has no string value there, the column is `null`.

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
- `schemes` records, for the row scheme, the version counted in and the crosswalks applied (as `from-to`; omitted when none were).
- `period` gives the first and last calendar day, inclusive.
- Each cell names its `row` code, its `column` (a string or `null`) and the `records` counted in it. Cells with no records are not written.
- `pending` and `unmapped` list records not placed in any cell. `total` lists every record in the period and must equal the cells plus `pending` plus `unmapped`.
- `count` keys are written for readability; readers derive every count from `records`.
- `people` (optional) maps each record in `total` to the subject ids it concerns. Its keys must be exactly the records in `total`. A run record without `people` has an unknown head count, not zero.
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
- `rows`: the entity type listed, one row per entity. `period.field`: the calendar-date field that places it in the period.
- `columns`: a non-empty array. Each column has a `label` and exactly one source, recognised in this order:

| Keys | Cell |
|---|---|
| `people: "count"` | The number of distinct subjects the record concerns. |
| `person` (+ `all`) | That field of the subject the record concerns; empty when it concerns several. With `all: true`, every subject's value, joined by `", "`. |
| `year` + `startMonth` | The year the date field falls in, for a year starting in month `startMonth` (1–12). |
| `field` + `ref` | Field `ref` of the entity that `field` refers to. |
| `field` + `scheme` + `version` (+ `part`) | The label of the classified value carried to that version; with `part: "top"` the label of its top-level ancestor (`part` is `top` or `item`, default `item`). |
| `field` | The field value as written. |

## Reading rules

The reader never stops on a bad file. A file whose path matches the layout but cannot be used is listed as unreadable with a reason, and every other file is still read:

| Reason | When |
|---|---|
| `Malformed` | Not valid JSON, including a file cut off while being written. |
| `UnknownFormat` | `format` is missing or is not the format this engine reads for that path. |
| `Invalid` | A required key is missing or has the wrong type or value; a run record whose report form is not in the vault or whose totals do not add up. |
| `NameMismatch` | The file name or path disagrees with the id, device, name or version inside. |
| `DuplicateId` | Two change files carry the same id with different content. |

A sync client's conflicted copy of a change file is read as a valid input: its name may add, before or after `.json`, anything that begins with a character other than a letter or digit (for example `<id>.<device> (conflicted copy).json` or `<id>.<device>.json-LAPTOP`). Copies with identical content count once. A copy of any other vault file — a scheme, crosswalk, form or run record named with something added after `.json` — is listed as `NameMismatch` rather than ignored, since it may differ from the file it copies and should be looked at by a person.

The only condition that refuses the whole read is the declaration check described under [Declaration](#declaration-vaultjson).

## Compatibility

- A change a previous engine could ignore without producing a wrong number (a new optional key) is additive and keeps the declared version.
- A change that would make a previous engine count wrongly without noticing (a new folder, a key that changes what is counted) raises the declared vault format, so that previous engines refuse the vault instead of reading it.

`VaultWriter` produces indented UTF-8 JSON with `\n` line endings and a trailing newline, stamps `at` in the device's local time with its offset, and gives every file a fresh id, so its path never names an existing file.
