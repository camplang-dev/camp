# Outstanding Bugs

Before adding a bug, verify the behavior against the language semantics and
confirm that it is in fact a bug. The explanation should include repro
instructions and should be written in general terms, without reference to a
particular file, package, or product.

Bugs should be added to this file one at a time. After each bug is added, commit
`OutstandingBugs.md` to the repo and include the new bug number in the commit
message. Add new bugs to the bottom of this file so existing bug numbers and
review history remain stable.

When committing a change that fixes a bug, or is related to a bug, reference the
bug number in the commit message. The final commit that fixes a bug, or the only
commit if there is just one, should delete the bug from this file and include
that `OutstandingBugs.md` change in the same commit.

Next bug number: BUG-161.

## Bug Template

Use this shape when adding a new bug:

```md
## BUG-###: <brief summary>

Date/Time: YYYY-MM-DD HH:MM <timezone>

Summary:
<One or two paragraphs describing the bug in generalized terms.>

Steps to Reproduce:

1. <Step one.>
2. <Step two.>

Expected:
<What should happen according to the language semantics or documented compiler behavior.>

Actual:
<What actually happens. Include diagnostics or observed output when useful.>

Known Impact:
<Who or what is affected, and any known workaround if one exists.>
```

## BUG-141: Aggregate return storage is undeclared with a later `finally` array

Date/Time: 2026-09-07 15:48 EDT

Status:
More information required - cannot reproduce on current HEAD using the listed
repro instructions. Do not mark fixed until a reproducible case is found or the
original failure is otherwise confirmed resolved.

Summary:
A function returning a struct can emit invalid C when it has return paths before
and after a later owned array local declared with `finally delete`. The cleanup
lowering assigns aggregate return values through a generated return temporary,
but that temporary is never declared.

Steps to Reproduce:

1. Compile this Camp source through the native C backend:

   ```camp
   struct Result
   {
       int value;
   }

   Result reproduce(bool early, bool later, within allocator)
   {
       if (early)
           return { 1 };
       int[] values = new int[1] finally delete;
       if (later)
           return { 2 };
       return { 3 };
   }
   ```

2. Compile the generated C.

Expected:
The generated function declares aggregate return storage, performs cleanup on
every applicable path, and returns the selected `Result` value.

Actual:
The generated cleanup paths assign and return a generated `_return...`
identifier that was never declared. The native compiler reports use of an
undeclared identifier.

Known Impact:
Valid aggregate-returning functions with this cleanup shape cannot complete a
native build. Explicitly delete the array on each post-allocation return path
instead of using `finally delete` until cleanup lowering is corrected.

## BUG-148: Conditional return expression is evaluated when its branch is false

Date/Time: 2026-09-07 20:18 EDT

Status:
Open - reproduced on current HEAD on 2026-09-10.

Summary:
An expression used as the return value of a conditional branch can be lowered
before the branch condition is tested. This violates the source control flow:
side effects in the return expression occur even when the branch is false.

Steps to Reproduce:

1. Compile a method with this shape:

   ```camp
   bool appendWhenRequested(bool requested, char[] output, uint* offset)
   {
       if (requested)
           return append(output, offset, " ") && append(output, offset, "spec");
       return true;
   }
   ```

   Here `append` advances `offset` and writes to `output`.
2. Call `appendWhenRequested(false, ...)` and inspect the output and offset.

Expected:
Neither `append` call executes, and the method returns `true` without changing
the caller-owned output.

Actual:
The generated C can evaluate the `append` conjunction before it tests
`requested`, leaving the output changed even though it returns `true` from the
false branch.

The same ordering defect also applies to a method call guarded by a null check:
the C emitter can materialize a chained call before testing its nullable
receiver. For example, a body shaped as `if (owner != null) { Result result =
owner.lookup(); use(result); }` can invoke `lookup()` with a null receiver.

Known Impact:
Any conditionally returned expression with side effects can run on a path where
the source program does not execute it, including a method invocation guarded
by a nullable receiver check. Keep the side-effecting call in a block-local
statement after the guard, or split the false guard into an early return, until
lowering preserves branch evaluation order.

## BUG-155: Escaped class layout can crash every isolated test process

Date/Time: 2026-09-11 02:15 EDT

Status: Unconfirmed, not able to reproduce.

Summary:
Adding owned escaped array fields to an existing escaped class can make every
isolated `@test` process exit with signal 11 before the test body reports a
result. The affected class has an ordinary allocator-capturing destructor that
deletes its owned arrays; the new fields are default-initialized and need not be
used by the test. This is a compiler correctness failure because extending a
private escaped-owner representation must preserve valid object layout and
default destruction.

Steps to Reproduce:

1. In a static Camp test module, define or extend an escaped class with an
   allocator field, several owned `escaped byte[]`/`escaped char[]` fields, a
   fixed array of small plain records, and a destructor that deletes the owned
   array fields within the captured allocator.
2. Construct and destroy the class from an `@test`, then run the module through
   `campc test` with its normal isolated-test harness.

Expected:
The harness enters the selected test and construction/destruction of the
default-initialized owner completes normally.

Actual:
The generated test executable exits with code 139 before reporting a result.
The test results record every selected test as a `test-runner-error`, even when
the test does not access the newly added fields.

Investigation:

The initial generic repro passed on `8c362d802ff1b48266017cdc6209d3401a00f798`
with compiler version
`v0.11.0-preview.1+8c362d802ff1b48266017cdc6209d3401a00f798` on macOS.
Its generated C included expanded pointer-and-length fields for each escaped
array, the fixed record storage, retained allocator storage, complete object
zero-initialization, and null-guarded array destruction.

The enlarged investigation fixture was then run from a separate worktree at
the report-era compiler baseline `9204e2ce09c1a27a6798bd28f8b48611d8e01907`.
It retained the owned escaped arrays and added fixed byte sidecars of 3072 and
5120 elements, a fixed char sidecar of 8192 elements, and 256 fixed two-uint
records. Running
`dotnet run --project src/campc/campc.csproj -- test tmp/bug-155-large.camp --out-dir tmp/bug-155-large-out --name bug-155-large`
passed `defaultEscapedOwnerDestroys` with exit 0. The isolated native harness
also passed with one 18,488-byte allocation, one free, and no live allocations.

No compiler change, regression, or bug classification is warranted without the
original crashing source snapshot, generated C, compiler binary or revision, or
exact test invocation and environment that produced exit 139.

Reported Impact:
The original report describes compiler modules that need an escaped owner to
retain portable sidecar sections as unable to extend that owner safely. This
impact remains unverified pending the missing reproduction evidence.

## BUG-157: An attribute before a `within` parameter fails to parse

Date/Time: 2026-09-16 15:40 EDT

Summary:
A parameter attribute placed immediately before a `within` parameter, in
either the implicit (`within allocator`) or explicit (`within Allocator*
allocator`) form, fails to parse. The parameter-parsing dispatcher checks for
the `within` keyword (and the `sizeof`/`vtableof` special forms) as the very
first token of the parameter, before any attribute list has been consumed;
attributes are only recognized later, inside the ordinary value-parameter
declarator path. When an attribute appears first, the dispatcher's `within`
keyword check never matches (the first token is `@`, not `within`), so the
parameter falls through to ordinary value-parameter parsing, which then tries
to parse the leftover `within` token as a type name.

Steps to Reproduce:

1. Compile this Camp source (implicit form):

   ```camp
   class Allocator
   {
   }

   void f(@symbol("a") within allocator)
   {
   }
   ```

2. Or this Camp source (explicit form):

   ```camp
   class Allocator
   {
   }

   void f(@symbol("a") within Allocator* allocator)
   {
   }
   ```

Expected:
The attribute binds to the `within` parameter like it does to any other
parameter kind, and the parameter is otherwise parsed normally.

Actual:
The implicit form reports `Unknown type 'within'.` in addition to a diagnostic
about the attribute itself. The explicit form fails much more severely,
cascading into a long run of unrelated parser-recovery errors (`Expected
')'.`, `Expected identifier.`, `Expected declaration or import/export
declaration.`, etc.) that make the real defect hard to see from the output
alone.

Known Impact:
Any parameter attribute cannot be combined with a `within` parameter in
either form. No workaround exists other than not attaching an attribute to a
`within` parameter. Discovered while implementing proposal 021 (factory
tests): the proposal requires diagnosing `@testname` when placed on a
`within` parameter of a `@factorytest` function, but that specific invalid
source shape cannot currently be written at all, so it cannot be proven with
a compiling golden fixture until this parser gap is fixed.

## BUG-159: A language-service test builds its search text with a platform-dependent line ending, so it silently fails to run its assertion on Windows

Date/Time: 2026-09-16 17:20 EDT

Status: Reproduced on current HEAD on Windows/MSVC. Passes on macOS.

Summary:
`LanguageServiceTests.Symbol_query_handles_late_overload_selectors` builds
one of its test inputs by taking a C# raw string literal (`text`) and
calling `string.Replace` to substitute a small snippet built with
`Environment.NewLine`. On Windows, `Environment.NewLine` is `"\r\n"`, but
the C# compiler normalizes line terminators inside raw string literals to
`"\n"` regardless of the source file's own line-ending style. The snippet
built with `Environment.NewLine` therefore never actually occurs inside
`text` on Windows, so the `Replace` call is a silent no-op: the returned
string is unchanged from the original, without the intended `override `
keyword ever being inserted. The test then searches that unchanged text for
the `override ` marker and fails with an unhandled exception, rather than a
normal assertion failure, before any real assertions run.

This is a test-infrastructure defect (a bug in the test's own text
construction), not a compiler behavior bug: it does not indicate any problem
with the language service's overload-selector completion logic itself.

Steps to Reproduce:

1. On a Windows host, run
   `Camp.Compiler.Tests.LanguageServiceTests.Symbol_query_handles_late_overload_selectors`
   from the `Camp.Compiler.TestRunner` xUnit suite.
2. Compare with the same test run on macOS or Linux.

Expected:
The test passes on every platform, exercising the `override ` completion
trigger the same way regardless of host OS.

Actual:
On Windows the test throws
`System.InvalidOperationException: Marker 'override ' was not found.` from
its own `PositionOf`/`PositionAfter` text helpers, because the `override `
snippet was never inserted into the search text in the first place. On
macOS the test passes.

Known Impact:
This specific test cannot verify the "insert `override` then request
completions" scenario on Windows; every other scenario in the same test
method still runs normally, and this does not affect production compiler
behavior. Likely fix: build the substituted snippet with the same line
terminator the raw string literal actually uses at runtime (`"\n"`) instead
of `Environment.NewLine`, or normalize both sides before comparing/
replacing.

## BUG-160: A `try`/`catch` statement's catch-declared variable is not zero-initialized, so a callee that clears it only on an explicit return can spuriously trigger the catch block

Date/Time: 2026-09-16 20:10 EDT

Summary:
The lowering for a `try`/`catch` statement declares the catch-bound local
(the storage a `thrown` out-parameter is passed into) without initializing
it. Separately, the lowering that clears a `thrown` out-parameter on a
callee's success path only runs immediately before an explicit `return`
statement; a callee whose success path instead falls off the end of the
function body (a common shape for a void function that only assigns the
out-parameter conditionally) never clears it. Together, calling such a
callee inside a `try` block can read the catch-declared variable's
uninitialized stack contents afterward, and whatever garbage happens to be
there determines whether the `catch` block spuriously runs, independent of
whether the callee actually reported a failure.

Steps to Reproduce:

1. Compile and run this Camp source through the native C backend:

   ```camp
   enum Err
   {
       OK = 0,
       BAD
   }

   void poisonStack()
   {
       int a = -1;
       int b = -1;
       int c = -1;
   }

   void mayFail(bool shouldFail, thrown Err error)
   {
       if (shouldFail)
           throw BAD;
   }

   bool checkOnce(bool shouldFail)
   {
       try
       {
           mayFail(shouldFail);
       }
       catch (Err e)
       {
           return false;
       }
       return true;
   }

   @test
   void probe(thrown Assertion* assertion)
   {
       poisonStack();
       assert(checkOnce(false) == true);
   }
   ```

2. Run the compiled test.

Expected:
`checkOnce(false)` returns `true`, because `mayFail(false)` never throws.

Actual:
The generated C declares `Err e;` with no initializer inside `checkOnce`,
and the immediately preceding `poisonStack` call leaves nonzero garbage in
that stack slot. `mayFail(false, &e)` returns without touching `e` (its
success path has no matching clear), so the subsequent `if ((e != 0))`
check reads the leftover garbage and spuriously jumps to the `catch`
block, making `checkOnce(false)` incorrectly return `false`. Removing the
`poisonStack()` call does not fix the underlying defect; it only makes the
outcome depend on whatever the stack already contained, so the same
source can appear to pass or fail from run to run depending on unrelated
prior stack usage.

Known Impact:
Any `try`/`catch` around a call whose callee can reach the end of its body
without an explicit `return` on the success path is vulnerable to spurious
catch-block execution driven by uninitialized stack memory, independent of
whether the callee actually threw. Known workaround: avoid the `try`/
`catch` statement for such callees and instead declare an explicitly
zero-initialized local paired with the `catch <var>` argument-modifier form
at the call site, which does not go through the same uninitialized
declaration path.

