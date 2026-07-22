# First-Class Testing And Camp Source Coverage

## Status

Accepted and implementation-complete.

Active language guidance now lives in the language guide, compiler-writer
semantics live in the semantic supplements, and command-line/tooling behavior
lives in the compiler docs. This proposal is retained as the accepted design
record.

## Proposal Date

2026-07-20

## Last Updated Date

2026-07-21

## Summary

Add compiler-owned test discovery, test execution, and Camp-source coverage
without adding language keywords, macros, fluent assertion APIs, or runtime
reflection.

The core commands are:

```sh
campc test app.campbuild
campc cover app.campbuild
```

The same test apparatus supports two scenarios:

- **In-module tests:** compile the module under test in test mode. Tests may
  live beside production code, may use `@testonly` helpers, and may test
  internals.
- **External test modules:** compile a separate test project in test mode while
  referencing the production module as a shared library. These tests exercise
  the API exported by the production module.

Tests are top-level functions marked with `@test`. The built-in runner invokes
only tests shaped as `void name(thrown Assertion* assertion)`:

```camp
@test
void addReturnsSum(thrown Assertion* assertion)
{
	assert(add(2, 3) == 5);
}
```

Test helpers use `@testonly` when they live in a source file that also
participates in ordinary builds:

```camp
@testonly
internal int expectedSum() => 5;

@test
void addUsesFixtureValue(thrown Assertion* assertion)
{
	assert(add(2, 3) == expectedSum());
}
```

The standard test-support surface is intentionally small:

- `Assertion`;
- `assert(...)`;
- `fail(...)`.

Those APIs use the implemented source-capture defaults:

```camp
sourceof(parameterName)
caller(sourcefile)
caller(sourceline)
```

Coverage is reported against Camp source. Native generated-C coverage is not
the canonical coverage model.

## Compiler Implementation Requirement

Test discovery and harness generation require compiler-owned test-only
filtering independent of ordinary visibility:

- `@testonly` is valid only on top-level declarations;
- a top-level `@testonly` declaration may be private or `internal`;
- it may not be `public`, `export`, or export-projected;
- a test-only class, struct, or newtype owns its complete declaration subtree;
- ordinary builds omit the complete test-only subtree from lookup, metadata,
  API output, lowering, C emission, and native symbols;
- test and coverage builds include test-only declarations, but production
  declarations still may not depend on them.

The implementation must not make user tests or test helpers `public` merely so
a generated harness can call them.

## Goals

- Support simple same-file tests for small and self-contained projects.
- Support separate black-box test modules for shared-library APIs.
- Keep ordinary builds free of tests, test-only helpers, test metadata, harness
  symbols, and coverage symbols.
- Allow in-module tests to exercise internals without weakening ordinary API
  visibility.
- Allow external test modules to exercise only the surface a real consumer can
  use.
- Generate normal native test executables without runtime reflection.
- Use source-capture defaults for assertion message, file, and line.
- Report coverage against Camp source sequence points.
- Share manifest/result/coverage data across the CLI, LSP, debug adapter, CI,
  and LCOV reports.
- Keep source paths deterministic through the existing `caller(sourcefile)`
  path policy.

## Non-Goals

The initial feature does not include:

- new language keywords;
- a general macro or reflection system;
- implicit directory scanning for test files;
- test methods on classes, structs, newtypes, or out-of-scope static members;
- fixture lifecycle conventions;
- parameterized, data-driven, async, or generator tests;
- expected-failure tests;
- parallel execution;
- nonfatal assertion aggregation;
- branch, condition, region, or per-test coverage;
- native generated-C coverage as the canonical report;
- source-column capture or a source-location struct.

## Terms

- **Production module:** the Camp project being delivered as a shared library,
  executable, package, or other artifact.
- **Test module:** a Camp project compiled in test mode. It may be the
  production module itself or a separate project that references the production
  shared library.
- **In-module test run:** `campc test` or `campc cover` applied directly to the
  production module.
- **External test run:** `campc test` or `campc cover` applied to a separate
  test module that references the production module as an ordinary shared
  library dependency.
- **Coverage subject:** the module whose production source contributes to the
  production coverage denominator.
- **Instrumented production build:** a production-semantics build with coverage
  counters added. It does not include `@test`, `@testonly`, test-specific
  sources, or `TEST_MODULE`.
- **Production declaration:** a declaration present in an ordinary production
  build.
- **Test-only declaration:** a declaration present only in test and coverage
  builds because it is marked `@testonly`, marked `@test`, owned by a test-only
  type, or owned by a test-only declaration.
- **Test manifest:** compiler-emitted JSON describing discovered tests before
  execution.
- **Test results:** runtime JSON describing pass/fail/skip/invalid/error
  outcomes.
- **Coverage map:** compiler-emitted CSV mapping counter IDs to Camp source.
- **Coverage results:** runtime counter data merged with the coverage map.

## Test Scenarios

### In-Module Tests

An in-module test run applies test mode to the production module itself:

```sh
campc test app.campbuild
campc cover app.campbuild
```

The compiler builds a test variant of the module:

- production source files are included;
- any additional source files selected by the test request are included;
- `@test` functions are included and discovered;
- `@testonly` declarations are included;
- `TEST_MODULE` is defined;
- production declarations are still checked for illegal dependencies on
  test-only declarations;
- ordinary production API output is not widened;
- the final artifact is a generated test executable.

In-module tests are appropriate when tests need private or internal
implementation details, when tests live beside the code under test, or when the
project is small enough that a separate consumer-style test module would add
unnecessary ceremony.

For executable production modules, an in-module test run does not test the
command-line interface. The compiler builds the executable project's code as a
test variant and replaces the normal program entry point with the generated
test harness entry point. The production entry point remains ordinary code that
can be called by tests if its signature permits it, but it is not the native
process entry for the test artifact.

### External Test Modules

An external test run applies test mode only to a separate test module:

```sh
campc test app.integration-tests.campbuild
campc cover app.integration-tests.campbuild
```

The production module is consumed the same way a real downstream consumer would
consume it:

- the production module is built as a shared library;
- the test module imports the production module through its ordinary generated
  API header;
- the test harness links against or loads the ordinary production shared
  library;
- tests call the public/exported API exposed by that shared library.

The external test module gets the test apparatus: `@test`, `@testonly`,
`assert(...)`, `fail(...)`, manifests, results, and optional coverage. The
production dependency does not get test mode unless it is itself the module
under test in a separate in-module run.

External tests are appropriate when tests should preserve the production
module's integrity, exercise only public/exported behavior, or validate shared
library API and ABI boundaries.

## Language Surface

### Attribute Recognition

`test`, `testonly`, and `skip` are attribute names. They are not
reserved words.

These attributes may be written directly:

```camp
@test
void addReturnsSum(thrown Assertion* assertion)
{
	assert(add(2, 3) == 5);
}
```

They may also be written through documentation comments where doc-comment
commands lower to the same metadata:

```camp
/// Adds two values.
/// @test
void addReturnsSum(thrown Assertion* assertion)
{
	assert(add(2, 3) == 5);
}
```

Plain doc-comment text becomes `@summary` metadata. The built-in runner uses
`@summary` as the test description.

### `@test`

`@test` accepts no arguments.

It is valid only on a top-level function declaration with no visibility
modifier. A file-level namespace does not make the function scoped.

The compiler treats `@test` as structured metadata:

- validate that it appears on a top-level function;
- validate that the function has no visibility modifier;
- validate that it is not combined with `@testonly`;
- mark the function as test-only for emission;
- include the function in the test manifest.

The compiler does not reject a top-level `@test` function merely because its
signature is not invocable by the built-in runner. This preserves `@test` as
metadata that a custom runner may interpret differently.

The built-in runner invokes only tests with this source shape:

```camp
@test
void name(thrown Assertion* assertion)
{
}
```

The parameter name is not significant. The function must:

- have a body;
- return `void`;
- have no ordinary parameters;
- have exactly one trailing `thrown Assertion*` slot;
- not be extern;
- not be generic;
- not be async;
- not be an iterator.

A top-level `@test` function with any other signature is discovered and
reported as an invalid test result. It does not stop compilation and does not
prevent valid tests from running.

`@test` is invalid on:

- functions with any visibility modifier, including `internal`, `public`, and
  `export`;
- methods, constructors, destructors, accessors, and out-of-scope static member
  declarations;
- local functions and lambdas;
- declarations also marked `@testonly`.

Tests are not placed inside `@testonly` declarations. `@test` itself implies
test-only emission for the test function.

### `@testonly`

`@testonly` accepts no arguments.

It is valid only on top-level declarations. It may appear on:

- functions;
- variables;
- inline constants;
- aliases;
- classes;
- structs;
- interfaces;
- enums;
- newtypes, including callable newtypes.

A top-level `@testonly` declaration may have no visibility modifier or may be
`internal`. It may not be `public`, `export`, or export-projected.

It is invalid on:

- local declarations;
- parameters and generic parameters;
- fields, methods, constructors, destructors, accessors, or other members;
- enum values;
- namespace and `using` declarations;
- export projections;
- statements, blocks, and expressions.

Example:

```camp
@testonly
internal struct Fixture
{
	int expected;
	string text;
}
```

A test-only class, struct, or newtype makes its complete declaration subtree
test-only. This includes all members and all compiler-generated declarations
owned by the type: constructors, destructors, adapters, lifecycle helpers,
iterator state helpers, callable-newtype helpers, vtables, and metadata
fragments.

For other test-only top-level declarations, the declaration itself and all
compiler-generated declarations owned by it are test-only.

### `@skip`

`@skip` may appear only on a declaration also marked `@test`.

Direct attribute form:

```camp
@skip("requires parser fix")
@test
void futureParserScenario(thrown Assertion* assertion)
{
	assert(add(2, 2) == 4);
}
```

Doc-comment form:

```camp
/// @test
/// @skip requires parser fix
void futureParserScenario(thrown Assertion* assertion)
{
	assert(add(2, 2) == 4);
}
```

Skipped tests are present in the manifest and results. The harness does not
invoke them.

### `TEST_MODULE`

`campc test` and `campc cover` define `TEST_MODULE` while compiling the test
module.

`TEST_MODULE` is an escape hatch for rare test-only declarations that should not
even parse in production mode. It is not the primary test discovery mechanism,
and it should not be required merely to import ordinary test support.

## Standard Test Support

Test and coverage builds make the standard test-support surface available to
the test module and generated harness.

The minimal API is:

```camp
public struct Assertion
{
	escaped string message;
	escaped string sourcefile;
	uint sourceline;
}

public void assert(
	bool condition,
	escaped string message = sourceof(condition),
	escaped string sourcefile = caller(sourcefile),
	uint sourceline = caller(sourceline),
	thrown Assertion* assertion)
{
	if (!condition)
		fail(message, sourcefile, sourceline);
}

public void fail(
	escaped string message,
	escaped string sourcefile = caller(sourcefile),
	uint sourceline = caller(sourceline),
	thrown Assertion* assertion)
{
	Assertion* created = within (default) new Assertion();
	created.message = message;
	created.sourcefile = sourcefile;
	created.sourceline = sourceline;
	throw created;
}
```

The API intentionally does not include fluent assertion helpers. Libraries may
add their own helpers, but the compiler-provided standard surface is only
`Assertion`, `assert(...)`, and `fail(...)`.

Assertion wrappers must forward source-capture defaults explicitly if they want
reported locations to point at the wrapper's caller:

```camp
@testonly
void assertPositive(
	int value,
	escaped string message = sourceof(value),
	escaped string sourcefile = caller(sourcefile),
	uint sourceline = caller(sourceline),
	thrown Assertion* assertion)
{
	assert(value > 0, message, sourcefile, sourceline);
}
```

The standard test-support API may be public within its own package so the
compiler-generated harness can link to it. User tests and user test helpers are
never made public for harness access.

API headers and metadata for this standard support must preserve
`sourceof(parameterName)` and `caller(...)` defaults exactly as the implemented
source-capture proposal requires.

## Source Selection

The compiler must distinguish the ordinary production source selection from the
source selection used to compile a test module.

For in-module tests:

- production source selection is the ordinary build source selection;
- the test request may select additional source files explicitly;
- files selected only by the test request are absent from ordinary production
  requests;
- `@test` functions and `@testonly` helpers mark declarations that are test-only
  within the test module, regardless of which source file contains them.

For external test modules:

- the test module's own sources are compiled in test mode;
- the production dependency is compiled or imported in ordinary production mode
  for `campc test`;
- selected production coverage subjects are compiled in instrumented production
  mode for `campc cover`;
- tests in the external module see only declarations exposed by the production
  shared library the same way a normal shared-library consumer would.

There is no implicit test directory scan in the initial feature. Build files or
CLI options must select additional test source files explicitly.

Source selection uses the existing build-file source model and the existing
CLI `--include` / `--exclude` options. In `campc test` and `campc cover`, those
options apply to the test-mode source selection for the test module. In
`campc build` and `campc run`, they retain their ordinary production meaning.
Durable test-source configuration should live in the test module's `.campbuild`
file or in the command used for the test/coverage run.

## Dependency And Visibility Rules

Allowed dependencies:

- a test declaration may depend on production declarations;
- a test declaration may depend on test-only declarations;
- a test-only helper may depend on production declarations;
- a test-only helper may depend on other test-only declarations;
- an external test module may depend on the public/exported surface of the
  production shared library it consumes.

Rejected dependencies:

- a production declaration may not depend on an `@test` declaration;
- a production declaration may not depend on an explicit `@testonly`
  declaration;
- exported or public API surfaces may not expose test-only types, values,
  callable shapes, default values, thrown types, or metadata;
- an external test module may not bypass the production shared library's
  ordinary visibility rules.

These rules apply even during test and coverage builds. Test mode adds test
code; it does not redefine the production API.

## Compiler Pipeline

The implementation should follow this order:

1. Parse sources and doc comments.
2. Lower doc-comment commands to metadata attributes.
3. Classify the build as production, test, or coverage.
4. Apply source selection for the selected scenario.
5. Classify `@test`, `@testonly`, `@skip`, and `@summary`.
6. Compute recursive test-only ownership.
7. Bind declarations and bodies.
8. Reject production-to-test-only dependencies.
9. Build test discovery records from top-level `@test` functions.
10. For test/coverage builds, generate private test thunks where needed.
11. For coverage builds, insert Camp-source coverage counters.
12. Lower callables, lifetimes, cleanup, generated helpers, and C/native output.
13. Generate the harness executable.
14. Run the harness unless the command is discovery-only.
15. Merge test results and coverage results.

Default argument insertion for `assert(...)` and `fail(...)` must happen using
the existing source-capture implementation before callable lowering rewrites
arguments.

## Test Manifest

The compiler emits one manifest per test module before running tests.

Required JSON shape:

```json
{
  "format": "camp.test-manifest",
  "version": 1,
  "mode": "in-module",
  "tests": [
    {
      "id": "MathTests::addReturnsSum",
      "name": "addReturnsSum",
      "qualifiedName": "MathTests::addReturnsSum",
      "sourcefile": "tests/math.camp",
      "sourceline": 8,
      "summary": "Adds two values.",
      "skipped": false,
      "skipReason": null,
      "runnerSignature": "valid"
    }
  ]
}
```

For external test modules, `mode` is `"external"`.

Test IDs use the visible source qualified name. Generated helper names, native
symbols, and thunk names are not part of user-facing IDs.

The manifest is the source of truth for CLI listing, filtering, LSP CodeLens,
test explorers, and debug launch selection.

## Harness Generation

The compiler generates a native test executable for every test or coverage run.

The harness contains:

- the selected test module code;
- a generated native entry point;
- a static table derived from the test manifest;
- test-support runtime initialization;
- result writing;
- coverage runtime initialization when coverage is enabled.

For each manifest entry:

- if skipped, record `skipped` without invoking the test;
- if the built-in runner signature is invalid, record `invalid` without
  invoking the test;
- otherwise call the test and catch `Assertion*`;
- if no assertion is thrown, record `passed`;
- if `Assertion*` is thrown, record `failed`;
- if a non-assertion failure can be observed by the runtime, record `error`.

Conceptually:

```camp
Assertion* failure = default;
addReturnsSum(catch failure);

if (failure != default)
	recordFailed(failure);
else
	recordPassed();
```

Private in-module tests remain private source declarations. The compiler may
generate same-file test thunks so the harness can invoke private tests without
widening user visibility. Thunks are test-only and never appear in ordinary
builds, ordinary metadata, ordinary API headers, or production native exports.

For executable production modules in an in-module test run, the harness entry
point replaces the production executable entry point in the test artifact.

## Test Runner

`campc test` performs these operations:

1. Build the test module in test mode.
2. Emit the test manifest.
3. Generate and build the test harness executable.
4. If `--list` is supplied, print/list manifest entries and stop.
5. Apply filters to the manifest.
6. Run the harness for selected tests.
7. Write test results JSON.
8. Print human-readable output unless disabled.
9. Return exit code `0` only when every selected test passed or was skipped.

Required options:

- `--list`;
- `--filter <pattern>`;
- `--test-output-dir <path>`;
- `--test-result-format text|json|text,json`;
- `--sourcefile-paths relative|absolute`;
- repeatable `--sourcefile-root <path>`.

`--list` and `--filter` are test-runner options. They are valid only for
`campc test` and `campc cover`.

`--test-output-dir` and `--test-result-format` are accepted by `campc build`,
`campc run`, `campc test`, and `campc cover`. `campc build` and `campc run`
ignore them. `campc test` and `campc cover` use them when writing test result
artifacts. `--test-output-dir` selects the directory where the test manifest
and test results are written.

`--sourcefile-paths` and `--sourcefile-root` are accepted and applied by
`campc build`, `campc run`, `campc test`, and `campc cover`.

`--filter` matches canonical ID, qualified name, and simple name.

Filter patterns use a deliberately small wildcard format:

- `*` matches zero or more characters;
- `?` matches exactly one character;
- `^` matches exactly one ASCII uppercase character, `A` through `Z`;
- matching is case-sensitive;
- matching is ordinal, not culture-sensitive;
- no character classes, alternation, escaping, or regular-expression syntax;
- a pattern with no wildcard characters must match the complete ID, qualified
  name, or simple name exactly.

Examples:

- `Json*` matches names that start with `Json`;
- `*Writer*` matches names containing `Writer`;
- `MathTests::addReturns?um` matches one character at `?`;
- `parse^alue` matches `parseValue`, but not `parse_value` or `parsevalue`;
- `parser` matches exactly `parser`;
- `*parser*` matches any ID/name containing `parser`.

Multiple `--filter` values are ORed together.

`--sourcefile-paths` and `--sourcefile-root` use the existing
`caller(sourcefile)` path mapper. The same mapped paths must appear in
assertions, manifests, test results, coverage maps, coverage results, LSP data,
and LCOV projections.

Test failures, invalid tests, and observable runtime errors are command
failures after results are written. Compile diagnostics, native build failures,
and infrastructure failures are also command failures.

## Test Results

Required JSON shape:

```json
{
  "format": "camp.test-results",
  "version": 1,
  "summary": {
    "passed": 1,
    "failed": 0,
    "skipped": 0,
    "invalid": 0,
    "error": 0,
    "total": 1
  },
  "tests": [
    {
      "id": "MathTests::addReturnsSum",
      "name": "addReturnsSum",
      "qualifiedName": "MathTests::addReturnsSum",
      "sourcefile": "tests/math.camp",
      "sourceline": 8,
      "summary": "Adds two values.",
      "outcome": "passed",
      "durationMs": 1.2,
      "failure": null
    }
  ]
}
```

Assertion failure:

```json
{
  "id": "MathTests::addReturnsSum",
  "outcome": "failed",
  "failure": {
    "kind": "assertion",
    "message": "sum should match",
    "sourcefile": "tests/math.camp",
    "sourceline": 10
  }
}
```

Invalid built-in signature:

```json
{
  "id": "MathTests::missingAssertionSlot",
  "outcome": "invalid",
  "failure": {
    "kind": "invalid-test-signature",
    "message": "built-in tests must have the signature void name(thrown Assertion*)",
    "sourcefile": "tests/math.camp",
    "sourceline": 18
  }
}
```

Text output should be a projection of this JSON. Tools should consume JSON, not
terminal text.

## Coverage Runner

`campc cover` performs the same work as `campc test` with coverage
instrumentation enabled:

1. Build the test module in coverage mode.
2. Select coverage subjects.
3. Build each coverage subject with production semantics and coverage
   instrumentation.
4. Emit the test manifest.
5. Emit coverage maps for instrumented subjects.
6. Generate and build the harness executable.
7. Run selected tests.
8. Write test results.
9. Merge runtime counters with coverage maps.
10. Write coverage results.
11. Produce requested JSON and LCOV outputs.

Required options:

- every applicable `campc test` option;
- `--coverage-format json|lcov|json,lcov`;
- `--coverage-output-dir <path>`;
- repeatable `--coverage-subject <name|self>`.

`--coverage-format` accepts only `json`, `lcov`, or both as `json,lcov`.
`--coverage-output-dir` selects the directory where coverage maps and coverage
result artifacts are written.

### Coverage Subject Set

Coverage subjects are modules whose production code is instrumented and counted
in the production coverage denominator.

For in-module coverage, the default coverage subject is `self`: the production
module's production code compiled with coverage instrumentation.

For external test-module coverage, the preferred coverage subject is the
production shared-library dependency being tested. The dependency is compiled
in instrumented production mode:

- no `@test` functions;
- no `@testonly` declarations;
- no `TEST_MODULE`;
- no widened visibility;
- same API/import/export surface as an ordinary production build;
- coverage counters and coverage runtime added.

This preserves the integrity of black-box tests while still measuring which
production source was executed.

Selection rules:

- `--coverage-subject self` instruments the test module's own production
  declarations;
- `--coverage-subject <name>` instruments a named shared-library dependency;
- in-module coverage defaults to `self`;
- external coverage defaults to the single non-test-support shared-library
  dependency when there is exactly one;
- external coverage with zero or multiple possible shared-library dependencies
  requires `--coverage-subject`.

For a shared-library coverage subject, the harness links or loads the
instrumented shared library.

Default coverage excludes:

- `@test` functions;
- `@testonly` declarations;
- generated harness code;
- compiler-generated adapters, vtables, lifecycle helpers, lambda helpers,
  iterator state helpers, async continuations, cleanup paths, and other helper
  code without direct source execution identity;
- other package and project dependencies unless explicitly selected.

### Instrumentation Rules

Counters are inserted against Camp source sequence points before destructive
lowering.

The initial metrics are:

- function-entry coverage;
- executable-line coverage.

Instrumentation must not:

- change evaluation order;
- evaluate expressions more times than the original program;
- change short-circuit behavior;
- introduce user-visible allocation or thrown flow;
- change lifetimes, cleanup, constness, `within`, or `thrown` flow;
- turn generated helpers into source coverage subjects.

Non-executable lines do not enter the denominator.

### Coverage Map CSV

Coverage maps are canonical CSV, not JSON. These files can be very large, so
the format must avoid repeated paths and repeated function names.

Rules:

- UTF-8 text;
- LF line endings;
- RFC 4180 CSV escaping for fields that need quotes;
- no header row;
- row kind is the first field;
- numeric IDs are unsigned decimal integers;
- counter kind is `l` for executable-line counter or `f` for function-entry
  counter.

Rows:

```text
v,<version>
p,<file-id>,<mapped-sourcefile>
n,<name-id>,<qualified-function-name>
c,<counter-id>,<kind>,<file-id>,<line>,<name-id>
```

Example:

```csv
v,1
p,1,src/math.camp
n,1,Math::add
c,12,l,1,4,1
c,13,f,1,3,1
```

Meaning:

- `v` declares the coverage-map format version;
- `p` declares a source file path once;
- `n` declares a function name once;
- `c` maps one runtime counter to source.

The runtime counter data uses counter IDs from this map. The coverage runner
loads the CSV map, merges runtime counts, and writes coverage results JSON.

### Coverage Results JSON

Coverage results remain JSON because they are aggregated and consumed by tools:

```json
{
  "format": "camp.coverage-results",
  "version": 1,
  "summary": {
    "line": { "covered": 42, "total": 50, "percent": 84.0 },
    "function": { "covered": 8, "total": 10, "percent": 80.0 }
  },
  "files": [
    {
      "path": "src/math.camp",
      "line": { "covered": 3, "total": 4, "percent": 75.0 },
      "uncoveredLines": [9]
    }
  ]
}
```

LCOV is a projection of the coverage results.

## Build Outputs And Caching

Test and coverage builds emit distinct manifest, harness, result, coverage map,
coverage runtime, and coverage result artifacts. The current implementation uses
the normal artifact directory naming for the selected target/profile/output
settings. Build scripts that need to isolate production, test, and coverage
generated C/native outputs should use separate `--out-dir` values.

Representative outputs:

```text
bin/<target>_test_<profile>/
bin/<target>_coverage_<profile>/
<project>.camp-test-manifest.json
<project>.camp-coverage-map.csv
<project>.camp-test-results.json
<project>.camp-coverage-results.json
lcov.info
```

The exact directory layout should follow existing compiler conventions.

## Metadata And API Boundaries

Ordinary generated Camp API headers and ordinary metadata never contain:

- user `@test`;
- user `@testonly`;
- user `@skip`;
- user test functions;
- user test-only helper declarations;
- test thunks;
- generated harness symbols;
- coverage runtime symbols.

The dedicated test manifest JSON, test results JSON, coverage map CSV, and
coverage results JSON artifacts are the machine-readable testing API.

The standard test-support package is compiler-owned runtime support. Its API
may preserve public declarations for `Assertion`, `assert(...)`, and
`fail(...)`, including structured source-capture metadata for default
parameters.

## LSP, Debugger, And Syntax Highlighting

The LSP must consume the same semantic discovery service and machine-readable
artifacts as the CLI.

Required LSP behavior:

- diagnostics for invalid source forms of `@test`, `@testonly`, `@skip`,
  `caller(...)`, and `sourceof(...)`;
- CodeLens or equivalent run/debug affordances beside top-level `@test`
  functions;
- test explorer discovery from the manifest model;
- run/debug by canonical test ID;
- result diagnostics for assertion failures and invalid built-in signatures;
- coverage decorations from coverage results;
- navigation using mapped source paths.

The debug adapter launches the generated harness executable with a selected
test filter. Breakpoints bind to Camp source through existing debug metadata.

Syntax highlighting files should recognize `@test`, `@testonly`, `@skip`,
`assert`, `fail`, `caller`, and `sourceof` without treating them as reserved
keywords.

## Diagnostics

Compiler diagnostics:

- `@test` on anything other than a top-level function;
- `@test` with arguments;
- `@test` with any visibility modifier;
- `@test` combined with `@testonly`;
- `@test` on methods, constructors, destructors, accessors, out-of-scope static
  members, local functions, or lambdas;
- `@testonly` outside a supported top-level declaration;
- `@testonly` with arguments;
- `@testonly` with `public`, `export`, or export projection;
- `@skip` without `@test`;
- malformed `@skip`;
- production declarations depending on test-only declarations;
- public/exported API exposing test-only declarations;
- malformed CLI options;
- unsupported coverage target;
- coverage result write failures.

Invalid built-in test results, not compiler diagnostics:

- `@test` function without a body;
- non-`void` result;
- ordinary parameters;
- missing `thrown Assertion*`;
- wrong thrown type;
- additional thrown slots;
- extern function;
- generic function;
- async function;
- iterator function.

Assertion failures are runtime test results, not compiler diagnostics.

## Documentation Updates

Language guide:

- add a small testing subsection to the attributes chapter;
- show the standard test shape;
- show `assert(...)` and `fail(...)`;
- explain `@testonly` for top-level helpers;
- mention in-module tests and external test modules briefly;
- keep harness and coverage internals out of the language guide.

Compiler semantics:

- document the two scenarios;
- document source selection and dependency modes;
- document attribute validation;
- document recursive test-only ownership;
- document test manifest JSON, test results JSON, coverage map CSV, and
  coverage results JSON;
- document harness generation and executable entry-point replacement;
- document test runner and coverage runner ordering;
- document instrumented production builds for external coverage subjects;
- document coverage subject selection and instrumentation rules.

LLM guide:

- add one compact correct test example;
- say built-in tests are top-level functions with `thrown Assertion*`;
- say tests use `assert(...)` and `fail(...)`;
- say `@testonly` is only for separate top-level helpers and helper types;
- mention external test modules for shared-library API tests.

Compiler command docs:

- document `campc test`;
- document `campc cover`;
- document source selection behavior for test and coverage modes;
- document name filters;
- document sourcefile path options;
- document test and coverage result directories and formats.

## Implementation Test Coverage

Use the minimum number of additional tests that covers each semantic boundary.
Prefer existing golden and driver test suites.

Required tests:

- valid top-level `@test` with `thrown Assertion*`;
- invalid built-in results for wrong signatures;
- rejection of method/static/local/lambda tests;
- rejection of `@test` with any visibility modifier;
- rejection of `@test` combined with `@testonly`;
- private and `internal` top-level `@testonly`;
- rejection of public/export `@testonly`;
- recursive test-only ownership for class, struct, and newtype;
- production-to-test-only dependency rejection in ordinary and test builds;
- same-file private test thunk without API leakage;
- in-module test run for a shared/static/executable project;
- external test module consuming a shared-library API;
- external coverage run against an instrumented shared-library subject;
- `--coverage-subject` selection and ambiguity diagnostics;
- executable in-module test run replaces the production entry point with the
  harness entry point;
- `@summary`, doc-comment summary, and `@skip` in manifest/results;
- `assert(...)` and `fail(...)` source capture for message, file, and line;
- source-capture metadata/API persistence for standard test support;
- CLI parsing for `test` and `cover`;
- filtering by ID/name;
- sourcefile path roots in assertions, manifests, results, and coverage;
- deterministic path-root tests with POSIX and Windows-style paths;
- coverage function-entry and executable-line counters;
- exclusion of tests, test-only declarations, dependencies, and generated
  helpers from default production coverage;
- JSON and LCOV projections from one coverage result;
- LSP semantic discovery, run/debug actions, result diagnostics, and coverage
  decorations;
- syntax highlighting for new attributes and standard support names.

## Acceptance Criteria

The feature is complete when:

- `campc test` supports in-module and external test-module scenarios;
- `campc cover` supports in-module and external test-module scenarios;
- external coverage can instrument selected shared-library dependencies without
  enabling test mode for those dependencies;
- executable projects can be tested in-module without using the production
  entry point as the harness entry point;
- ordinary builds omit all user tests, user test-only declarations, harness
  artifacts, test metadata, and coverage artifacts;
- `@test` is valid only on top-level functions with no visibility modifier;
- the built-in runner invokes only `void name(thrown Assertion*)` tests;
- nonmatching top-level `@test` signatures are invalid test results and do not
  prevent valid tests from running;
- `@testonly` is top-level-only, private or `internal`, and recursive for
  class/struct/newtype declarations;
- production declarations cannot depend on test-only declarations in any mode;
- external test modules cannot bypass ordinary shared-library dependency
  visibility;
- instrumented production coverage subjects preserve ordinary production API
  and visibility semantics;
- `Assertion`, `assert(...)`, and `fail(...)` are available in test/coverage
  builds and use source-capture defaults;
- manifests, results, coverage maps, coverage results, diagnostics, LCOV, and
  LSP navigation use the shared sourcefile path policy;
- coverage reports Camp function-entry and executable-line coverage;
- tests, test-only declarations, dependencies, and generated helpers are
  excluded from default production coverage;
- docs, LSP, syntax highlighting, and tests are updated.

## Resolved Decisions

- Tests are top-level functions, not static methods or scoped member tests.
- `@test` is metadata; the built-in runner's signature rule is a runner rule.
- The built-in runner invokes `void name(thrown Assertion*)`.
- Invalid built-in signatures are invalid test results, not compiler-stopping
  diagnostics.
- Tests use `assert(...)` and `fail(...)`.
- The standard support package does not provide fluent assertion APIs.
- Tests are not placed inside `@testonly` declarations.
- `@test` implies test-only emission for the test function.
- `@test` functions have no visibility modifier.
- `@testonly` is for separate top-level helper declarations and helper types.
- `@testonly` may be private or `internal`, never `public` or `export`.
- A test-only class, struct, or newtype owns its complete declaration subtree.
- `@summary` supplies test descriptions.
- `TEST_MODULE` is an escape hatch, not the discovery model.
- In-module tests test internals by compiling the production module in test
  mode.
- External test modules test public behavior by referencing the production
  module as a shared library and calling its exported API.
- External coverage uses instrumented production builds of selected coverage
  subjects, not test-mode builds of those subjects.
- Executable in-module tests replace the entry point with the harness.
- Coverage is Camp-source coverage, not generated-C coverage.
