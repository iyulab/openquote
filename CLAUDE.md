# openquote

A domain-neutral .NET engine for vaults of immutable change files. See [README.md](README.md) and [docs/](docs/).

## Language

Everything committed here is English: code, comments, XML docs, tests, commit messages, `docs/`.

## Rules

- The engine knows no field or region. Field-specific meaning belongs in the applications and packs built on it.
- The engine is encryption-neutral: it reads and writes plaintext files only.
- A vault file, once written, is never modified. Every change is a new file.
- Public text describes the library's observable behaviour and the conditions that trigger it — never where or how an issue was found.

## Build and test

```sh
dotnet test --solution Openquote.slnx
```

Warnings are errors (`Directory.Build.props`). The test build also compiles the C# examples of
`README.md` and `docs/` (`tests/Openquote.Tests/DocExamples.targets`), so a change to an example is
checked by running the tests again.
