# Openquote

**A domain-neutral engine for ongoing records kept as immutable change files.**

Openquote reads a folder of records — a *vault* — where every edit is a new file and no file is ever rewritten. From those files it rebuilds the current state of each record, keeps concurrent edits from several devices side by side instead of losing one, carries classified values across revisions of a classification scheme, and produces statistics whose every count traces back to the records behind it.

It knows nothing about any particular field. What a record means, which schemes classify it, and which report or export forms exist are data in the vault, supplied by the applications and packs built on top of it.

> Status: early development. The vault format is at version 0. Versions are tagged here; the package is not on nuget.org yet — see [Getting it](#getting-it).

## What it does

- **Reads a vault** (`VaultReader.Read`) — change files, classification schemes and their crosswalks, report and export forms, and kept report runs. A file it cannot use is listed as unreadable — with why, and what it was for as read from its path (`VaultFileKind`) — rather than stopping the read; a vault declared in a newer or unknown format is refused as a whole.
- **Merges changes into records** (`EntityMerger.Merge`) — replays each record's change history, and keeps conflicting edits made on different devices as named heads until one is chosen.
- **Carries classified values across scheme versions** (`SchemeCatalog`) — one-to-one and many-to-one links are followed automatically; a value whose category was split waits for a person instead of being guessed.
- **Runs reports** (`ReportRunner`) — counts records by a classified field for a period, with the records behind each cell and the number of different people they concern, and compares two runs of the same form (`ReportDiff`).
- **Lays records out for other systems** (`ExportRunner`) — one row per record in a period, with columns taken from fields, classified values, referenced records and the people involved.
- **Writes changes** (`VaultWriter`) — every write is a new file; nothing existing is modified.

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

It targets .NET 10. Until the package is published, pack it from a version tag into a local package source:

```sh
git clone --branch v0.2.0 https://github.com/iyulab/openquote
dotnet pack openquote/src/Openquote/Openquote.csproj -c Release -o ./packages
dotnet nuget add source "$(pwd)/packages" --name openquote-local
dotnet add package Openquote --version 0.2.0
```

To build and test the engine itself: `dotnet test --solution Openquote.slnx`.

## Documentation

- [Concepts](docs/concepts.md) — records and changes, classification versions and crosswalks, reports and exports
- [Vault format](docs/format.md) — the files, their JSON, and the rules for reading and writing them

## License

[MIT](LICENSE)
