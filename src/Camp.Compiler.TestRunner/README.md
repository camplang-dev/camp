# Camp.Compiler.TestRunner

`Camp.Compiler.TestRunner` contains Camp compiler tests.

## Setup

Build from the repository root:

```sh
dotnet build src/camplang.sln
```

## Usage

Run the full test project:

```sh
dotnet test src/camplang.sln
```

Run the built test assembly directly:

```sh
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll
```

## Testing

Golden tests use files under `tests`. Semantic tests live in
`SemanticTests.cs`. LSP tests launch `camp-lsp` over stdio. Targeted golden
runs use `CAMP_TEST_KIND` and `CAMP_TEST_CASE`.

## Coding Instructions

Prefer semantic unit tests for small compiler facts and golden tests for exact
emitted text or phase output. There is no automatic bless mode; inspect actual
files before updating expected files. See `docs/compiler-development-guide.md`
for the shared workflow.
