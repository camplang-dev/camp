# Standard Library Camp Test Migration

## Status

Accepted.

## Proposal Date

2026-08-28

## Last Updated Date

2026-08-28

## Summary

Migrate standard library behavior tests into Camp source tests using `@test`.

The standard library test suite should live with the standard library, compile
the standard library source and test source in the same in-module test build,
and run through the existing `campc test` harness. The C# xUnit test runner
remains the outer orchestration layer, but xUnit should project individual Camp
`@test` functions as individual xUnit test cases by reading Camp test manifests.

Existing compiler tests are not removed as part of the initial migration.
Runtime behavior tests that are demonstrably replaced by stdlib `@test`
coverage may be skipped or disabled later, with a migration ledger recording the
replacement. Compiler-shape tests for diagnostics, lowering, C emission, API
headers, metadata, tooling, and command behavior remain active.

## Motivation

Camp now has a first-class source test system:

- top-level `@test` functions are discovered by the compiler;
- `campc test` builds and runs a native test harness;
- `assert(...)` and `fail(...)` produce structured assertion failures;
- test results are emitted as `camp.test-results` JSON;
- `campc cover` can measure production Camp source coverage;
- runner-supplied `within Allocator* allocator` parameters enable leak
  detection.

The standard library should use that system for its own behavior tests. Today,
most stdlib runtime behavior coverage lives in executable golden tests under
`dev/tests/StdRun`. Those tests are useful, but they are not the right permanent
home for stdlib behavior:

- many failures report only `exit: N`;
- tests are organized around the compiler golden runner, not stdlib API areas;
- stdlib behavior is mixed with compiler/runtime feature coverage;
- coverage is hard to audit against the stdlib public API;
- package-style and future self-hosted testing will benefit from Camp-source
  tests that are independent of the C# golden fixture format.

This proposal moves stdlib behavior coverage into stdlib-owned Camp tests while
keeping existing compiler regression tests intact until replacement coverage is
verified.

## Goals

- Add stdlib `@test` source beside the standard library.
- Compile stdlib source and stdlib tests together as an in-module test build.
- Run stdlib self-tests with `--nostdlib`, so std does not import itself.
- Use stdlib's own `Assertion`, `assert`, `fail`, and `Allocator` declarations.
- Preserve native PAL C source linkage for stdlib self-tests.
- Migrate existing stdlib behavior coverage from `StdRun` into named
  assertions.
- Add missing high-value stdlib behavior tests where existing coverage is weak.
- Integrate stdlib Camp tests into xUnit as individual test cases.
- Build and run the stdlib Camp harness once per module execution key.
- Maintain a migration ledger so existing coverage is not lost.
- Keep tests dense enough to control suite size while keeping failures readable.

## Non-Goals

- Removing all existing xUnit tests.
- Removing golden tests that assert compiler diagnostics, lowering, C emission,
  API headers, metadata, command-line behavior, package behavior, LSP behavior,
  DAP behavior, syntax highlighting, or native toolchain behavior.
- Achieving 100% line or branch coverage.
- Requiring one Camp `@test` function per old return code.
- Rewriting standard library implementation code except where a test exposes a
  real stdlib bug.
- Changing the semantics of `@test`, `@testonly`, `@skip`, `assert`, `fail`,
  `campc test`, or `campc cover`.

## Bug Handling During Migration

If a new stdlib `@test` exposes a failure during implementation, do not assume
the migrated test is correct and do not paper over the failure.

Handle the failure in this order:

1. Confirm that the new test is written correctly.
2. Confirm that the test is using the standard library API correctly.
3. Consult the compiler semantics documentation and determine whether the
   failure is a compiler bug, a standard library implementation bug, or an
   invalid test.
4. If it is a real compiler or stdlib bug, fix that bug first.
5. Commit the bug fix separately from the test migration work.
6. Resume the proposal implementation after the separate bug-fix commit.

This keeps the migration ledger honest: migrated tests should either verify
existing correct behavior or expose real bugs that are fixed independently
before the migration continues.

## Current Test Surface

Current Camp fixture counts under `dev/tests`:

| Lane | Cases | Current purpose | Stdlib migration role |
| --- | ---: | --- | --- |
| `StdRun` | 131 | Native executable runtime checks, mostly `exit: 0` goldens | Main source for stdlib behavior migration |
| `Std` | 3 source cases, 6 files including goldens | Stdlib-enabled lowering snapshots | Keep; compiler shape/ABI sanity |
| `CCompile` | 176 | Generated C compile checks | Keep except for any small stdlib behavior assertions found during audit |
| `CEmit` | 40 | Exact generated C snapshots | Keep |
| `Diagnostics` | 225 | Invalid program diagnostics | Keep |
| `Api` | 36 source cases, 72 files including goldens | API header snapshots | Keep |
| `Metadata` | 33 | Metadata JSON snapshots | Keep |
| `Lowering` | 28 source cases, 56 files including goldens | Lowered Camp snapshots | Keep |
| `LoweringXml` | 4 | XML lowering/lifetime fact snapshots | Keep |
| `Ast` | 7 | AST/parser snapshots | Keep |
| `Declarations` | 3 | Declaration-surface snapshots | Keep |

The stdlib migration should primarily consume `StdRun` cases whose purpose is
stdlib behavior. `CCompile`, `CEmit`, `Diagnostics`, `Api`, `Metadata`,
`Lowering`, `Ast`, and `Declarations` should remain active because they test
compiler behavior that stdlib runtime tests cannot replace.

## Current Standard Library Coverage

The following current tests are stdlib behavior candidates and should be
migrated.

| Existing test | New stdlib test file | Coverage to preserve |
| --- | --- | --- |
| `std_array_functions` | `std_array_tests.camp` | `copyTo`, `copyFrom`, `clear`, `reverse`, `sort`, `binarySearch`, overlapping copies, `copyArray`, delete of copied arrays |
| `string_functions` | `std_string_tests.camp` | UTF-8 search, case-insensitive search, codepoint search, `indexOfAny`, `lastIndexOfAny`, prefix/suffix, compare, trim, uppercase/lowercase, join, embedded null behavior |
| `wstring_functions` | `std_wstring_tests.camp` | wide search, compare, trim, case conversion, join, UTF-16 codepoint/unit behavior |
| `astring_functions` | `std_astring_tests.camp` | ANSI search, compare, trim, case conversion, join |
| `strconv_functions` | `std_strconv_tests.camp` | `toString`, `toWString`, `toAString`, lossy ANSI fallback, UTF conversion lengths/content |
| `parse_functions` | `std_parse_tests.camp` | signed/unsigned/long/ulong/double parse success, failure, whitespace/sign/bounds behavior |
| `hashcode_functions` | `std_hash_tests.camp` | primitive hashes, string hashes, case-insensitive hashes, equality helpers, pointer hash policy |
| `character_primitive_helpers` | `std_character_tests.camp` or `std_numerics_tests.camp` | char-family `MIN`, `MAX`, comparisons, hash/equality, `toString` |
| `math_double_min_max` | `std_numerics_tests.camp` | numeric constants and `min`/`max` helpers |
| `hashmap_collections` | `std_collections_tests.camp` | insert/update/remove/clear/growth/capacity/string hash policies/tryGet |
| `hashset_collections` | `std_collections_tests.camp` | add/remove/contains/clear/growth |
| `list_collections` | `std_collections_tests.camp` | add/insert/remove/index/copy/trim/growth |
| `list_char_writer` | `std_stream_tests.camp` or `std_collections_tests.camp` | `List<char>` as `CharWriter`, write/writeLine integration |
| `retained_container_allocators` | `std_collections_tests.camp` | retained allocator lifecycle for `List`, `HashMap`, and `HashSet` |
| `reader_helpers` | `std_stream_tests.camp` | reader helpers, line reads, read-to-end |
| `stream_adapters` | `std_stream_tests.camp` | byte/char/wchar/achar reader/writer adapters |
| `console_streams` | `std_console_tests.camp` | Console stream adapters, noninteractive smoke behavior |
| `console_trimmed_line` | `std_console_tests.camp` or `std_stream_tests.camp` | line trimming behavior |
| `environment_filesystem_runtime` | `std_io_tests.camp` | current directory, executable path, environment variables, directory creation/deletion, file copy/move/delete/size |
| `file_errors` | `std_io_tests.camp` | file error values and common failure paths |
| `file_handle` | `std_io_tests.camp` | open/read/write/close byte behavior |
| `path_lexical_helpers` | `std_path_tests.camp` | lexical path manipulation helpers |
| `time_functions` | `std_time_tests.camp` | `Date`, `TimeOfDay`, `DateTime`, `OffsetDateTime`, `Instant`, `TimeSpan`, `UtcOffset` parse/format/arithmetic |
| `timing_functions` | `std_timing_tests.camp` | `sleep`, `sleepAsync`, timers, cancellation smoke |

Some stdlib behavior is present incidentally in compiler regression tests. Those
cases should be inspected during the migration audit. If they contain distinct
stdlib behavior, add equivalent assertions to the stdlib suite. Do not use
compiler regression tests as the primary source of stdlib coverage.

## Target Standard Library Test Shape

Add tests under:

```text
dev/lib/std/tests/
```

The standard library should be tested as itself. The stdlib build definition
should include both production source and test source. It should be possible to
run:

```sh
campc test stdlib.campbuild --nostdlib
campc cover stdlib.campbuild --nostdlib
```

The concrete build-file path should match the final repository layout. The
important rule is that stdlib source and stdlib test source compile into the
same in-module test build, and std is not imported as an external dependency of
itself.

The current package build path gathers stdlib native `.c` PAL sources from
`lib/std/src`, while ordinary source-pattern expansion is `.camp`-oriented. The
implementation must ensure stdlib self-tests still link any required native PAL
sources. Acceptable implementation strategies:

- run stdlib self-tests through the existing package preparation path and add
  test-source participation there;
- extend build-file/project loading so the stdlib build file can include native
  C source files explicitly;
- introduce a small internal standard-library test request path that uses the
  same source/native-source discovery as bundled `std` package builds.

This proposal does not require a separate stdlib-only test build file distinct
from the stdlib package build definition.

## Proposed Stdlib Test Files

| File | Proposed tests | Does not test |
| --- | --- | --- |
| `std_test_helpers.camp` | shared helpers for comparing spans, arrays, strings, temp paths, cleanup | public API behavior by itself |
| `std_array_tests.camp` | `arrayCopyAndOverlap`, `arrayReverseSortSearch`, `arrayResizeAndCopyArray`, `arrayFindIndexHelpers` | generic compiler erasure details |
| `std_numerics_tests.camp` | `numericMinMaxConstants`, `numericAbsMinMaxHelpers`, `characterSemanticRanges` | exhaustive floating-point precision |
| `std_character_tests.camp` | `characterToStringAndHash`, `characterComparisons`, `unicodeCharacterBoundaries` | parser diagnostics for invalid literals |
| `std_string_tests.camp` | `stringSearchAndCompare`, `stringTrimAndCaseConversion`, `stringJoinAndEmbeddedNull`, `utf8CodepointHelpers` | interpolation lowering, C string literal storage |
| `std_wstring_tests.camp` | `wstringSearchAndCompare`, `wstringTrimCaseAndJoin`, `utf16CodepointHelpers` | UTF-8 string helper behavior |
| `std_astring_tests.camp` | `astringSearchAndCompare`, `astringTrimCaseAndJoin` | Unicode conversion fallback policy |
| `std_strconv_tests.camp` | `utf8ToWideAndBack`, `ansiConversionFallback`, `conversionPreparedBufferLengths` | compiler literal encoding |
| `std_format_tests.camp` | `primitiveToStringValues`, `boolCharAndStringToString`, `explicitPreparedBuffers`, `defaultPreparedCalls` | prep language lowering except as needed to test std API |
| `std_parse_tests.camp` | `parseSignedIntegers`, `parseUnsignedIntegers`, `parseLongIntegers`, `parseDoubleValues`, `parseRejectsInvalidText` | command-line parsing packages |
| `std_hash_tests.camp` | `primitiveHashPolicies`, `stringHashPolicies`, `caseInsensitiveHashPolicy`, `pointerHashPolicyWorks` | HashMap collision internals |
| `std_collections_tests.camp` | `listGrowthAndMutation`, `hashMapInsertUpdateRemove`, `hashSetAddRemoveContains`, `collectionsRetainAllocatorAndReleaseStorage` | compiler generic lowering beyond container behavior |
| `std_stream_tests.camp` | `charWriterWritesValues`, `readerReadsLinesAndEnd`, `byteCharAdapters`, `listCharWriterIntegration` | real terminal interactivity |
| `std_io_tests.camp` | `fileHandleReadWriteClose`, `filesystemCreateCopyMoveDelete`, `filesystemReportsCommonErrors`, `environmentVariablesAndCwd` | exact native OS error values hidden by std |
| `std_path_tests.camp` | `pathCombineAndNormalize`, `pathFilenameExtensionDirectory`, `pathSeparatorBehavior` | filesystem existence |
| `std_time_tests.camp` | `dateConstructionAndFormatting`, `timeOfDayFormatting`, `dateTimeParseFormat`, `offsetDateTimeParseFormat`, `durationAndInstantArithmetic` | deterministic wall-clock `now()` beyond smoke |
| `std_timing_tests.camp` | `sleepReturnsAfterDelay`, `sleepAsyncCompletes`, `timerCanStartAndStop` | precise scheduler timing |
| `std_atomic_tests.camp` | `atomicExchangeNInt`, `atomicExchangeNUInt`, `atomicExchangePointer`, `atomicCompareExchangeShapes` | concurrency stress testing |
| `std_console_tests.camp` | `consoleWriterSmoke`, `consoleLineHelpersSmoke` | terminal UI behavior |

This is intentionally grouped by stdlib source/API area. Most files should
contain a small number of dense tests rather than dozens of one-assertion tests.

## Test Style

Prefer tests that cover a coherent behavior group with several assertions.

Example:

```camp
@test
void arrayCopyReverseSortAndSearch(thrown Assertion*)
{
	int[] source = [3, 1, 2, 4];
	int[] destination = [0, 0, 0, 0];

	source.copyTo<int>(destination);
	assert(destination[0] == 3);
	assert(destination[3] == 4);

	destination.reverse<int>();
	assert(destination[0] == 4);

	destination.sort<int>(compareInt);
	assert(destination.binarySearch<int>(3, compareInt) == 2);
}
```

Tests that allocate through stdlib-owned heap paths should use the runner
allocator where practical:

```camp
@test
void collectionGrowthReleasesStorage(within Allocator* allocator, thrown Assertion*)
{
	auto list = new List<int>() finally delete;
	for (int i = 0; i < 32; i++)
		list.add(i);
	assert(list.Length == 32);
}
```

Use `within(default)` only for deliberate process-lifetime allocations or cases
where tracking would obscure the behavior under test.

Filesystem tests should use unique temp paths, avoid depending on ambient user
state, and clean up through `finally` where possible. Timing tests should use
tolerant assertions and should not attempt precise scheduler validation.

## xUnit Integration

### Required behavior

The xUnit integration must make individual stdlib Camp `@test` functions visible
as individual xUnit cases, while avoiding one compiler/native-build invocation
per Camp test.

The intended flow:

1. xUnit discovery asks the compiler for the stdlib Camp test manifest.
2. Each manifest entry becomes one xUnit test case.
3. During execution, the runner builds and runs the stdlib Camp harness once for
   the module/target/profile/request key.
4. The runner parses `camp.test-results` JSON.
5. Each xUnit case reports the outcome of its matching Camp test result.

This same mechanism can later be reused for package tests, but package test
integration is not part of this proposal.

### Discovery

Discovery should use compiler APIs directly when practical, not shell out to
`campc`, so it can reuse repository paths and avoid command-line quoting issues.
It may use the same code paths as `campc test --list`.

Discovery output must include:

- test ID;
- simple name;
- qualified name;
- source file;
- source line;
- summary;
- skip reason, if any;
- runner signature validity;
- owning Camp test module key.

If discovery fails, expose one synthetic failing xUnit test for the stdlib test
module rather than silently hiding all stdlib tests.

### Execution and caching

Execution should be keyed by:

- module key;
- target;
- profile;
- source file set hash or output path freshness key;
- relevant command options such as leak policy and coverage mode.

Use a process-local static cache with locking, similar in spirit to the current
`StdRun` batch cache. The first xUnit case for the stdlib module executes the
harness; subsequent cases read the parsed result from cache.

The runner may execute the full stdlib harness even when xUnit requests one
Camp test case. That preserves single-harness behavior and catches cross-test
failures consistently. A later optimization can run a filtered harness for
single-test IDE debugging if needed.

### Result mapping

Map Camp outcomes to xUnit outcomes:

| Camp outcome | xUnit outcome |
| --- | --- |
| `passed` | pass |
| `skipped` | skip |
| `failed` assertion | fail with assertion message and source location |
| `memory-leak` | fail with allocation/leak summary |
| `invalid` | fail; invalid test signature is a test authoring error |
| `error` | fail; harness/runtime error |
| compile/native-build/infrastructure failure | fail every projected test for the module, or expose a synthetic module failure if discovery did not complete |

Failure messages should include:

- Camp test qualified name;
- source file and line;
- assertion message or memory summary;
- path to the test result JSON;
- path to generated harness/build output when available.

### Coverage

The normal xUnit projection should run `campc test`, not `campc cover`.
Coverage should remain an explicit command or CI job because coverage builds are
slower and produce separate artifacts.

Add optional CI/local commands for:

```sh
campc cover stdlib.campbuild --nostdlib
```

## Migration Ledger

Add a migration ledger, for example:

```text
dev/tests/stdlib-camp-test-migration.md
```

The ledger should contain one row per old test fixture considered for stdlib
migration:

| Old lane | Old test | Classification | New location | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| `StdRun` | `string_functions` | stdlib-behavior | `dev/lib/std/tests/std_string_tests.camp` | pending/replaced/legacy-active/skipped | Preserve embedded-null case |

Classifications:

- `stdlib-behavior`;
- `mixed`;
- `compiler-shape`;
- `diagnostic`;
- `tooling`;
- `obsolete`.

Statuses:

- `pending`;
- `partially-replaced`;
- `replaced`;
- `legacy-active`;
- `legacy-skipped`;
- `kept`.

No existing test should be disabled until its ledger row names replacement
coverage or explains why the old test is intentionally kept.

## Existing Test Disabling Policy

Do not delete existing tests during the first migration pass.

After replacement coverage is reviewed:

- exact-shape tests remain active;
- tests whose primary purpose is compiler/runtime behavior remain active;
- duplicated stdlib runtime-behavior tests may be skipped or disabled;
- duplicated old `StdRun` cases may be moved to a legacy directory or annotated
  with a skip mechanism;
- if a lane-level skip mechanism is added, it must be explicit in the test file
  or ledger, not hidden in broad runner logic.

This is intentionally conservative. The cost of accidentally losing coverage is
higher than the cost of carrying duplicate tests for a while.

## Future Self-Hosting Use

The stdlib test suite should remain ordinary Camp source. A future
self-compiling compiler should be able to compile and run the same stdlib tests
once it supports enough of the language and stdlib to build std.

Future flow:

1. Today's compiler runs the full stdlib Camp test suite.
2. The future compiler starts by compiling smaller stdlib test subsets if
   needed.
3. As compiler support matures, it runs the full stdlib test suite.
4. CI compares pass/fail status by Camp test ID.

This proposal does not define the future compiler's test runner. It only shapes
the stdlib test source so it can be reused.

## Implementation Plan

### Stage 1: Add migration ledger and stdlib test skeleton

- Add the migration ledger.
- Add `dev/lib/std/tests`.
- Add stdlib test-source inclusion through the stdlib build path.
- Add one stdlib smoke `@test`.
- Verify `campc test stdlib.campbuild --nostdlib` or the final equivalent.

Completion criteria:

- stdlib source and stdlib test source compile in one test module;
- stdlib self-test uses stdlib's own `Assertion`, `assert`, `fail`, and
  `Allocator`;
- required native PAL C sources are linked;
- one stdlib smoke test passes on the host platform;
- ledger exists and contains initial rows for `StdRun` stdlib candidates.

### Stage 2: Add xUnit Camp test projection

- Add C# model for stdlib Camp test modules.
- Add discovery-time manifest generation.
- Add xUnit case projection per Camp test.
- Add one-harness execution cache for the stdlib module.
- Map Camp outcomes to xUnit outcomes.
- Add stdlib module projection to the normal test suite.

Completion criteria:

- stdlib Camp tests appear as individual xUnit test cases;
- stdlib harness runs once per module execution key;
- assertion failures show Camp source location and message;
- discovery failure produces a visible xUnit failure;
- normal full suite includes projected stdlib Camp tests.

### Stage 3: Migrate deterministic stdlib tests

- Migrate array, numeric, character, string, wide string, ANSI string,
  conversion, parse, hash, collection, and stream behavior.
- Keep tests dense and grouped by API area.
- Update ledger rows as replacements land.

Completion criteria:

- replacement tests pass through xUnit projection;
- old return-code cases are mapped to named assertions;
- no old stdlib behavior test is disabled without a ledger replacement;
- leak-tracked collection tests use runner-supplied allocator where appropriate.

### Stage 4: Migrate OS/PAL stdlib tests

- Migrate filesystem, environment, file handle, path, time, timing, atomic, and
  console smoke behavior.
- Make temp files/directories isolated and cleanup robust.
- Keep timing tests tolerant.

Completion criteria:

- tests pass on Windows and Linux;
- macOS targeted or full sectioned run passes depending on suite cost;
- target-specific tests use `requires` or `@skip` deliberately;
- old OS/PAL stdlib tests remain active until replacement is verified.

### Stage 5: Coverage and suite cleanup

- Run stdlib coverage.
- Review obvious blind spots.
- Disable or move duplicated old stdlib runtime tests.
- Keep shape/diagnostic/tooling tests active.

Completion criteria:

- coverage output is generated for stdlib;
- migration ledger has no unexplained stdlib behavior candidates;
- duplicated tests are explicitly marked legacy/skipped or left active with
  reason;
- full suite passes on Windows and Linux;
- macOS full suite passes sectioned or has only separately documented unrelated
  flakes.

## Documentation Updates

Update:

- `docs/compiler/01-campc-command-line.md` with stdlib test command examples if
  the command surface changes;
- `docs/compiler/05-artifacts-cache-and-output-layout.md` if new test artifact
  directories are introduced;
- `docs/compiler-development-guide.md` with the policy for Camp-source stdlib
  tests, migration ledger, and xUnit projection;
- `docs/camp-llm-coding-guide.md` with guidance to prefer Camp `@test` for
  stdlib behavior and to keep compiler-shape tests in compiler lanes;
- `dev/tests/README.md` with the new stdlib test surface and legacy-test policy.

The language guide should not get detailed stdlib test infrastructure material.
This is contributor/compiler-maintainer documentation.

## Test Surface for This Migration

The migration itself needs tests:

- xUnit projection discovers a synthetic Camp manifest and produces one xUnit
  case per Camp test;
- execution cache runs the harness once for multiple projected cases;
- passed/skipped/failed/invalid/error/memory-leak results map correctly;
- discovery failure appears as a visible xUnit failure;
- stdlib smoke `@test` can run with `--nostdlib`;
- stdlib test build links native PAL C sources;
- impacted-test selection includes stdlib source/test changes;
- existing full suite still runs old lanes unless explicitly disabled.

## Risks

### Coverage loss

Risk: return-code tests encode many small cases that are easy to lose.

Mitigation: maintain the migration ledger and map each meaningful return code to
a named assertion or documented retirement.

### False replacement

Risk: a stdlib runtime test proves behavior but not compiler ABI/API/metadata
shape.

Mitigation: do not retire CEmit/API/Metadata/Lowering tests when only stdlib
runtime behavior has been migrated.

### xUnit performance regression

Risk: projecting each Camp test into xUnit accidentally builds/runs the harness
once per xUnit case.

Mitigation: enforce one-harness execution caching and add a test for it.

### Native/PAL omission in stdlib self-tests

Risk: plain `.campbuild` stdlib self-tests omit native C sources currently
included by package preparation.

Mitigation: make native-source inclusion an explicit completion criterion for
the stdlib test path.

### Duplicate suite cost

Risk: keeping old and new tests active temporarily increases suite time.

Mitigation: accept this during migration, then disable duplicated old stdlib
runtime tests only after replacement review.

## Recommendation

Implement this migration.

The critical engineering choices are:

- stdlib tests use Camp `@test` beside stdlib source;
- stdlib source and stdlib test source compile together with `--nostdlib`;
- xUnit projects Camp tests from manifests but runs the stdlib harness once;
- old tests are disabled only after ledger-backed replacement;
- compiler-shape tests remain in the existing suite.
