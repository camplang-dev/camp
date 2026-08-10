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

Machine-specific notes and maintainer scripts belong outside of the repository.
Never add, stage, or commit those to the repository.

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
scripts/run-tests.sh
dotnet msbuild src/publish-tools.proj -p:RuntimeIdentifier=osx-x64
dotnet msbuild src/test-fast.proj
dotnet msbuild src/test-fast.proj -p:NoBuild=true
dotnet msbuild src/coverage.proj
```

`scripts/run-tests.sh` is the canonical local full-suite command. On macOS it
automatically runs the suite in stable sections, including one `StdRun` golden
case per VSTest invocation, because long all-in-one macOS VSTest processes can
accumulate native build/run state and hang during the heaviest runtime golden
coverage. On other hosts it defaults to a single full VSTest invocation. Use
`scripts/run-tests.sh full` to force one invocation or
`scripts/run-tests.sh sectioned` to force sectioned execution.

Each VSTest invocation has a timeout controlled by
`CAMP_TEST_TIMEOUT_SECONDS` and writes macOS process snapshots/samples under
`tmp/test-hang-dumps` if a timeout occurs. Golden tests also print the active
case name before execution so a future hang identifies the current fixture.

`src/publish-tools.proj` produces the user-facing tools in
`bin/publish/<rid>/`: `campc`, `camp-lsp`, and `camp-dap` with `.exe` on
Windows. It does not replace the normal repository `bin/` development tools.

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
layout cannot be inferred from the tool's own path.

## Release Request Protocol

When the maintainer asks for a release, do not immediately tag or publish.
Prepare the release in two explicit steps.

First, inspect the repository state and recent history since the previous
release tag. Recommend a version number using the user-facing significance of
the change set. During the `v0` preview stage, do not choose the version number
based on whether a change is breaking. The language and standard library are
expected to make breaking changes before `v1`; version numbers communicate the
scale and importance of the user-facing change instead.

- Use a new minor version, such as `v0.10.0-preview.1`, for significant language
  changes, such as a substantial new language feature, a redesign of an existing
  feature, or a significant standard-library addition.
- Use a patch version, such as `v0.9.2-preview.1`, for smaller user-facing
  changes and important bug fixes.
- Use another preview on the same version, such as `v0.9.1-preview.2`, for small
  bug fixes, packaging fixes, backend fixes, or internal polish that is not
  meaningfully user-facing.

Then present the maintainer with:

- the recommended version tag;
- the current branch and commit that would be released;
- a user-facing change summary grouped into features, fixes, tooling/editor
  changes, documentation, and breaking or compatibility notes where relevant;
- a short list of changes intentionally omitted from release notes, such as
  proposal bookkeeping, implementation-plan movement, contributor-instruction
  edits, internal refactors, and test-only maintenance;
- the expected release artifacts and platforms;
- the validation plan before publishing.

Stop there and wait for explicit maintainer approval.

After approval, create or update `docs/releases/<version>.md` with curated
release notes. Release notes are product-facing documentation: describe what a
Camp user can do now, what changed in behavior, what was fixed, and any known
limitations that affect use. Do not include management details that are useful
only to compiler maintainers. The file should start with the body text, not a
Markdown heading, because GitHub renders the release title separately.

Commit the release notes before publishing. Push `master` to GitHub, then run
the GitHub Actions workflow named `release-build` with the intended version.
This is the rehearsal workflow. It builds the release matrix, runs targeted
release-gate tests where enabled, packages the archives, smoke-tests the
archives, and uploads workflow artifacts. It does not create a tag or GitHub
Release.

If the rehearsal fails because of a transient infrastructure or known flaky test
failure, rerun only the failed job first. If it fails from a real release issue,
fix the issue, commit it, push it, and run the rehearsal again. Do not proceed to
publishing until the rehearsal has passed or the maintainer explicitly accepts a
documented exception.

After the rehearsal passes, run the GitHub Actions workflow named
`release-publish` from `master`:

- enter the approved version;
- enable the `publish` confirmation checkbox.

Do not create or push the release tag locally. The publish workflow validates the
version and release notes, verifies that an existing tag does not point at a
different commit, builds and smoke-tests the full release matrix, creates the tag
only after the matrix passes, uploads the GitHub Release assets, verifies
checksums, and runs installer checks on Linux, macOS, and Windows against the
published release assets. The release tag is also the canonical history marker
for the released commit; use the same `vX.Y.Z-preview.N` spelling as the GitHub
Release.

Camp preview releases should be normal GitHub Releases, not GitHub prereleases.
The Camp version string carries the preview label. This keeps GitHub's
repository sidebar and `/releases/latest` endpoint pointed at the latest Camp
preview release, which is the behavior users expect on the repository homepage.
Use the GitHub prerelease flag only if the maintainer explicitly asks for a
release that should not be treated as the latest public Camp release.

Watch the publish workflow through completion. Confirm that the release page has
the expected archives, per-archive `.sha256` files, and `checksums.txt`. If the
workflow has to be rerun after the tag has been created, rerun failed jobs
against the same commit and tag. Do not move a published tag unless the
maintainer explicitly approves that repair.

After the GitHub publish workflow has created the canonical release tag, sync
that tag into the local development `origin` used by local and remote test
clones. Fetch the tag from `github`, then push that existing tag to `origin`.
Do not invent, recreate, or move the tag locally.

## Targeted Test Workflow

When the test assembly is already built, prefer targeted `dotnet vstest` runs:

```sh
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net10.0/Camp.Compiler.TestRunner.dll
```

Golden discovery supports `CAMP_TEST_KIND` and `CAMP_TEST_CASE` for focused
runs.

## Commit Gate

Before commits that change compiler behavior, the full non-skipped suite should
pass at least once after the final change. Prefer `scripts/run-tests.sh` for
that gate so macOS uses the stable sectioned path automatically. For this
documentation rewrite, unit tests are not required unless a later change
explicitly asks for them.

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
