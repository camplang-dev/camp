# Camp.Compiler

`Camp.Compiler` is the compiler library. It owns parsing, preprocessing,
semantic analysis, lowering, C emission, metadata serialization, target loading,
project loading, first-class test/coverage services, and language-service APIs.

## Setup

Build from the repository root:

```sh
dotnet build src/camplang.sln
```

## Usage

Most users reach this library through `campc` or `camp-lsp`. Compiler code can
call `CompilerDriver.Execute`, `CampProjectLoader`, `CampLanguageService`, the
test discovery/result services, or the coverage map/result services directly
for tests and tools.

## Testing

Use targeted tests in `src/Camp.Compiler.TestRunner` for compiler behavior.
Golden cases live under `tests`. Test runner, coverage runner, LSP, and DAP
integration tests also live in `src/Camp.Compiler.TestRunner`. Shared workflow
is documented in `docs/compiler-development-guide.md`.

## Coding Instructions

Keep behavior in the service that owns it. Use shared helpers such as
`CallableShapeService`, `ExpandedFormService`, `GeneratedDeclarationFactory`,
`DeclarationParticipation`, test discovery/result services, coverage services,
and target services instead of duplicating shape, visibility, participation, or
symbol logic. File-local invariants should be documented with source comments
near the relevant code. General compiler-writer rules live in `docs/semantics`.
