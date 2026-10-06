# Changelog

Notable changes to the `Openquote` and `Openquote.Gil` packages, which are released together at the same version.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/) — before 1.0, a minor version may break the API.
Versions before 0.16.0 are described by their tags and the package history on nuget.org.

## [Unreleased]

## [0.16.1] - 2026-10-06

### Fixed
- An export column holding every subject's value of a field lists the values in their order, not in the order the subjects were created.

### Changed
- `Openquote.Gil` depends on Gil 0.15.0.

## [0.16.0] - 2026-10-05

### Added
- `DefinitionWriter` writes report forms in the order a reader reads them.

[Unreleased]: https://github.com/iyulab/openquote/compare/v0.16.1...HEAD
[0.16.1]: https://github.com/iyulab/openquote/compare/v0.16.0...v0.16.1
[0.16.0]: https://github.com/iyulab/openquote/compare/v0.15.1...v0.16.0
