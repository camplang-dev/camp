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

Next bug number: BUG-214.

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

## BUG-210: Member access through a const pointer emits C value access

Date/Time: 2026-10-02 22:35 EDT

Summary:
Member access through a pointer with a top-level `const` qualifier emits C's
value-member operator (`.`) instead of its pointer-member operator (`->`).
The Camp expression is valid, but the generated C fails to compile.

Steps to Reproduce:

1. Compile and run this program:

   ```camp
   struct Item
   {
       int value;
   }

   int read(Item* const value) => value.value;

   export int main()
   {
       Item item = { .value = 7 };
       return read(&item);
   }
   ```

Expected:
Camp uses `.` for member access through pointers. The program compiles and
exits with status 7; the C backend emits `value->value`.

Actual:
The C backend emits `value.value` for the `Item * const` parameter. Clang
rejects it with `member reference type 'Item *const' is a pointer; did you
mean to use '->'?`.

Known Impact:
Reading struct members through top-level const pointers fails native
compilation, including when the pointer comes from dereferencing a pointer
to a const pointer. Copying the dereferenced struct to a value local before
reading its member avoids this defect. Confirmed with the Release compiler
at `403e9d13`, before the BUG-208 and BUG-209 fixes.

## BUG-211: Qualified name requires an import of its namespace

Date/Time: 2026-10-03 12:38 EDT

Summary:
A name qualified with its namespace (`Namespace::name`) is rejected unless the
file also imports that namespace. The language semantics say qualification
searches the named namespace directly and does not require a `using`; the
`global::` form likewise names the root namespace without an import.

Steps to Reproduce:

1. Declare `namespace Util { public int twice(int value) { return value * 2; } }`
   in one source file.
2. In a second source file of the same program, with no `using` declaration, write:

   ```camp
   export int main()
   {
       return Util::twice(1) - 2;
   }
   ```
3. Compile and run the program.

Expected:
The program compiles and exits with status 0. Qualifying a function, type or
enum value with its full namespace path works without an import, including a
multi-segment path such as `Geometry::Metrics::perimeter(1)` for
`namespace Geometry::Metrics`. A namespace alias (`using Geometry::Metrics as M;`)
still does not make `Metrics::perimeter` valid.

Actual:
Compilation fails with `Symbol 'Util::twice' could not be found.` The same
happens for a multi-segment qualifier and for types. The error disappears only
after adding `using Util;`. A declaration in the same source file as the use is
found without any import, so the defect is visible only across source files.

Known Impact:
Qualification cannot be used as an alternative to an import when the two
namespaces overlap in vocabulary, which is the case the language reference
recommends it for. Adding the import works around it. Confirmed with the Release
compiler at HEAD.

## BUG-212: Same-named struct types in different namespaces are treated as one type

Date/Time: 2026-10-03 12:38 EDT

Summary:
Type identity ignores the namespace of a struct. Two structs with the same simple
name in different namespaces are accepted as interchangeable by semantic
analysis, and the program fails only when the generated C is compiled, because
the emitted C types have different names. Structs with different simple names
are correctly rejected.

Steps to Reproduce:

1. Compile this program:

   ```camp
   namespace A { public struct Box { int side; } }
   namespace B { public struct Box { int side; } }

   export int main()
   {
       A::Box a = default;
       B::Box b = a;
       return b.side;
   }
   ```
2. Replace the declaration with an assignment (`b = a;`), or pass `a` to a
   function that takes `B::Box`, and compile again.

Expected:
Types with the same simple source name are distinct when their effective
namespaces differ, and compatibility checks compare resolved type definitions.
Each variant fails with a conversion diagnostic at the offending expression,
as it does for `struct First {}` and `struct Second {}` (`Declaration
initializer cannot convert 'First' to 'Second'.`).

Actual:
Semantic analysis succeeds. The native build then fails with a clang error such
as `initializing 'BBox' with an expression of incompatible type 'ABox'`.

Known Impact:
A mismatch between same-named types goes undiagnosed at the Camp level, so the
error points at generated C. Using distinct simple names avoids it.

## BUG-213: Unqualified type name matching two imported namespaces silently picks one

Date/Time: 2026-10-03 12:38 EDT

Summary:
When two imported namespaces each declare a type with the same simple name, an
unqualified use of that name is accepted and resolves to one of them without a
diagnostic. The language reference says overlapping imports make an unqualified
name ambiguous and that it should be qualified or imported selectively. The
compiler already rejects the same situation for functions.

Steps to Reproduce:

1. In one source file declare
   `namespace Left { public struct Box { int leftSide; } }` and
   `namespace Right { public struct Box { int rightSide; } }`.
2. In a second source file write:

   ```camp
   using Left;
   using Right;

   export int main()
   {
       Box box = default;
       box.leftSide = 1;
       return box.leftSide - 1;
   }
   ```
3. Compile and run the program.

Expected:
Compilation fails because `Box` matches declarations in two imported
namespaces. Qualifying the name (`Left::Box`) or importing only one namespace
makes it compile. The same applies to an enum name or enum value.

Actual:
The program compiles and exits with status 0; the first imported namespace's
`Box` is chosen silently. Two functions of the same name in the two namespaces
are rejected with `Multiple candidates found for call target`, so the behavior
is inconsistent between functions and types.

Known Impact:
A program can bind to the wrong type when an import is added that happens to
overlap an existing one, and the choice depends on import order. Qualifying the
name avoids it.
