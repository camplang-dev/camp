# Camp.Compiler.TestRunner

`Camp.Compiler.TestRunner` contains Camp compiler tests.

## Setup

Build from the repository root:

```sh
dotnet build src/camplang.sln
```

This builds the compiler in Debug configuration without replacing `bin/campc`.
Tests that launch `campc` use the Release compiler in `bin/` by default. To
test a Debug compiler explicitly, set `CAMP_TEST_CAMPC` to
`src/campc/bin/Debug/net10.0/campc`; to refresh the shared compiler, run
`dotnet build src/campc/campc.csproj -c Release`.

## Usage

Run the full test project:

```sh
dotnet test src/camplang.sln
```

Run the built test assembly directly:

```sh
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net10.0/Camp.Compiler.TestRunner.dll
```

## Testing

Golden tests use files under `tests`. Semantic tests live in
`SemanticTests.cs`. CLI, test runner, coverage runner, LSP, and DAP integration
tests live in focused xUnit classes under this project. Targeted golden runs
use `CAMP_TEST_KIND` and `CAMP_TEST_CASE`.

## Coding Instructions

Prefer semantic unit tests for small compiler facts, golden tests for exact
emitted text or phase output, and command-line integration tests for harness,
result, coverage, LSP, and DAP behavior. There is no automatic bless mode;
inspect actual files before updating expected files. See
`docs/compiler-development-guide.md` for the shared workflow.
