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

Next bug number: BUG-173.

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

## BUG-171: A cross-project export projection emits a mismatched generated type name

Date/Time: 2026-09-24 00:00 EDT

Summary:
A module that references another project and projects one of that project's
`public` types into its own exported API, using the documented Export
Projections feature, produces Camp source that compiles but fails during
native compilation. The C emitter names the projected type using the
consuming module's own generated namespace prefix (for example `BFormat` for
a projection declared in module `B`) instead of either the owning module's
already-generated name (for example `AFormat`) or a bridging typedef between
the two; no declaration for the consuming module's spelling is ever emitted,
so the generated code references an undefined type.

Separately, and independently of the native-compilation failure above,
renaming the projection makes the Camp-level compile itself impossible: no
spelling of the type is accepted in the exporting signature. The source type
name is rejected as "exposes non-exported type", and the projected alias name
is rejected as usable only by the "exported API surface", each error pointing
at the other as the required fix.

Steps to Reproduce:

1. Project `a`, `a.campbuild`: `src/*.camp`.

   `src/a.camp`:
   ```camp
   namespace A;

   public enum Format
   {
   	ONE,
   	TWO,
   }
   ```

2. Project `b`, `b.campbuild`: `--project-reference ../a/a.campbuild:static` then
   `src/*.camp`.

   `src/b.camp` (bare projection, no rename):
   ```camp
   using A;

   namespace B;

   export Format;

   export void useFormat(Format value)
   {
   }
   ```

3. Build `b.campbuild`.

4. Separately, replace step 2's file with a renamed projection and observe a
   different, Camp-level failure instead:
   ```camp
   using A;

   namespace B;

   export Format as BFormat;

   export void useFormat(Format value)
   {
   }
   ```

Expected:
Step 3 either produces a working native build in which `useFormat` correctly
uses module `A`'s underlying generated representation for `Format`, or the
compiler rejects the unsupported cross-project projection with a clear
diagnostic during the Camp-level compile rather than letting it reach native
compilation. Step 4 is accepted using some consistent, satisfiable spelling of
the projected type in `useFormat`'s signature.

Actual:
Step 3's Camp-level compile succeeds, but native compilation fails:

```
error: unknown type name 'BFormat'; did you mean 'AFormat'?
```

`b_api.h` and the private header both declare `useFormat` in terms of
`BFormat`, but no `BFormat` type is defined anywhere in the generated output;
only `AFormat` (module `A`'s own generated name for the same declaration) is
defined, in `a_api.h`.

Step 4 fails the Camp-level compile itself, and the two reported errors are
mutually unsatisfiable: using the plain source name `Format` reports
`Exported declaration 'useFormat' exposes non-exported type 'Format'`, while
using the projected name `BFormat` in its place reports `Export projection
name 'BFormat' is only used by the exported API surface; use the source type
name 'A::Format' within this module.`

Known Impact:
A module cannot export a declaration whose signature uses a type owned by a
separately referenced project, even after adding a same-name or renamed
export projection for that type exactly as documented. `public` (artifact-
internal, non-exported) visibility is unaffected: the identical signature
shape compiles and links correctly when the declaration is `public` instead
of `export`, since no C-ABI boundary or generated-name projection is
involved. Workaround: keep any declaration whose signature needs a dependency
project's type `public` rather than `export`, or wrap the dependency's values
behind a locally declared Camp-owned type instead of projecting the foreign
type directly.

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
