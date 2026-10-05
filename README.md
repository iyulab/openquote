# Openquote

**A domain-neutral engine for ongoing records kept as immutable change files.**

Openquote reads a folder of records — a *vault* — where every edit is a new file and no file is ever rewritten. From those files it rebuilds the current state of each record, keeps concurrent edits from several devices side by side instead of losing one, carries classified values across revisions of a classification scheme, and produces statistics whose every count traces back to the records behind it.

It knows nothing about any particular field. What a record means, which schemes classify it, and which report or export forms exist are data in the vault, supplied by the applications and packs built on top of it.

> Status: early development. Vault format version 0 is frozen; version 1 adds report forms by up to three dimensions, coded fields that take several values, schemes that extend others and crosswalks into another scheme. The engine reads both, and a vault holding what only version 1 counts right says so.

## What it does

- **Reads a vault** (`VaultReader.Read`) — change files, classification schemes and their crosswalks, report and export forms, and kept report runs. A file it cannot use is listed as unreadable — with why, and what it was for as read from its path (`VaultFileKind`) — rather than stopping the read; a vault declared in a newer or unknown format is refused as a whole, and `VaultContent.RequiredVersion` says which format the files a vault holds need.
- **Merges changes into records** (`EntityMerger.Merge`) — replays each record's change history, and keeps conflicting edits made on different devices as named heads until one is chosen.
- **Carries classified values across scheme versions** (`SchemeCatalog`) — one-to-one and many-to-one links are followed automatically; a value whose category was split waits for a person instead of being guessed. A scheme may extend another, each of its items anchored to the one it refines, and a crosswalk may lead into another scheme.
- **Runs reports** (`ReportRunner`) — counts records over a day, a month, a year from any month or any range of days, split by up to three dimensions — a classified field of the record (by its primary value, or by every value it holds), or a field of the people it concerns — and narrowed by conditions, as records, people and visits. Every count keeps the records behind it; records with no value, a value waiting for a person or with no place in the form, and values set on two devices without seeing each other are kept apart from every cell. Two runs of a form compare (`ReportDiff`), telling a value a person settled from a move nothing explains.
- **Lays records out for other systems** (`ExportRunner`) — one row per record in a period, with columns taken from fields, classified values, referenced records and the people involved.
- **Layers packs** (`VaultContent.CheckPacks`, `LabelCatalog`, `FieldCatalog`, `SuggestionCatalog`) — data packs build on each other: a later pack adds schemes, forms and fields, narrows fields of the packs it builds on, names the entity types it declares and where those are kept (`FieldCatalog.Types`, `KeptUnder`), relabels items, types, fields and forms per locale — with other names a field goes by for taking data in, and the value a field starts from — and says which items may be suggested, or must be confirmed when they are, without ever changing a code or what is counted. Run an export with `content.FieldCatalog()` and fields declared as written content are left out of every column.
- **Writes changes** (`VaultWriter`) — every write is a new file, a person's choice of value included (`Reclassify`); nothing existing is modified. Definitions a host keeps beside its packs — a list extending a shared scheme, with the crosswalk from its previous version, and a form counting by it — are produced as the reader reads them (`DefinitionWriter`).
- **Suggests codes** (`CodeSuggester`, in the separate `Openquote.Gil` package) — for a record being entered, the codes of its empty classified fields that the vault's settled records suggest, each with why it is suggested (`SuggestionBasis`): the settled records most like it that hold the code, the records that share a field's value with it, or that the code is simply chosen often — then with no record as evidence. Records close enough to answer come first, at thresholds chosen by replaying the settled records once there are enough of them, and a code a person has to confirm is offered only when such records hold it, or the nearest settled record does. It learns in memory from the vault each time, with no model, and offers only items that may be suggested (`SuggestionCatalog`), in the version in force on the record's date, setting apart those a person has to confirm. Who a record is about is never compared.

The engine only ever sees plaintext. A host that keeps its vault encrypted decrypts each file before handing it over.

## Example

```csharp
using Openquote.Records;
using Openquote.Reports;
using Openquote.Vault;

var content = VaultReader.Read(VaultFiles.FromDirectory("path/to/vault"));
var records = EntityMerger.Merge(content.Changes);

var form = content.Reports.Single(r => r.Name == "monthly-topic" && r.Version == 2);
var run = ReportRunner.RunMonth(form, 2026, 4, records.Values, content.Catalog());
```

## Getting it

It targets .NET 10.

```sh
dotnet add package Openquote
dotnet add package Openquote.Gil   # suggestions; brings Gil
```

To build and test the engine itself: `dotnet test --solution Openquote.slnx`.

## Documentation

- [Concepts](https://github.com/iyulab/openquote/blob/main/docs/concepts.md) — records and changes, classification versions and crosswalks, reports and exports
- [Vault format](https://github.com/iyulab/openquote/blob/main/docs/format.md) — the files, their JSON, and the rules for reading and writing them

## License

[MIT](https://github.com/iyulab/openquote/blob/main/LICENSE)
