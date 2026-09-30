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

Next bug number: BUG-183.

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

## BUG-175: Linux shared artifacts cannot link static dependencies with global data

Date/Time: 2026-09-28 02:15 EDT

Summary:
The built-in `gcc-linux-x64` target compiles static Camp dependencies without
position-independent code, then links their archives into shared artifacts.
When a required object references an exported global, GNU `ld` rejects its
`R_X86_64_PC32` relocation. The shared artifact never builds.

Steps to Reproduce:

1. Create a static Camp project with this source:

   ```camp
   namespace PicRepro;
   export int counter = 42;
   export int readCounter() => counter;
   ```

2. Create a shared Camp project with
   `--project-reference ../lib/lib.campbuild:static` and this source:

   ```camp
   using PicRepro;
   namespace PicReproConsumer;
   export int read() => readCounter();
   ```

3. Build the shared project using the built-in `gcc-linux-x64` target.

Expected:
The compiler produces a usable shared library from supported project
references, arranging for any static objects included in it to be linkable as
position-independent code.

Actual:
The static dependency compiles without `-fPIC`; `ld` reports
`relocation R_X86_64_PC32 against symbol 'PicRepro_counter' can not be used
when making a shared object; recompile with -fPIC` and fails the link.

Known Impact:
Linux shared Camp libraries with static dependencies containing exported
global data cannot build using the default target. A private target with PIC
static flags might avoid this specific relocation, but it does not make the
standard target's shared-library contract work and has not been validated as
a general workaround for transitive or prebuilt static dependencies.

## BUG-176: Unchosen conditional string-array arm is indexed eagerly

Date/Time: 2026-09-28 04:18 EDT

Summary:
When a conditional expression returns a counted text view and one arm indexes
an array of strings, the native C backend evaluates the index expression before
testing the condition. A guard in the condition therefore cannot protect an
invalid index in the unchosen arm.

Steps to Reproduce:

1. Run this standalone source with `campc run repro.camp` on the native C
   backend:

   ```camp
   const char[] choose(string[] values, uint raw)
   {
       return raw == 0 || raw > values.length ? default : values[raw - 1];
   }

   export int main()
   {
       string[] values = new string[1] finally delete;
       values[0] = "seven";
       return choose(values, 0).length == 0 ? 0 : 1;
   }
   ```

2. Inspect the emitted C for `choose` if the process does not fault on the
   host. The backend computes `values[(raw - 1)]` before the C conditional.

Expected:
With `raw == 0`, the conditional chooses `default` without evaluating or
indexing the other arm; the program returns 0.

Actual:
The emitted C indexes `values[raw - 1]` before checking the condition. On
macOS x86-64 the standalone program exited with code 139.

Known Impact:
A bounds-guarded conditional returning counted text from a string array can
read out of bounds or crash. An explicit `if` that returns before indexing is
a safe source workaround.

## BUG-178: Member access on a slice of a fixed array emits invalid C

Date/Time: 2026-09-30 11:50 EDT

Summary:
Reading `.elements` (or another view member) directly from a slice expression
over a fixed inline array emits an invalid C expression. The slice bounds are
written as a subscript, `array[0, N]`, and the member access is applied to that
subscript. Binding the slice to an array-view local first works.

Steps to Reproduce:

1. Build this standalone source with `campc build repro.camp` on the native C
   backend:

   ```camp
   export int main()
   {
       fixed char[4] text;
       text[0] = 'a';
       text[1] = '\0';
       const char* pointer = text[..].elements;
       return pointer[0] == 'a' ? 0 : 1;
   }
   ```

2. Inspect the clang diagnostics for the generated C.

Expected:
The program compiles, and `text[..].elements` is the address of the first
element of `text`; the program returns 0.

Actual:
The generated C contains `text[0, 4].elements`, and clang reports "member
reference base type 'char' is not a structure or union".

Known Impact:
Any code that takes `.elements` (or otherwise reads a view member) straight from
a slice of a fixed array fails in the C compiler. Workaround: bind the slice to a
view first, for example `char[] view = text[..]; const char* pointer = view.elements;`.

## BUG-179: Static consumer calls an export whose native symbol starts with underscores

Date/Time: 2026-09-30 12:05 EDT

Summary:
A static library can export a function whose effective native symbol starts
with two underscores, for example through `@symbol("__name")`. The library's Camp
API lists the function, so a consumer can call it from Camp source, but the
library's generated C API header omits it ("No exported declarations" when it is
the only export). The consumer's generated C then calls an undeclared function
and the C compiler rejects it.

Steps to Reproduce:

1. Create `lib/lib.camp`:

   ```camp
   namespace Q;

   @symbol("__renamed_sym")
   export void renamed(int value)
   {
   }
   ```

   with `lib/lib.campbuild` containing `--out-dir bin`, `--name qlib`, and
   `lib.camp`.
2. Create `app/app.camp`:

   ```camp
   export int main(string[] args)
   {
       Q::renamed(2);
       return 0;
   }
   ```

   with `app/app.campbuild` containing `--out-dir bin`, `--name app`,
   `--artifact exec`, `app.camp`, and
   `--project-reference ../lib/lib.campbuild:static`.
3. Run `campc build app/app.campbuild`.

Expected:
The consumer builds and links. Renaming the symbol to one without a leading
double underscore, for example `renamed_sym`, does build.

Actual:
clang reports "call to undeclared function '__renamed_sym'" for the generated
consumer C. A `:shared` project reference to the same library builds.

Known Impact:
Camp callers of a statically linked export whose native symbol starts with `__`
cannot compile. Workaround: choose a native symbol without a leading double
underscore, or have the consumer declare its own `extern` for the symbol and link
the library through a native `--reference`.

## BUG-181: Conditional expression with array-view call arms drops or over-evaluates its arms

Date/Time: 2026-09-30 14:55 EDT

Summary:
A conditional expression whose arms are calls returning an array view (for
example `int[]`) is lowered incorrectly. As a declaration initializer the whole
initializer is omitted, so the declared view's pointer and length are never
assigned. As an assignment or `return` value both arms are called instead of only
the selected arm, so side effects of the unselected call run. Using the result
directly as a member-access target, such as `.length`, emits invalid C. A
conditional whose arms are plain array-view variables lowers correctly.

Steps to Reproduce:

1. Declaration initializer. Build and run this program with
   `campc run cond_decl.camp --show-errorlevel`:

   ```camp
   int[] pick(int[] view) { return view; }

   export int main()
   {
       int[] a = [7, 8, 9];
       int[] b = [4, 5];
       int[] s = true ? pick(a) : pick(b);
       return (int)s.length;
   }
   ```

2. Assignment. Build and run this program the same way:

   ```camp
   int[] tick(int[] view, int* calls)
   {
       *calls = *calls + 1;
       return view;
   }

   export int main()
   {
       int[] a = [7, 8, 9];
       int[] b = [4, 5];
       int calls = 0;
       int[] s = b;
       s = true ? tick(a, &calls) : tick(b, &calls);
       return calls;
   }
   ```

3. Member access. Build this program:

   ```camp
   int[] pick(int[] view) { return view; }

   export int main()
   {
       int[] a = [7, 8, 9];
       int[] b = [4, 5];
       return (int)(true ? pick(a) : pick(b)).length;
   }
   ```

Expected:
1. Exits with 3, the length of `a`.
2. Exits with 1: only the selected arm is evaluated, like any other conditional.
3. Builds and exits with 3.

Actual:
1. The generated C declares `int *s; uintptr_t s_length;` and never assigns them,
   then returns `(int)(s_length)`. The exit code is an uninitialized value (216
   on one macOS run).
2. Exits with 2: both `tick` calls run before the conditional selects a result.
3. clang rejects the generated C with "member reference base type 'int *' is not a
   structure or union" for `(true ? pick(a, ...) : pick(b, ...)).length`.

Known Impact:
Conditional expressions that select between calls returning array views produce
undefined values (as an initializer), repeated side effects (as an assignment or
return value), or uncompilable C (as a member-access target). Workaround: use an
`if`/`else` statement that assigns the view, or select between plain view
variables and call afterwards.

## BUG-182: Returning a view of a local array literal is accepted and dangles

Date/Time: 2026-09-30 15:05 EDT

Summary:
The lifetime semantics give an array literal a scoped lifetime fact, because its
backing storage is local to the enclosing function. Returning a pointer to a
local variable is rejected ("Return expression cannot return a pointer-bearing
value tied to local storage"), but returning an array view backed by a local
array literal is accepted without a diagnostic. The generated C backs the literal
with a C compound literal, which has automatic storage, so the caller receives a
dangling pointer and reads undefined data.

Steps to Reproduce:

1. Create `return_literal.camp`:

   ```camp
   int[] make()
   {
       int[] x = [1, 2, 3];
       return x;
   }

   export int main()
   {
       int[] v = make();
       return v[0];
   }
   ```

2. Run `campc run return_literal.camp --show-errorlevel`.
3. For comparison, run the same shape with a pointer to a local:

   ```camp
   int* make()
   {
       int v = 1;
       return &v;
   }

   export int main()
   {
       int* p = make();
       return *p;
   }
   ```

Expected:
Step 2 reports a lifetime diagnostic for returning a value tied to local storage,
as step 3 does. `return [1, 2, 3];` and returning a local view of a literal are
both covered.

Actual:
Step 2 compiles. The generated function contains
`int *x = (int []){1, 2, 3}; ... return x;`, so the returned pointer refers to
storage that ends when `make` returns. The program may appear to work or read
garbage depending on the stack (it printed `32765` for a larger example on
macOS). Step 3 is rejected as expected.

Known Impact:
Code that returns a view of a local array literal compiles and has undefined
behavior at run time. Workaround: have the caller own the storage and return a
view of it, or return a view of allocated storage.
