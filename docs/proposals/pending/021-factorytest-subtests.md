# Factory Tests And Runtime Subtest Reporting

Status: draft  
Proposal date: 2026-09-16  
Last updated date: 2026-09-16

This proposal extends Camp's built-in test runner with factory tests: test-only
functions that are not invoked automatically, but report one child result each
time they are called while a top-level `@test` is running.

The primary motivation is to make dense data-driven and golden-file test suites
cheap to write and maintain without giving up individual test result reporting.

## Summary

Add a new function metadata attribute:

```camp
@factorytest
```

A factory test is a self-reporting test helper. It has a test-shaped thrown
failure channel, may accept ordinary caller-supplied parameters, and records a
child result under the currently running top-level `@test` each time it is
called.

Example:

```camp
@test
void basicTests(within allocator, thrown Assertion*)
{
	testAdd("addOneAndFive", 1, 5, 112);
	testAdd("addOneAndTwo", 1, 2, 3);
}

@factorytest
void testAdd(@testname string testname, int first, int second, int expected, thrown Assertion*)
{
	assert((first + second) == expected);
}
```

The built-in runner invokes `basicTests` because it is marked `@test`. The
runner does not invoke `testAdd` directly. Instead, the compiler lowers
`testAdd` so its own body records a child result whenever it is called during
`basicTests`.

Human-readable output may look like:

```text
failed: basicTests (1 passed, 1 failed, 0 skipped)
> failed: addOneAndFive
> passed: addOneAndTwo
```

The feature is intentionally narrow:

- no nested factory tests;
- no automatic discovery of runtime data files;
- no general-purpose dynamic test framework;
- no call-site instrumentation for ordinary calls to factory tests.

Factory tests are meant to make patterns such as golden-file testing practical:

```camp
@test
void semanticTests(within allocator, thrown Assertion*)
{
	foreach (auto sourceFilePath in discoverTestSourceFiles())
	{
		testSourceFile(Path.fileNameSpan(sourceFilePath), sourceFilePath);
	}
}

@factorytest
void testSourceFile(@testname const char[] testname, string sourceFilePath, within allocator, thrown Assertion*)
{
	// Compile or check the source file and assert the expected result.
}
```

The filename becomes the child test name, so adding or updating a golden file
does not require editing the test driver source.

## Current Behavior

Camp currently has first-class `@test` and `@testonly` support.

Top-level `@test` functions are discovered into a test manifest. The built-in
runner invokes runnable tests with one of these shapes:

```camp
@test
void name(thrown Assertion*)
{
}

@test
void name(within allocator, thrown Assertion*)
{
}
```

`@testonly` marks top-level helpers that are available only in test and coverage
builds.

The current model has no way to report multiple runtime-discovered cases under
one `@test` without turning each case into a separate static `@test`
declaration. That makes directory-driven golden tests more expensive than they
need to be, because every new case requires test source edits instead of just a
new input/expected file pair.

## Goals

1. Allow a top-level `@test` to run many related child test cases while still
   reporting each child pass/fail/skip result separately.
2. Preserve normal Camp call semantics at factory-test call sites.
3. Allow factory tests to be called directly, through helpers, through lambdas,
   and through function/delegate values during test execution.
4. Support runtime-discovered test cases such as filesystem golden suites.
5. Keep factory tests test-only and unavailable to production code.
6. Keep ordinary top-level `@test` functions runner-owned and not callable from
   user code.
7. Preserve existing `@test`, `@testonly`, `@skip`, `assert`, `fail`,
   filtering, result emission, and coverage semantics except where this
   proposal explicitly extends them.

## Non-Goals

This proposal does not add:

- nested factory-test hierarchies;
- parameterized static test manifest entries;
- automatic directory scanning by the compiler;
- automatic golden-file conventions;
- a new assertion library;
- a new CLI command;
- new syntax beyond the `@factorytest` and `@testname` metadata attributes;
- call-site rewriting for every call to a factory test;
- production availability for factory-test functions.

## Terms

**Top-level test** means a top-level function marked `@test`.

**Factory test** means a top-level function marked `@factorytest`.

**Parent test** means the currently running top-level `@test` under which
factory-test child results are recorded.

**Child result** means the result recorded by one runtime call to a factory test.

**Factory display name** means the runtime name assigned to a child result,
either from an argument passed to a `@testname` parameter or from the factory
test function name.

**Factory ordinal** means the `.1`, `.2`, `.3`, ... suffix used to disambiguate
multiple child results with the same display name under the same parent test.

## `@factorytest`

`@factorytest` accepts no arguments.

It is valid only on a top-level function declaration with no visibility
modifier. A file-level namespace does not make the function scoped.

The compiler treats `@factorytest` as structured metadata:

- validate that it appears on a top-level function;
- validate that the function has no visibility modifier;
- validate that it is not combined with `@test`;
- mark the function as test-only for emission;
- include the function in test-module metadata as a factory-test declaration;
- generate runtime result instrumentation inside the factory-test function.

The built-in runner never invokes `@factorytest` functions automatically.

Factory-test functions are available only in `TEST_MODULE` builds. Production
declarations may not depend on them. A factory test may be called from a
`@test`, from a `@testonly` helper, from another declaration available only in
`TEST_MODULE`, or through a callable value while a top-level test is running.

`@factorytest` is invalid on:

- functions with any visibility modifier, including `internal`, `public`, and
  `export`;
- methods, constructors, destructors, accessors, out-of-scope static member
  declarations, local functions, or lambdas;
- declarations also marked `@test`.

Factory tests must be callable like ordinary functions in test-mode source. The
compiler must not diagnose ordinary calls to factory tests merely because the
call is not syntactically inside a `@test` body. Runtime reporting determines
whether a parent test is active.

## Factory-Test Signature

A factory test must:

- have a body;
- return `void`;
- have exactly one trailing `thrown Assertion*` slot;
- optionally have one `within` allocator parameter immediately before the
  thrown slot;
- place all ordinary caller-supplied parameters before the optional `within`
  slot and required `thrown` slot;
- not be extern;
- not be generic;
- not be async;
- not be an iterator.

Examples:

```camp
@factorytest
void parseCase(@testname const char[] name, const char[] source, bool shouldPass, thrown Assertion*)
{
}

@factorytest
void writesText(@testname string name, string value, within allocator, thrown Assertion*)
{
}
```

The allocator slot may use either the implicit form:

```camp
within allocator
```

or the explicit standard interface form:

```camp
within Allocator* allocator
```

When a factory test has a `within` parameter, callers supply it using normal
Camp `within` context rules. The parent test's runner-supplied allocator is not
magically inserted into an unrelated factory-test call. In ordinary usage, the
call occurs inside a parent `@test` that already has an active allocator
context, so the call naturally passes that allocator through.

All allocations through the parent test's tracked allocator are checked once
when the parent test completes. Factory-test calls do not get separate tracked
allocators by default.

Invalid factory-test signatures are test results or diagnostics according to
the same line used by `@test`: invalid runner shape is a test discovery/result
problem, while invalid source placement or invalid attribute combination is a
compiler diagnostic.

## `@testname`

`@testname` is a parameter metadata attribute.

It accepts no arguments.

It is valid only on an ordinary parameter of a `@factorytest` function. It is
invalid on:

- parameters of non-factory-test functions;
- `within` parameters;
- `thrown` parameters;
- generic parameters;
- fields or declarations.

A factory test may have at most one `@testname` parameter.

The marked parameter type must be `string` or `const char[]`.

The parameter remains an ordinary parameter. The function body may read it, pass
it to helpers, or ignore it. The attribute only tells the test runtime which
argument value supplies the child result name.

Example:

```camp
@factorytest
void fileCase(@testname const char[] name, string path, thrown Assertion*)
{
	Console.writeLine(name);
}
```

When no parameter is marked `@testname`, the factory-test function name is used
as the child result name.

## Calling `@test` And `@factorytest`

Top-level `@test` functions are runner-owned. User source may not explicitly
call a `@test` function and may not take the address of a `@test` function.

The compiler must diagnose both forms:

```camp
@test
void root(thrown Assertion*)
{
}

void invalid(thrown Assertion*)
{
	root();       // error
	auto f = root; // error
}
```

Factory tests are different. They are ordinary callable test-only functions
with self-reporting instrumentation inside their own bodies.

These shapes are valid in `TEST_MODULE` code:

```camp
@test
void root(thrown Assertion*)
{
	testAdd("direct", 1, 2, 3);
	callHelper();
}

@testonly
void callHelper(thrown Assertion*)
{
	testAdd("helper", 2, 2, 4);
}

@test
void callableCase(thrown Assertion*)
{
	fn void(string, int, int, int, thrown Assertion*) f = testAdd;
	f("functionPointer", 3, 4, 7);
}

@factorytest
void testAdd(@testname string name, int first, int second, int expected, thrown Assertion*)
{
	assert((first + second) == expected);
}
```

The compiler does not need to wrap or rewrite the call sites above. All
instrumentation belongs inside the lowered body of `testAdd`.

## Argument Evaluation

Factory-test calls preserve ordinary Camp argument evaluation semantics.

Filtering and skipping must not prevent argument expressions from being
evaluated. In this example, all arguments are evaluated before control reaches
the factory-test body:

```camp
testAdd(makeName(), expensiveFirst(), expensiveSecond(), expected);
```

If expensive setup should happen only when the child test actually runs, place
that setup inside the factory-test body rather than in the call arguments.

This rule keeps factory-test calls predictable: `@factorytest` changes result
reporting and body execution, not the ordinary evaluation of the call
expression.

## Lowering Model

The compiler lowers each valid `@factorytest` body into a self-reporting wrapper.

Source:

```camp
@factorytest
void testAdd(@testname string testname, int first, int second, int expected, thrown Assertion*)
{
	assert((first + second) == expected);
}
```

Conceptual lowered shape:

```camp
void testAdd(string testname, int first, int second, int expected, thrown Assertion*)
{
	if (__camp_test_beginFactory("testAdd", testname))
	{
		try
		{
			assert((first + second) == expected);
		}
		catch (Assertion* assertion)
		{
			__camp_test_endFactory(assertion);
			return;
		}

		__camp_test_endFactory(default);
	}
}
```

This is conceptual only. The real compiler may use private generated thunks,
explicit catch parameters, helper calls, or another equivalent lowering shape.
The observable requirements are:

- call arguments are evaluated normally before entering the factory-test
  function;
- if the runtime determines that the child result should not run, the original
  body is not executed;
- if the original body completes without assertion failure, the child result is
  passed;
- if the original body throws the mandated `Assertion*`, the child result is
  failed;
- the assertion does not propagate to the parent test;
- the function returns normally to its caller after recording the child result;
- the lowered body preserves ordinary lifetimes, cleanup, `within`, and
  `thrown` rules for the original body.

The factory-test wrapper uses the same assertion failure shape already detected
for built-in tests. Because the factory-test thrown slot type is mandated, there
is no separate non-assertion thrown channel for ordinary factory-test failures.

## Runtime Parent Context

The test runtime maintains the currently running top-level test.

A factory test normally runs while a parent `@test` is active. Its child result
is attached to that parent.

If a factory test is called while no parent test is active, the runtime records
an infrastructure misuse if it can do so and does not run the factory-test body.
In ordinary source this should be rare because factory tests are available only
in `TEST_MODULE` code, and top-level tests themselves are runner-owned.

The runtime also maintains the currently active factory-test call under the
current parent test.

## Nested Factory Tests

Nested factory tests are not supported.

If a factory test begins while another factory test is still active under the
same parent, the active unfinished factory test is recorded as invalid because
it did not complete before another factory test began.

The newly attempted nested factory test is also recorded as invalid, and its
body is not executed.

Example:

```camp
@factorytest
void outer(thrown Assertion*)
{
	inner();
}

@factorytest
void inner(thrown Assertion*)
{
}
```

A call to `outer()` records invalid factory-test results rather than running a
nested hierarchy.

This is a runtime rule because factory tests may be called indirectly through
helpers, lambdas, function pointers, or delegates. A purely syntactic compiler
rule would either miss real nesting or reject useful non-nested helper patterns.

## Skipped Factory Tests

`@skip` may appear on a declaration marked `@factorytest`.

This extends the existing placement rule for `@skip`: it is valid only on
`@test` or `@factorytest` declarations.

When a skipped factory test is called:

- all call arguments are evaluated normally;
- the factory-test body is not executed;
- one skipped child result is recorded for the attempted invocation.

Example:

```camp
@skip("parser recovery not implemented yet")
@factorytest
void recoveryCase(@testname const char[] name, string source, thrown Assertion*)
{
}
```

If a skipped factory test is called multiple times, each attempted invocation is
recorded as skipped, subject to the same naming and ordinal rules as pass/fail
results.

## Names And Ordinals

If a factory test has a `@testname` parameter, the runtime value passed to that
parameter supplies the base display name.

If no parameter is marked `@testname`, the factory-test function's visible
source name supplies the base display name.

When two or more child results under the same parent have the same base display
name, the runtime appends a one-based ordinal suffix to each result:

```text
> failed: add.1
> passed: add.2
```

When there is only one result with a base display name, no ordinal suffix is
required:

```text
> passed: add
```

The runtime may buffer child results until the parent test completes so it can
retroactively add `.1` to the first duplicate when a later duplicate appears.

If ordinals need to be stable across platform- or environment-dependent case
sets, test authors should supply unique `@testname` values instead of relying on
duplicate-name ordinals.

## Filtering

`--filter` continues to match top-level tests as it does today.

This proposal extends filter syntax with an optional child portion separated by
`/`:

```text
campc test app.campbuild --filter basicTests/addOne
```

The portion before `/` matches the parent top-level test using the existing
filter rules for manifest ID, qualified name, and simple name.

The portion after `/` matches factory-test child results under the matched
parent.

Examples:

```text
campc test app.campbuild --filter basicTests
campc test app.campbuild --filter basicTests/addOne
campc test app.campbuild --filter basicTests/addOneAndTwo
campc test app.campbuild --filter basicTests/testAdd.1
```

Rules:

- A filter without `/` behaves as it does today.
- If a filter without `/` matches a parent test, the parent test runs and all
  factory-test calls under it are eligible to run.
- A filter with `/` selects matching parent tests, then applies the child
  portion to factory child result names.
- Multiple filters remain ORed.
- Existing wildcard syntax applies to both parent and child portions.
- Child matching uses the final display name, including ordinal suffixes when
  present.

Because child names are runtime values, child filters are applied by the test
runtime when each factory test begins. A parent test selected only by a child
filter must still run so it can discover and attempt its factory-test calls.

When a factory-test child is filtered out:

- all call arguments have already been evaluated normally;
- the factory-test body is not executed;
- no child result is printed as passed/failed/skipped;
- internal filter bookkeeping may record that the child was filtered.

If a parent selected by a child filter produces no matching child results, the
runner should report a filter miss for that parent or for the overall command in
the same spirit as existing no-test-selected behavior.

## Parent Result Aggregation

A top-level parent `@test` result includes the results of its child factory
tests.

Recommended aggregation:

- `passed`: the parent body did not fail and all executed child results passed
  or were skipped;
- `failed`: the parent assertion failed, or at least one child result failed;
- `skipped`: the parent test itself was skipped and therefore not invoked;
- `invalid`: the parent test has an invalid built-in runner signature;
- `error`: an infrastructure/runtime error occurred.

If a factory test fails, the parent test continues running unless the parent
itself chooses to stop. Later factory-test calls still run, and ordinary cleanup
at the end of the parent test still runs.

If the parent test itself throws an assertion outside a factory-test body, the
parent fails and normal parent execution stops. Factory-test results already
recorded remain attached to the parent.

## Human-Readable Output

Text output should show a parent summary followed by child lines.

Example with one failed child:

```text
failed: basicTests (1 passed, 1 failed, 0 skipped)
> failed: addOneAndFive
> passed: addOneAndTwo
```

Example with duplicate child names:

```text
failed: basicTests (1 passed, 1 failed, 0 skipped)
> failed: add.1
> passed: add.2
```

Example with no `@testname` parameter:

```text
failed: basicTests (1 passed, 1 failed, 0 skipped)
> failed: testAdd.1
> passed: testAdd.2
```

The exact indentation marker is not semantically important. The output should
make parent/child hierarchy obvious and remain easy to scan in terminal logs.

Text output remains a projection of JSON results. Tools should consume JSON.

## Test Manifest

The existing test manifest remains the source of truth for top-level test
discovery.

Factory-test declarations may appear in the manifest or in a separate manifest
section as non-runnable factory declarations. They must not appear as top-level
runnable tests.

Recommended manifest extension:

```json
{
  "format": "camp.test-manifest",
  "version": 2,
  "mode": "in-module",
  "tests": [
    {
      "id": "MathTests::basicTests",
      "name": "basicTests",
      "qualifiedName": "MathTests::basicTests",
      "sourcefile": "tests/math.camp",
      "sourceline": 8,
      "summary": "",
      "skipped": false,
      "skipReason": null,
      "runnerSignature": "valid"
    }
  ],
  "factoryTests": [
    {
      "id": "MathTests::testAdd",
      "name": "testAdd",
      "qualifiedName": "MathTests::testAdd",
      "sourcefile": "tests/math.camp",
      "sourceline": 14,
      "summary": "",
      "skipped": false,
      "skipReason": null,
      "runnerSignature": "valid",
      "testNameParameter": "testname"
    }
  ]
}
```

The exact JSON field shape may follow existing serializer conventions, but the
semantic distinction must be preserved:

- `tests` are runner-invoked top-level tests;
- `factoryTests` are callable test-only functions that can produce runtime child
  results.

## Test Results JSON

The test result format should preserve hierarchy.

Recommended extension:

```json
{
  "format": "camp.test-results",
  "version": 2,
  "summary": {
    "passed": 1,
    "failed": 1,
    "skipped": 0,
    "invalid": 0,
    "error": 0,
    "total": 2
  },
  "tests": [
    {
      "id": "MathTests::basicTests",
      "name": "basicTests",
      "qualifiedName": "MathTests::basicTests",
      "outcome": "failed",
      "children": [
        {
          "id": "MathTests::basicTests/addOneAndFive",
          "name": "addOneAndFive",
          "factoryName": "testAdd",
          "qualifiedFactoryName": "MathTests::testAdd",
          "outcome": "failed",
          "failure": {
            "kind": "assertion",
            "message": "(first + second) == expected",
            "sourcefile": "tests/math.camp",
            "sourceline": 17
          }
        },
        {
          "id": "MathTests::basicTests/addOneAndTwo",
          "name": "addOneAndTwo",
          "factoryName": "testAdd",
          "qualifiedFactoryName": "MathTests::testAdd",
          "outcome": "passed",
          "failure": null
        }
      ]
    }
  ]
}
```

Summary counting should include child results because child results are the
individual reported test outcomes users care about in data-driven suites.

The parent object should also preserve its own direct failure when the parent
body fails outside a factory-test body.

## Coverage

Factory-test declarations are test-only and excluded from production coverage
subjects, just like ordinary `@test` functions and explicit `@testonly`
helpers.

Factory-test execution still contributes coverage for production declarations
called by the factory-test body.

The generated factory-test wrapper and runtime reporting helpers are not source
coverage subjects.

## Metadata And API Boundaries

Ordinary generated Camp API headers and ordinary production metadata must not
contain:

- user `@factorytest`;
- user `@testname`;
- user factory-test functions;
- generated factory-test wrapper declarations;
- generated runtime reporting helpers.

Test-module metadata may include factory-test discovery data for source tooling,
debugging, and result correlation.

`@factorytest` implies test-only ownership of the function and generated
declarations owned by it.

## LSP And Debugger

The LSP should recognize `@factorytest` and `@testname` as testing attributes.

Minimum LSP behavior:

- diagnostics for invalid source placement of `@factorytest`;
- diagnostics for invalid source placement of `@testname`;
- diagnostics for explicit calls/address-taking of `@test`;
- no ordinary CodeLens run/debug action that treats a factory test as a
  top-level runnable test.

Optional follow-up behavior:

- show factory-test declarations in a test explorer as non-runnable factory
  declarations;
- after a test run, show runtime child results under the parent top-level test;
- allow rerunning a known child result by constructing a parent/child filter.

The debug adapter can continue launching the generated harness with `--filter`.
Debugging a specific factory child uses a parent/child filter when the child
name is known and stable.

## Diagnostics

Compiler diagnostics:

- `@factorytest` on anything other than a top-level function;
- `@factorytest` with arguments;
- `@factorytest` with any visibility modifier;
- `@factorytest` combined with `@test`;
- `@factorytest` on methods, constructors, destructors, accessors,
  out-of-scope static members, local functions, or lambdas;
- `@testname` outside a `@factorytest` parameter list;
- more than one `@testname` parameter on one factory test;
- `@testname` on a `within` or `thrown` parameter;
- `@testname` on a parameter whose type is not `string` or `const char[]`;
- `@skip` on a declaration not marked `@test` or `@factorytest`;
- explicit call to a `@test` function;
- address-taking or callable conversion of a `@test` function;
- production declaration depending on a factory-test declaration;
- public/exported API exposing a factory-test declaration or test-only type.

Invalid factory-test results, not compiler-stopping diagnostics:

- factory-test function without a body;
- non-`void` result;
- missing trailing `thrown Assertion*`;
- wrong thrown type;
- additional thrown slots;
- ordinary parameters after the optional allocator slot;
- extern function;
- generic function;
- async function;
- iterator function.

Runtime invalid child results:

- factory test called while no parent top-level test is active;
- factory test begins while another factory test is active;
- test runtime cannot record or close a factory-test child result.

Assertion failures inside factory-test bodies are child test results, not
compiler diagnostics and not parent-thrown assertions.

## Interaction With `@testonly`

`@factorytest` is not a synonym for `@testonly`, but it implies test-only
participation.

`@testonly` remains useful for fixtures, helper functions, helper types, and
support code that should exist only in test and coverage builds.

Factory tests may call `@testonly` helpers. `@testonly` helpers may call factory
tests. A factory test called through a helper still records under the currently
running top-level parent test.

## Interaction With `requires (TEST_MODULE)`

Factory tests are reachable in `TEST_MODULE` configuration.

Source explicitly guarded with `requires (TEST_MODULE)` may call factory tests.
That does not make factory tests production-visible; it only makes them
available in the same configuration where test support exists.

## Interaction With Existing Filters

Existing filters are preserved. A filter without `/` remains valid and keeps its
current meaning.

The `/` child separator is only meaningful to `campc test` and `campc cover`
filter handling. It is not a namespace separator and does not change test IDs
outside the test runner.

## Interaction With Assertions And Source Capture

Factory-test wrappers must preserve `assert(...)` and `fail(...)` source
capture.

When an assertion fails inside the original factory-test body, the reported
source file and source line should point at the assertion call site inside the
factory-test source, not at generated wrapper code.

Assertion wrappers used inside factory tests follow the same source-capture
rules as ordinary tests.

## Interaction With Allocator Leak Checking

Factory-test child results do not perform independent leak checks by default.

The runner-supplied tracking allocator belongs to the parent top-level test.
All allocations made through that allocator during the parent test, including
allocations made in factory-test calls, are checked once when the parent test
completes.

If a child factory test leaks but later parent cleanup releases the allocation,
the parent leak check passes. If a child factory test leaks and no later cleanup
releases it, the parent test receives the leak failure according to existing
leak-checking semantics.

This keeps fixture setup and teardown simple and avoids requiring every
factory-test call to create or destroy its own allocator scope.

## Implementation Notes

The current bootstrap compiler already has these relevant pieces:

- test attribute validation in `BindableNodeAnalyzer.TestAttributes`;
- test discovery in `CampTestDiscovery`;
- test manifest/result serialization;
- generated harness support;
- test and coverage command paths;
- LSP test discovery integration;
- coverage exclusion rules for test/test-only declarations.

The implementation should extend these rather than building a second test
runner.

Likely compiler work:

1. Parse and bind `@factorytest` and `@testname` as ordinary metadata attributes.
2. Classify factory tests during test-attribute validation.
3. Extend declaration participation so factory tests are test-only.
4. Validate factory-test source placement and signature.
5. Diagnose explicit calls/address-taking of `@test`.
6. Add lowered wrapper instrumentation to factory-test bodies.
7. Add test runtime helpers for begin/end factory child results.
8. Extend result state to buffer child results until parent completion.
9. Extend filtering to parse and apply optional parent/child patterns.
10. Extend test manifest and result JSON formats.
11. Update text output.
12. Update coverage exclusion.
13. Update LSP/test discovery metadata as needed.
14. Update syntax highlighting for `@factorytest` and `@testname`.

The lowering should avoid call-site instrumentation. Calls to factory tests
should continue to bind and lower like ordinary calls; the called factory-test
function is responsible for reporting its own result.

## Test Surface

### Attribute validation

- valid `@factorytest` with `thrown Assertion*`;
- valid `@factorytest` with ordinary parameters;
- valid `@factorytest` with `within allocator`;
- valid `@testname string`;
- valid `@testname const char[]`;
- reject `@factorytest` on methods, static members, local functions, lambdas,
  types, fields, enum values, aliases, and variables;
- reject visibility modifiers on `@factorytest`;
- reject `@factorytest` combined with `@test`;
- reject multiple `@testname` parameters;
- reject `@testname` on non-string parameter types;
- reject `@testname` on `within` and `thrown` parameters.

### Signature discovery

- factory test with no body is invalid;
- factory test returning non-void is invalid;
- factory test missing thrown slot is invalid;
- factory test with wrong thrown type is invalid;
- factory test with optional allocator before thrown is valid;
- factory test with parameters after allocator is invalid;
- extern/generic/async/iterator factory tests are invalid.

### Call semantics

- direct factory-test call from `@test`;
- factory-test call through `@testonly` helper;
- factory-test call through function pointer;
- factory-test call through delegate/lambda helper if the compiler supports the
  callable shape;
- arguments are evaluated even when child is filtered out;
- arguments are evaluated even when factory test is skipped;
- explicit call to `@test` is rejected;
- address-taking of `@test` is rejected.

### Runtime results

- one passing child result;
- one failing child result;
- parent continues after failed child;
- parent cleanup runs after failed child;
- skipped factory test records skipped child;
- duplicate names receive `.1`, `.2`, ... ordinals;
- unnamed duplicate factory calls use function name plus ordinals;
- factory test called outside parent context records invalid/runtime misuse;
- nested factory tests record invalid results and do not run nested bodies.

### Filtering

- parent-only filter runs parent and all child calls;
- parent/child filter runs parent and matching child calls only;
- child wildcard filter;
- child ordinal filter;
- multiple filters OR together;
- no matching child reports appropriate no-selected-test behavior;
- skipped child still respects child filtering.

### JSON/text artifacts

- manifest contains top-level tests and factory-test declarations distinctly;
- result JSON preserves child hierarchy;
- text output shows child results under parent summaries;
- invalid factory signatures appear in machine-readable output;
- source locations for child assertion failures point to user source.

### Coverage

- factory-test declarations excluded from production coverage denominator;
- generated wrappers excluded from source coverage;
- production code called by factory tests is counted;
- coverage results still merge correctly with child test results.

### LSP/debugging

- factory-test declarations do not appear as ordinary runnable top-level tests;
- invalid attribute placement diagnostics appear through the language service;
- parent/child filters can be passed to debug/test launch paths.

## Documentation Updates

### Language guide

Update `docs/language/19-attributes-documentation-comments-and-metadata-hints.md`.

The language guide should stay everyday-focused:

- introduce `@factorytest` as a way to write data-driven tests;
- show one small example with a parent `@test` and two factory-test calls;
- explain `@testname` briefly;
- mention that factory tests are not run automatically;
- mention that failed factory-test calls do not stop later factory-test calls;
- avoid lowering, manifest, result JSON, and runtime helper details.

Suggested guide-level example:

```camp
@test
void addCases(thrown Assertion*)
{
	checkAdd("onePlusTwo", 1, 2, 3);
	checkAdd("twoPlusFive", 2, 5, 7);
}

@factorytest
void checkAdd(@testname const char[] name, int left, int right, int expected, thrown Assertion*)
{
	assert((left + right) == expected);
}
```

### Semantic docs

Update canonical semantic docs, especially:

- `docs/semantics/01-binding-analysis-and-lowering-pipeline.md`;
- `docs/semantics/11-metadata-api-surface-and-symbols.md`;
- `docs/semantics/13-diagnostics-source-ranges-and-error-quality.md`;
- any test/coverage semantic page if one exists by implementation time.

The semantic docs must fully specify:

- source placement and signature rules for `@factorytest`;
- parameter placement/type rules for `@testname`;
- test-only participation;
- explicit `@test` call/address diagnostics;
- factory-test lowering obligations;
- runtime parent/child state;
- filtering behavior;
- result JSON hierarchy;
- coverage exclusions;
- metadata/API boundaries.

The proposal must not be the only place where these semantics are preserved
after implementation.

### Compiler docs

Update command-line docs for `campc test` and `campc cover`:

- document parent/child filter syntax;
- show examples of filtering a factory child;
- describe text and JSON child result output.

Update metadata/result format docs:

- manifest version/shape changes;
- result JSON child hierarchy;
- factory-test declaration metadata if emitted.

### LLM coding guide

Update `docs/camp-llm-coding-guide.md`:

- explain when to use `@factorytest` instead of many separate top-level tests;
- emphasize that factory-test call arguments are evaluated normally even when
  filtered or skipped;
- remind the model not to call `@test` functions directly;
- describe `@testname`;
- show dense data-driven and golden-file patterns.

### IDE/syntax highlighting

Update syntax/highlighting assets under `dev/extras/`:

- recognize `@factorytest`;
- recognize `@testname`;
- preserve existing handling of `@test`, `@testonly`, and `@skip`.

## Compatibility

Existing tests continue to work.

Existing `@test`, `@testonly`, `@skip`, `--filter`, and coverage behavior is
preserved except:

- `@skip` becomes valid on `@factorytest` as well as `@test`;
- explicit calls/address-taking of `@test` become diagnostics if they are not
  already rejected today;
- result JSON and manifest formats may need version bumps to represent factory
  declarations and child results.

This proposal adds no production language surface. The new attributes are
test-only metadata.

## Risks

### Result model complexity

The result model becomes hierarchical. Text output is easy, but JSON consumers,
LSP, and debugging tools need to tolerate child results.

Mitigation: version the result format and keep top-level `tests` recognizable.

### Runtime filtering surprises

A child filter must run the parent test so runtime child calls can be
discovered. Parent setup code still executes, and child call arguments still
evaluate even if the child body is filtered.

Mitigation: document this clearly. Test factors should be cheap to produce;
expensive setup belongs inside the factory-test body.

### Duplicate-name stability

Ordinal suffixes can change when platform-specific/environment-specific cases
appear or disappear.

Mitigation: recommend unique `@testname` values for stable filters.

### Nested factory misuse

Because factory tests can be called indirectly, nested calls cannot be prevented
reliably by syntax alone.

Mitigation: detect nesting in the test runtime and report invalid child results.

### Coverage attribution

Generated wrapper code must not pollute source coverage.

Mitigation: use generated-declaration provenance rather than symbol-name
heuristics, consistent with existing test-only and coverage rules.

## Recommendation

This proposal should be accepted after review.

The design gives Camp a practical middle ground between one-static-function-per
test and a fully dynamic testing framework. It keeps top-level `@test`
functions simple and runner-owned while allowing runtime-discovered child cases
to report independently.

The most important design choice is that instrumentation belongs inside
`@factorytest` functions, not at call sites. That preserves ordinary call
semantics, supports helpers and callable values, and makes factory tests behave
like self-reporting test helpers.

The first implementation should stay strict:

- top-level factory functions only;
- no nested factory tests;
- no automatic golden discovery;
- no production visibility;
- no special call-site rewriting.

That narrower version provides most of the value needed for standard-library
and compiler golden test migration while keeping the bootstrap compiler changes
manageable.
