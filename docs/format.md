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
  packs/<pack>/v<N>.json                     pack manifest: a pack applied to this vault, version N
  labels/<pack>/v<N>.<locale>.json           labels a pack gives in one locale
  fields/<pack>/<type>/v<N>.json             fields a pack declares for an entity type
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
- `suggest` (default `false`) marks items a host may offer as suggestions.
- `effective` (optional): `from` (required) and `to` (optional, on or after `from`), calendar dates on which the body that issues the scheme puts this version in force. A version without it is in force throughout. It guides which version a host offers for input (`SchemeCatalog.InForce`, the highest version in force on a given date); reports never use it.

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
- `version` is an integer of 1 or more, and `label` is text a person reads.
- `depends` (optional) maps the ids of other packs to a minimum version, an integer of 1 or more. A pack never depends on itself. A pack only adds, so a later version holds everything an earlier one did and a minimum version is all a dependency needs.
- `provides` lists the definition files this version added, as vault paths: schemes, crosswalks, report and export forms, labels and field definitions. It is required, and may be empty. It is what says which pack owns which definition.
- A manifest in the vault is the record that the pack was applied; there is no separate record. Of several versions of a pack, the highest one counts.

`VaultContent.CheckPacks` compares the packs with each other and with the definition files the vault could read, and reports each problem with the pack it concerns (`PackIssue`):

| Kind | When |
|---|---|
| `MissingDependency` | A pack builds on a pack the vault does not hold. |
| `OlderDependency` | A pack needs a later version of a pack than the vault holds. |
| `MissingFile` | A manifest lists a definition file the vault does not hold in a readable form. |
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
  "fields": { "session": { "date": "Date" } }
}
```

- Path: `labels/<pack>/v<version>.<locale>.json`; `pack`, `version` and `locale` must match it. `locale` is a language tag such as `fr` or `en-US`.
- `schemes` (optional) maps a scheme name to a scheme version to item codes to labels. `fields` (optional) maps an entity type to field names to labels. Every label is non-empty text.
- Labels change what people read, never a code or what is counted, so a renamed item needs no new scheme version.
- When several packs label the same thing in one locale, the pack that builds on all the others wins. Two packs that do not build on each other and give different labels are a conflict (`LabelCatalog.Conflicts`), and neither label is used for that locale. The same text from several packs is not a conflict.
- A host asks for labels by a list of locales in order of preference (`LabelCatalog.SchemeLabel`, `FieldLabel`). Each locale falls back to its language alone (`fr-CA`, then `fr`), and tags are compared without regard to case; if no locale gives a label, the answer is `null` and the host shows the scheme item's own `label`. Of several versions of a pack's labels in one locale, the highest counts.

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

- Path: `fields/<pack>/<type>/v<version>.json`; `pack`, `type` and `version` must match it. `type` is the entity type the fields belong to.
- `fields` (optional) declares fields. A field has a `name`, unique within the file, and a `kind`, one of `text`, `date`, `number`, `coded`, `reference` and `references`.
- A `coded` field names the `scheme` its values are classified in, and no other kind has one. A `reference` or `references` field names the entity `type` it refers to, and no other kind has one.
- `tier` is `structured` (the default) or `narrative`. A narrative field is written content: `ExportRunner.Run` leaves every column that would carry it empty — its text, a year taken from it, the label of its code — and names the column in `ExportTable.Withheld`. Callers pass `content.FieldCatalog()` (required; `FieldCatalog.Empty` for a vault without field definitions).
- `required` (default `false`) says a value must be entered.
- `default` (optional) is `{ "subject": "<field>" }`: the host offers, when the record is written, the value of that field of the record's subject. The record keeps the value as entered.
- `label` (optional) is what people read for the field; [Labels](#labels) can give it per locale.
- `constrain` (optional) narrows fields other packs declared, by `name`: `required` and `hidden` may each be set to `true`, and at least one must be. A key set to `false` is invalid, because a constraint only narrows. A pack does not constrain its own fields; it declares them as they should be.

`VaultContent.FieldCatalog` merges the field files of the vault's packs, each pack at its highest version, packs in the order they build on each other (packs that do not build on each other by id, and packs without a manifest last). A field is kept from the first pack that declares it; then each constraint is applied. `FieldCatalog.Issues` lists what does not fit (`FieldIssue`):

| Kind | When |
|---|---|
| `DuplicateField` | Two packs declare a field of the same name for one entity type; the earlier pack's declaration stands. |
| `ConstraintWithoutField` | A constraint names a field no pack declares. |
| `ConstraintFromUnrelatedPack` | A constraint narrows a field of a pack it does not build on; it is not applied. |
| `HiddenRequired` | A field ends up both required and hidden. |

## Reading rules

The reader never stops on a bad file. A file whose path matches the layout but cannot be used is listed as unreadable with a reason and what it was for — a subject's or group's records, a scheme version, a crosswalk, a form, a pack, labels, field definitions or a run record, read from the path alone (`VaultFileKind.Of`, which also reads a sync client's copy by the start of its name) — and every other file is still read:

| Reason | When |
|---|---|
| `Malformed` | Not valid JSON, including a file cut off while being written. |
| `UnknownFormat` | `format` is missing or is not the format this engine reads for that path. |
| `Invalid` | A required key is missing or has the wrong type or value; a run record whose report form is not in the vault or whose totals do not add up. |
| `NameMismatch` | The file name or path disagrees with the id, device, name or version inside. |
| `DuplicateId` | Two change files carry the same id with different content. |

A sync client's conflicted copy of a change file is read as a valid input: its name may add, before or after `.json`, anything that begins with a character other than a letter or digit (for example `<id>.<device> (conflicted copy).json` or `<id>.<device>.json-LAPTOP`). Copies with identical content count once. A copy of any other vault file — a scheme, crosswalk, form, pack manifest, label file, field file or run record named with something added after `.json` — is listed as `NameMismatch` rather than ignored, since it may differ from the file it copies and should be looked at by a person.

The only condition that refuses the whole read is the declaration check described under [Declaration](#declaration-vaultjson).

## Compatibility

- A change a previous engine could ignore without producing a wrong number (a new optional key) is additive and keeps the declared version.
- A change that would make a previous engine count wrongly without noticing (a new folder of records, a key that changes what is counted) raises the declared vault format, so that previous engines refuse the vault instead of reading it. Folders a previous engine ignores without changing any count — `packs/`, `labels/`, `fields/` — are additive.

`VaultWriter` produces indented UTF-8 JSON with `\n` line endings and a trailing newline, stamps `at` in the device's local time with its offset, and gives every file a fresh id, so its path never names an existing file.
