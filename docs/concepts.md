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

A change that names a base not yet present (not synced to this device yet) is still read; the missing link is ignored until it arrives.

## Classification schemes are immutable versions

A **classification scheme** (for example a list of topics) is stored as numbered versions. A version is never edited; editing a scheme produces the next version.

- An item's **code** is its identity and its **label** is what people see. Relabelling an item keeps its code.
- A record stores a classified value exactly as entered: scheme, version and code. It is never rewritten when the scheme moves on.

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

A person's choice for a pending record is a `reclassify` change that sets the field to a value in the newer version. The value entered originally stays in its earlier change file.

## Report forms and run records

A **report form** is an immutable, versioned definition: which entity type it counts, which calendar-date field places a record in a month, which classified field and scheme version form the rows, and optionally which field splits the columns.

Running a form (`ReportRunner`) for a period does not guess:

- Each record's value is carried to the form's scheme version. An assigned value lands in a cell; a pending value goes to `pending`; an unmapped or missing value goes to `unmapped`.
- If a record has since been reclassified to a version newer than the form's, the run uses the most recent value that is at or below the form's version. Earlier values are never lost, so an older form can be run again later.
- The total is always the cells plus pending plus unmapped. Nothing is dropped to make numbers look complete.

Each run can be kept as a **run record**. A run record is never edited; running the form again produces a new one. It holds:

- the form and version, the scheme version counted in, and the crosswalks applied;
- the period;
- for every cell, and for pending, unmapped and the total, the ids of the records behind the number;
- for every record, the subjects it concerns, from which a **head count** (distinct people) is computed beside every record count. A group-held record contributes its `attendees`.

Because every number keeps its evidence, two runs of the same form can be compared record by record (`ReportDiff`): records entered late, records removed or destroyed since, records moved by a scheme revision, records moved for another reason, and records unchanged.

## Export forms

An **export form** lays out one period's records as rows for another system or spreadsheet: one row per record of a given entity type, ordered by date and then by record id. Each column takes its cells from one source:

- a field of the record, as written;
- a classified field carried to a chosen scheme version, shown by its label or its top-level ancestor's label;
- a field of an entity the record refers to, such as a practitioner's name;
- the number of distinct subjects the record concerns;
- a field of those subjects: the one subject's value, or every subject's joined;
- the year a date falls in, for a year that starts in a given month (for example an academic or fiscal year).

An export counts nothing, but it follows the same rule as reports: a classified cell that is pending or unmapped is left empty, and the record is listed apart so the gap is seen before the rows go anywhere.

## Reading rules

Reading a vault (`VaultReader.Read`) is tolerant of individual files and strict about the vault as a whole:

- **An unreadable file is listed, not fatal.** Invalid JSON, an unknown per-file format, a missing key, a name that disagrees with the content, or two different files claiming the same id: each is reported in `VaultContent.Unreadable` with its reason, and the rest of the vault is read normally.
- **A newer or unknown vault declaration refuses the whole read.** A later format may lay records out in a way this engine would count wrongly without noticing, so `VaultFormatException` is thrown and nothing is returned.
- Files outside the vault layout are ignored.
