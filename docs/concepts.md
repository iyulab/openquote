# Concepts

Openquote keeps ongoing records as a folder of immutable files and rebuilds everything else from them. This page explains the ideas behind that design. The exact file layout and JSON are in [Vault format](format.md).

The engine is domain-neutral. Entity types, fields, schemes and forms are data supplied by the host application. Names such as `session`, `subject` or `topic` below are examples only.

## Entities and changes

An **entity** is anything the vault records: a subject, a group, a practitioner, a session, and so on. An entity is never stored as a single file. It exists only as the sequence of **changes** made to it.

- Each change is a new file, written with create-new semantics. No existing file is ever edited or replaced.
- A change sets only the fields it names. `null` clears a field on purpose; a field it does not name is left untouched.
- The entity's id is the id of the change that created it.
- The current state of an entity (`Entity`) is computed on every read and never stored. A host can cache it, but the change files remain the only source.

Because writers never touch the same file, several devices can share a vault through a network folder or a sync client without one silently overwriting another. The history of changes is also the evidence for every value.

## Merging and conflicts

Each change lists in `base` the changes to the same entity its writer had already seen. Together they form a "has seen" graph, and `EntityMerger` reads it field by field:

1. Among the changes that set a field, those no other such change has seen are the field's **heads**.
2. One head: its value is current.
3. Several heads: the field is in **conflict**. Every head's value is kept and reported in `Entity.Conflicts`. `Entity.Fields` shows the head with the highest change id, a deterministic pick rather than a decision.

A conflict is settled by a person. Their choice is written as a new change whose `base` names all current heads, so it has seen every competing value. Nothing is lost along the way: all earlier values stay in their files.

A change that names a base not yet present (not synced to this device yet, or unreadable here) is still read; the missing link is ignored until it arrives. Without it the engine cannot tell that the change had seen what came before, so two values can show as a conflict that is only a gap. `Entity.MissingBase` lists those absent changes, so a host can say "waiting for another device" instead of asking a person to choose.

## Classification schemes are immutable versions

A **classification scheme** (for example a list of topics) is stored as numbered versions. A version is never edited; editing a scheme produces the next version.

- An item's **code** is its identity and its **label** is what people see. Relabelling an item keeps its code.
- A record stores a classified value exactly as entered: scheme, version and code. It is never rewritten when the scheme moves on.
- A version may say the dates it is in force. A host offers the version in force on a record's date (`SchemeCatalog.InForce`), and a report may count in the version in force on the last day of its period instead of a named one.
- A scheme may **extend** another (format 1) — a body's own list beside a shared one. Each of its items names its **anchor**, the item of the extended scheme it counts as: a report counting the shared scheme counts a local value as its anchor, and one counting the local scheme counts its own items.
- A coded field may take **several values** (format 1), one of them **primary**. A count places the record by its primary value, or by every value when the form says so. With several values and none marked primary, the record waits for a person to say which comes first, as a split category does (`VaultWriter.Reclassify` marks it and keeps the others).

## Crosswalks

A **crosswalk** relates two versions of a scheme with `[old code, new code]` links. Reading the links as a whole, an old item can map to a new one in four shapes:

| Shape | Example | Carried automatically |
|---|---|---|
| 1:1 | `attendance` becomes `school/attendance` | yes |
| N:1 | `sadness` and `worry` merge into `emotion` | yes |
| 1:N | `relations` splits into `peers` and `family` | no, waits for a person |
| N:M | several old items spread across several new ones | only for old codes with a single link |

The shape is not stored; it follows from the links. The rule the engine applies is per old code (`SchemeCatalog.Resolve`):

- **Assigned**: the code leads to exactly one new code. The value is carried, with no guess involved.
- **Pending**: it leads to several new codes. The record waits for a person to choose, and is never split or estimated.
- **Unmapped**: it leads to none, or no chain of crosswalks reaches the target version. This is counted apart from pending, because it points at a gap in the crosswalk rather than at a choice to make.

Several revisions (v1 to v2 to v3) are crossed by chaining crosswalks, taking the shortest chain; after each step only codes that exist in the next version are kept.

A format 1 crosswalk may also state how an old item relates to the new one. `equivalent` (the same meaning) and `narrower` (the new item holds all of the old one) carry a value without asking; `broader` (the new item holds only part of the old one) leaves it to a person even when it is the only link, since carrying it would claim more than was recorded; `retired` keeps where a retired item went nearest and carries nothing. A crosswalk may lead into another scheme — from a local list into one a report is made in — and values are carried along it as along a scheme's own versions.

`Entity.Classify(field, scheme, version, catalog)` applies this rule to one record: it takes the record's most recent value entered in that scheme at that version or an earlier one and carries it forward. A pending result lists the codes a person chooses from, so a host never re-implements the rule. Report runs place records the same way.

A person's choice for a pending record is a `reclassify` change that sets the field to a value in the newer version (`VaultWriter.Reclassify`). It is accepted only when the record is pending in that version and the code is one of its candidates; any other value is refused, because a change file cannot be taken back. The value entered originally stays in its earlier change file.

## Report forms and run records

A **report form** is an immutable, versioned definition: which entity type it counts, which calendar-date field places a record in a period and the unit it is run over (a day, a month, a year from any month, or any range of days), and one to three **dimensions** whose values make each cell's key. A dimension is a classified field of the record — counted by its primary value, or by every value it holds — or a field the record or the people it concerns hold as written. A form may also set **conditions** every counted record meets, and the **measures** it shows: records, people (distinct subjects) and visits (each record's subjects, added up). A classified dimension counts in a named scheme version, or in the version in force on the last day of the period.

Running a form (`ReportRunner`) for a period does not guess:

- Each record's value is carried to the form's scheme version. An assigned value lands in a cell; a pending value goes to `pending`; a value with no code there goes to `unmapped`; a field with no value at all (never set, or cleared with no earlier value to count) goes to `blank`, so an empty field is told apart from a gap in the crosswalks. A record whose period field, a dimension or a condition holds values set without seeing each other goes to `conflicted` and in no cell — counting any one of them would decide for the person who has to pick; a disputed date puts it in every period one of its dates falls in.
- If a record has since been reclassified to a version newer than the form's, the run uses the most recent value that is at or below the form's version. Earlier values are never lost, so an older form can be run again later.
- The total is always the cells plus pending, unmapped, blank and conflicted, each record in exactly one. Nothing is dropped to make numbers look complete.

Each run can be kept as a **run record**. A run record is never edited; running the form again produces a new one. It holds:

- the form and version, the scheme versions counted in, and the crosswalks applied;
- the period;
- for every cell, and for pending, unmapped, blank, conflicted and the total, the ids of the records behind the number;
- for every record, the subjects it concerns, from which a **head count** (distinct people) is computed beside every record count. A group-held record contributes its `attendees`.

Because every number keeps its evidence, two runs of the same form can be compared record by record (`ReportDiff`): records entered late, records removed or destroyed since, records moved by a scheme revision, records that were in conflict and now have a place (a person settled the values), records moved for another reason, and records unchanged. Only runs that place records the same way are compared — the same form name, entity type, period field, dimensions in order, and period; the form and scheme versions may differ. Any other pair is refused, since every record would read as moved by a person.

### One pass: run, settle, run again, compare

A run's `Cells` hold the records behind each number by key — one value per dimension — `Pending`, `Unmapped`, `Blank` and `Conflicted` the records in no cell, and `PeopleOf` the distinct subjects behind any of them. A pending record is settled by a person choosing one of the candidates `Entity.Classify` lists; the next run counts it where it was placed, and comparing with the kept run shows it as moved rather than entered late:

```csharp
using Openquote.Classification;
using Openquote.Records;
using Openquote.Reports;
using Openquote.Vault;

// Every write is a new file; this host keeps the vault in plaintext.
void Save(VaultFile file)
{
    var path = Path.Combine(vault, file.Path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, file.Content.ToArray());
}

var content = VaultReader.Read(VaultFiles.FromDirectory(vault));
var records = EntityMerger.Merge(content.Changes);
var catalog = content.Catalog();
var form = content.Reports.Single(r => r.Name == "monthly-topic" && r.Version == 2);
var writer = new VaultWriter("desk01");

// 1. Run the form for April and keep the run.
var before = ReportRunner.RunMonth(form, 2026, 4, records.Values, catalog);
foreach (var cell in before.Cells)
    Console.WriteLine($"{string.Join(" / ", cell.Key)}: {cell.Count} records, {before.PeopleOf(cell.Records)?.Count} people");
Console.WriteLine($"pending {before.Pending.Count}, unmapped {before.Unmapped.Count}, blank {before.Blank.Count}, conflicted {before.Conflicted.Count}, total {before.Total.Count}");
Save(writer.RunRecord(before));

// 2. A person settles each pending record by choosing one of its candidates, in the version the run counted in.
var topic = before.Report.Dimensions.First(d => d.Classified);
foreach (var id in before.Pending)
{
    var record = records[new EntityRef(form.Counts, id)];
    var waiting = record.Classify(topic.Field, topic.Scheme!, topic.Version!.Value, catalog);
    var chosen = waiting.Candidates[0]; // in an application, the person's choice
    Save(writer.Reclassify(record, topic.Field, new CodedValue(topic.Scheme!, topic.Version.Value, chosen), catalog));
}

// 3. Read the vault again, run again, and explain the difference from the kept run.
content = VaultReader.Read(VaultFiles.FromDirectory(vault));
var after = ReportRunner.RunMonth(form, 2026, 4, EntityMerger.Merge(content.Changes).Values, content.Catalog());
var kept = content.Runs.Single().Run;
var diff = ReportDiff.Compare(kept, after, content.Catalog());
Console.WriteLine($"moved by a person {diff.Moved.Count}, entered late {diff.Late.Count}, unchanged {diff.Unchanged.Count}");
```

`Reclassify` refuses a code that is not one of the candidates, and a record that is not pending in that version: a change file cannot be taken back, so a wrong choice is stopped before it is written.

## Export forms

An **export form** lays out one period's records as rows for another system or spreadsheet: one row per record of a given entity type, ordered by date and then by record id. Each column takes its cells from one source:

- a field of the record, as written;
- a classified field carried to a chosen scheme version, shown by its label or its top-level ancestor's label;
- a field of an entity the record refers to, such as a practitioner's name;
- the number of distinct subjects the record concerns;
- a field of those subjects: the one subject's value, or every subject's joined;
- the year a date falls in, for a year that starts in a given month (for example an academic or fiscal year).

An outside form often asks for cells in a shape of its own, and an export form of format 1 fills them: a fixed text in every row, a classified value at a given level of its hierarchy, a date in ISO 8601 basic format (`20260310`), a whole number divided (minutes as hours and minutes), and for a record about several subjects the value they share, or a text that says they differ.

An export counts nothing, but it follows the same rule as reports: a classified cell that is pending or unmapped is left empty, and the record is listed apart so the gap is seen before the rows go anywhere. A field that holds no value is simply an empty cell (`Entity.HasValue` decides it for both).

## Packs and layers

Most of what a vault knows about its field of work (which schemes classify a record, which fields an entity has, which forms exist, what things are called in a language) is data. A **pack** is that data ready to share, and a vault stacks several of them. The names below (`care`, `care.school`, `region-a`) are examples only.

- **A pack is a bundle of data in the vault's own format.** It is a set of scheme, crosswalk, form, label, field-definition and suggestion files with a manifest that lists them. Applying a pack copies those files into the vault, and the manifest is the record that it was applied. The engine reads them like any other definition file.
- **A pack builds on other packs.** `care.school` can depend on `care` at a minimum version and add to it. A pack only adds, so a later version holds everything an earlier one did, and a minimum version is all a dependency needs to say. A vault whose packs do not fit together (a missing or older dependency, a missing or shared file, a cycle) is reported by `VaultContent.CheckPacks`, not refused.
- **A pack can add, narrow and label, and nothing else.** It adds schemes, forms and fields. It narrows a field of a pack it builds on, by making it required or hiding it, never the other way round. It gives labels per locale, so `region-a` can call the items of `care`'s schemes by names people there use, and says which items hosts may suggest, or must have a person confirm (`SuggestionCatalog`).
- **A code and what is counted are never overridden.** No pack replaces a code, moves an item, or changes how a record is placed in a cell. A different classification is a new scheme version with a crosswalk, and a label can only change what people read, so a relabel needs no new version.
- **Precedence follows what builds on what, not the order packs were installed.** Where packs label the same thing, or say different things about suggesting an item, the one that builds on the others wins; two packs that do not build on each other and disagree are reported rather than one being preferred silently, and neither label is used (`LabelCatalog.Conflicts`). Fields work the other way: a pack that redeclares a field of a pack it builds on is reported, and the earlier pack's declaration stands (`FieldCatalog.Issues`). To narrow such a field, a pack uses `constrain`.
- **`local` is kept for what people make in the app.** What people add to a vault themselves is meant to sit in a layer above every pack, so the id `local` (and `oq`, for the engine) cannot name a pack.

Fields declared as written content (`tier: narrative`) are left out of exports, wherever a column would read them from: the field itself, a year taken from it, the label of its code. `ExportRunner.Run` takes the vault's field definitions (`content.FieldCatalog()`) as a required argument, so an export cannot be run without the guard; `FieldCatalog.Empty` is for a vault that declares no fields.

## Reading rules

Reading a vault (`VaultReader.Read`) is tolerant of individual files and strict about the vault as a whole:

- **An unreadable file is listed, not fatal.** Invalid JSON, an unknown per-file format, a missing key, a name that disagrees with the content, or two different files claiming the same id: each is reported in `VaultContent.Unreadable` with its reason and what the file was for (`Kind`, read from its path — a subject's or group's records, a scheme version, a crosswalk, a report or export form, a pack, labels, field definitions, suggestions, a run record), and the rest of the vault is read normally.
- **A newer or unknown vault declaration refuses the whole read.** A later format may lay records out in a way this engine would count wrongly without noticing, so `VaultFormatException` is thrown and nothing is returned.
- Files outside the vault layout are ignored.
