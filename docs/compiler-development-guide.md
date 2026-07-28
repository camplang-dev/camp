# Compiler Development Guide

## Repository Layout

The repository is organized around the compiler, command-line driver, language
server, standard library, targets, tests, docs, archived source docs, and
scratch output:

- `src/Camp.Compiler`: compiler library, parser, analyzer, lowering, emission,
  metadata, project loading, target loading, and language-service APIs.
- `src/campc`: command-line compiler.
- `src/camp-lsp`: language server.
- `src/Camp.Compiler.TestRunner`: golden, semantic, CLI, project loader, target,
  test runner, coverage runner, LSP, and DAP tests.
- `src/Camp.Compiler.Coverage`: coverage report driver for the C# compiler test
  suite, separate from Camp source coverage produced by `campc cover`.
- `lib`: bundled Camp declarations and standard library sources.
- `targets`: target metadata.
- `tests`: golden input and expected-output cases.
- `docs`: canonical documentation produced by the rewrite.
- `archive/docs`: archived documentation used as rewrite source material.
- `tmp`: scratch output.
- `local`: untracked machine-specific notes for local agents.

## C# Solution Projects

Build the solution from the repository root:

```sh
dotnet build src/camplang.sln
```

Each C# project folder should keep a local `README.md` with basic usage, setup,
testing, and coding instructions. Project READMEs should link to shared docs
rather than duplicating broad compiler guidance.

## Compiler Pipeline Orientation

Compiler work usually flows through tokenization, parsing, bindable-node
building, declaration analysis, body analysis, expansion, lowering, emission,
and optional native build or metadata serialization. Keep changes close to the
existing pass boundary and shared helper service that owns the behavior.

## Documentation Layout And Update Rules

Language-facing behavior belongs in `docs/language`. Compiler tooling belongs
in `docs/compiler`. Compiler-writer semantics belong in `docs/semantics`.
Repository workflow belongs here. LLM coding guidance belongs in
`docs/camp-llm-coding-guide.md`.

Semantic compiler changes should update the relevant docs and tests in the same
change.

## Proposal Lifecycle

New proposal drafts live directly in `docs/proposals/` with a descriptive,
unnumbered filename. Do not assign a proposal number, put a new draft in
`docs/proposals/pending`, stage it, or commit it. Keep the draft uncommitted
until the maintainer explicitly says it is ready to become pending and be
committed.

When the maintainer approves that transition, assign the proposal number, move
the file to `docs/proposals/pending`, and commit it as directed. Rejected
proposals live in `docs/proposals/rejected` and should not be updated after
rejection except for mechanical archive changes. New accepted proposals live
in `docs/proposals/accepted` as historical files after their details are
integrated into canonical docs.

Implementation plans are not proposals. Keep implementation plans outside
`docs/proposals/accepted`, even when the work is complete. If a plan was written
for a proposal, the proposal may move through the proposal lifecycle, but the
plan itself should remain uncommitted unless the maintainer explicitly asks for
it to be committed somewhere else.

## Working In A Dirty Tree

Assume unrelated changes belong to someone else. Do not revert them. If a file
you need already has unrelated changes, work with the current content and keep
your edit scoped.

## Using `tmp/`

Use `tmp/` for generated traces, smoke-test projects, docs-example builds,
coverage reports, rendered docs, and other scratch output. Do not commit `tmp/`
files. If a scratch note becomes long-term documentation, rewrite it into a
curated docs file.

## Build Commands

Common commands:

```sh
dotnet build src/camplang.sln
dotnet msbuild src/publish-tools.proj -p:RuntimeIdentifier=osx-x64
local/package-release.sh --version v0.1.0-preview.1 --rid osx-x64
dotnet msbuild src/test-fast.proj
dotnet msbuild src/test-fast.proj -p:NoBuild=true
dotnet msbuild src/coverage.proj
```

`src/publish-tools.proj` produces the user-facing tools in
`bin/publish/<rid>/`: `campc`, `camp-lsp`, and `camp-dap` with `.exe` on
Windows. It does not replace the normal repository `bin/` development tools.
`local/package-release.sh` and `local/package-release.ps1` turn that publish
output into the installable archive layout used by GitHub Releases.

To test published tools with the existing integration tests, point the test
runner at those artifacts:

```sh
CAMP_TEST_CAMPC=bin/publish/osx-x64/campc \
CAMP_TEST_LSP=bin/publish/osx-x64/camp-lsp \
CAMP_TEST_DAP=bin/publish/osx-x64/camp-dap \
dotnet test src/Camp.Compiler.TestRunner/Camp.Compiler.TestRunner.csproj
```

For installed-layout testing, the tools expect an installation root containing
`bin`, `lib`, `targets`, and `cache`. Set `CAMP_HOME` to that root when the
layout cannot be inferred from the tool's own path. Use
`local/test-release-archive.sh` or `local/test-release-archive.ps1` to smoke-test
an archive before publishing it.

## Targeted Test Workflow

When the test assembly is already built, prefer targeted `dotnet vstest` runs:

```sh
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net10.0/Camp.Compiler.TestRunner.dll
```

Golden discovery supports `CAMP_TEST_KIND` and `CAMP_TEST_CASE` for focused
runs.

## Commit Gate

Before commits that change compiler behavior, the full non-skipped suite should
pass at least once after the final change. For this documentation rewrite, unit
tests are not required unless a later change explicitly asks for them.

## Golden Tests

Golden tests live under `tests`. Each `.camp` file has a committed expected
output. Test runs write actual files first. When compiler output intentionally
changes, inspect actual output and manually update expected files.

## Semantic Unit Tests

Use semantic unit tests when the behavior is a small compiler fact such as a
callable shape, generated helper symbol, lifetime fact, metadata decision, or
target capability.

## LSP Tests

LSP tests launch `camp-lsp` over stdio and cover initialization, diagnostics,
hover, completion, definition behavior, and test CodeLens behavior. LSP changes
should usually run the LSP-focused tests before full validation.

## Coverage

Coverage output is generated under `tmp/coverage-report` by
`dotnet msbuild src/coverage.proj`. The underlying VSTest collector output is
written under `tmp/dotnet-test-results`.

## Updating Expected Files

There is no automatic bless mode. Inspect actual files and copy or merge
changes into expected files manually.

## Diagnostics Expectations

Diagnostics should have tight source ranges, useful messages, stable severity,
and stable codes where tests or tooling depend on them. LSP tests may fail from
range-only changes.

## Code Style

Follow the existing file's style. Prefer shared compiler services over local
string manipulation or one-off shape logic. Add comments only where they clarify
non-obvious invariants.

## Source Code Comments As Local Instructions

The preferred place for coding instructions about a specific compiler file,
type, method, or invariant is a source comment near that code. General
multi-project guidance belongs in docs. It is acceptable to add source comments
as part of documentation work when the instruction belongs next to the
implementation.

## Per-Project READMEs

Each C# project README should explain what the project is, how to build or run
it, which tests are relevant, and what coding conventions are project-specific.
Use links back to this guide and the semantic supplements for shared rules.

## Before Commit Checklist

- The change is scoped to the requested behavior.
- Relevant docs are updated.
- Relevant expected files are inspected when outputs change.
- Confirmed compiler bugs are logged in `src/campc/OutstandingBugs.md`.
- Documentation uncertainties are logged in `docs/OutstandingIssues.md`.
- No private machine-specific paths or notes are committed.
