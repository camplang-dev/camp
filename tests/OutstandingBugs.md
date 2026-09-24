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

Next bug number: BUG-174.

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

## BUG-172: A `@factorytest` body cannot name a cross-file type for a local variable

Date/Time: 2026-09-24 00:00 EDT

Summary:
Inside a `@factorytest` function's body, declaring a local variable whose
explicit type name is declared in a different file of the same project fails
to resolve, even though the identical type is visible and used correctly
everywhere else: as a parameter type of the same `@factorytest` function, as
the explicit type of a local variable inside an ordinary `@test` in the same
file, and implicitly through `auto` type inference inside the same
`@factorytest` body. The failure is specific to spelling a cross-file type
name for an explicit local-variable declaration inside a `@factorytest`
body.

Steps to Reproduce:

1. `src/types.camp`:
   ```camp
   namespace Camp;

   internal struct Foo
   {
   	int x;
   }

   internal Foo makeFoo() => { 42 };
   ```

2. `tests/repro_tests.camp`:
   ```camp
   requires (TEST_MODULE);

   namespace Camp;

   @test
   void parentTest(within Allocator* allocator, thrown Assertion*)
   {
   	childFactory("case1");
   }

   @factorytest
   void childFactory(@testname const char[] name, within Allocator* allocator, thrown Assertion*)
   {
   	Foo value = makeFoo();
   	assert(value.x == 42);
   }
   ```

3. Run the project's tests.

Expected:
`childFactory` compiles and runs identically to an ordinary `@test` with the
same body; `Foo` is visible inside a `@factorytest` body exactly as it is
inside an ordinary `@test` body in the same file, since both are declared in
the same project.

Actual:
```
tests/repro_tests.camp(14,2): error: Type 'Foo' is declared in namespace 'Camp' but is not imported by this file.
tests/repro_tests.camp(14,21): error: Call result cannot convert 'CampFoo' to '#UNRESOLVED(Foo)'.
tests/repro_tests.camp(14,21): error: Declaration initializer cannot convert 'CampFoo' to '#UNRESOLVED(Foo)'.
tests/repro_tests.camp(15,15): error: Member 'x' could not be found on type '#UNRESOLVED(Foo)'.
```

Three closely related shapes each avoid the failure, confirming it is specific
to an explicit local-variable type name inside the lowered `@factorytest`
body:

- Replacing `Foo value = makeFoo();` with `auto value = makeFoo();` compiles
  and passes.
- Moving the identical body into an ordinary `@test` in the same file (no
  `@factorytest`, no `@testname` parameter) compiles and passes.
- Adding a `Foo`-typed parameter to `childFactory` and passing `makeFoo()` from
  the caller, instead of declaring the local inside the body, compiles and
  passes.

Known Impact:
A `@factorytest` function cannot declare a local variable using an explicit
type name from another file of its own project; only types already visible
through a parameter, or through `auto` inference, work. This narrows how
factory-test bodies can be written for any case that needs an explicit local
type name for a cross-file result, such as a discovery or comparison result
struct returned by a helper function. Workaround: declare such locals with
`auto` instead of the explicit type name, or receive the value through a
parameter instead of constructing it inside the body.

## BUG-173: A `finally` cleanup double-frees when a later `try`/`catch` in the same function returns from its `catch`

Date/Time: 2026-09-24 00:00 EDT

Summary:
A `finally { delete x; }` registered for a local pointer, followed later in
the same function by an ordinary `try`/`catch` whose `catch` block returns,
runs the `finally` cleanup twice: once for the early return inside `catch`,
and once more for the function's own exit, freeing the same pointer a second
time. The bug requires no exception to actually be thrown from inside the
function's own `try` for the throwing call itself to matter; it is the mere
presence of a `catch` block that returns, later in the same function than an
earlier `finally` registration, that causes the double free. Neither the
return type of the function nor the type of the `finally`-managed local
matters: a plain `bool`-returning function with a single owned pointer
reproduces it.

This may be the same underlying defect as a previously observed, then
unreproducible, "invalid allocator free: pointer was already freed" noted
once during the investigation that led to BUG-166 and never pinned down at
the time; this report supersedes that earlier inconclusive observation with a
minimal, reliable repro.

Steps to Reproduce:

1. Compile and run this Camp source as a test:

   ```camp
   requires (TEST_MODULE);

   namespace Camp;

   internal class Marker
   {
   }

   bool tryIterateMissingDirectory(string directory, within Allocator* allocator)
   {
   	Marker* marker = new Marker();
   	finally { delete marker; }
   	if (marker == null)
   		return false;

   	try
   	{
   		foreach (string name in FileSystem.iterateDirectory(directory))
   		{
   		}
   	}
   	catch (IoError error)
   	{
   		return false;
   	}
   	return true;
   }

   @test
   void iterateMissingDirectoryReturnsFalse(within Allocator* allocator, thrown Assertion* assertion)
   {
   	bool result = tryIterateMissingDirectory("definitely-does-not-exist-xyz");
   	assert(!result);
   }
   ```

2. `definitely-does-not-exist-xyz` does not exist relative to the test
   binary's working directory, so `FileSystem.iterateDirectory` throws
   `IoError`, `catch` returns `false`, and the test asserts on that `false`.

Expected:
`marker` is freed exactly once when `tryIterateMissingDirectory` returns,
regardless of which return statement is taken, and the test passes.

Actual:
```
failed: Camp::iterateMissingDirectoryReturnsFalse
  at tests/repro_tests.camp:26 invalid allocator free: pointer was already freed
```

Known Impact:
Any function that registers a `finally` cleanup for an owned pointer and
later, in the same function, uses an ordinary `try`/`catch` whose `catch`
returns, double-frees that pointer. This is a plain correctness defect
independent of any particular API; `FileSystem.iterateDirectory` is only the
concrete throwing call used to reach it here. No workaround is known other
than avoiding `finally` in a function that also contains a `try`/`catch`
returning from `catch`, for example by replacing the `finally` with explicit
`delete` calls on every return path instead.
