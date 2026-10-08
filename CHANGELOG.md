# Changelog

Notable changes to the `Openquote` and `Openquote.Gil` packages, which are released together at the same version.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/) — before 1.0, a minor version may break the API.
Versions before 0.16.0 are described by their tags and the package history on nuget.org.

## [Unreleased]

## [0.18.0] - 2026-10-08

### Added
- A field file of a type that closes a case may say within how many days a follow-up is expected (`followUpDays`). `FieldCatalog.FollowUpDays` gives it; `FieldIssueKind.FollowUpWithoutClosing` reports it on a type that closes no case. `SubjectCase.FollowUpDue`, `FirstAfterClosing` and `FirstAfterClosingDay` tell when it is due and the subject's first record after the closing — in the case or beginning the next one — and `SubjectCase.FollowUpOn(day)` says whether it came in time, came late, is still awaited or is overdue. Nothing is stored. Engines that predate the key ignore it, and every count stays the same.
- A pack may give scales (`scales/<pack>/v<N>.json`, format `openquote.scales/0`): which entity type records a response, which of its fields names the scale and which holds the score, and for each scale its range, the way a better score moves and, optionally, its terms of use. `VaultContent.ScaleCatalog` merges them; `ScaleCatalog.Check` lists a scale field that is not coded or a score field that is not a number. A scale holds no cutoff and no rule for a change.
- `ScaleReader.Read` reads, for each of a subject's cases, each scale's baseline (the first score) and last available score up to the closing, whether they fall on two days, and the change between them; a response it cannot use is listed apart with the reason. `ScaleReader.Summarize` counts the cases closed in a stretch of days by scale, and those closed with no score at all. Nothing is stored. Engines that predate `scales/` ignore the folder, and every count stays the same.

### Changed
- `Openquote.Gil` takes Gil 0.17.0 and hands each judged field its memory similarity floor with its threshold: when several similar records vote, a draft less like them than the replayed answers were is only guessed at, though they agree. The nearest record still decides alone (one vote), so suggestions are unchanged.

## [0.17.1] - 2026-10-06

### Fixed
- A code a person has to confirm is offered whenever the nearest settled record holds it, as documented. A code settled rarely could fall outside the few candidates a suggestion lists — after the codes chosen often — and was then not offered at all. On synthetic sessions about a rare topic to confirm, it is now offered for 23–33 of 40 instead of 20–22; for sessions about other topics, still for none of 80 from 60 settled records on, and for 3 of 80 with 20.

### Changed
- `Openquote.Gil` builds on Gil 0.16.0. Its similar records now vote; `CodeSuggester` keeps the nearest record deciding alone, as before. On synthetic sessions written in other words than the settled ones, ten voters at thresholds replayed for a precision of 0.7 answered wrongly 37–58% of them from 240 settled records on, against none for the nearest record alone, while the codes suggested first stayed the same.

## [0.17.0] - 2026-10-06

### Added
- A field file may say where its entity type is listed among the others (`order`), which date field dates a record of it (`dated`), and whether a record of it opens or closes a subject's case (`role`). `FieldCatalog.TypeOrder`, `DatedField` and `TypeRole` give them from the first pack that says; `FieldIssueKind.DatedNotADate` and `RoleOnItsOwn` report what cannot apply. Engines that predate the keys ignore them, and every count stays the same.
- `CaseReader.Read` reads a subject's cases from its records: a record that opens a case starts one, a record that closes it ends it, the records between belong to it, and records after a closing stay with that case as follow-ups. A case that began without an opening, a case followed by another opening before any closing, and records without a date (`SubjectCases.Undated`) are told apart. Nothing is stored.

## [0.16.1] - 2026-10-06

### Fixed
- An export column holding every subject's value of a field lists the values in their order, not in the order the subjects were created.

### Changed
- `Openquote.Gil` depends on Gil 0.15.0.

## [0.16.0] - 2026-10-05

### Added
- `DefinitionWriter` writes report forms in the order a reader reads them.

[Unreleased]: https://github.com/iyulab/openquote/compare/v0.18.0...HEAD
[0.18.0]: https://github.com/iyulab/openquote/compare/v0.17.1...v0.18.0
[0.17.1]: https://github.com/iyulab/openquote/compare/v0.17.0...v0.17.1
[0.17.0]: https://github.com/iyulab/openquote/compare/v0.16.1...v0.17.0
[0.16.1]: https://github.com/iyulab/openquote/compare/v0.16.0...v0.16.1
[0.16.0]: https://github.com/iyulab/openquote/compare/v0.15.1...v0.16.0
