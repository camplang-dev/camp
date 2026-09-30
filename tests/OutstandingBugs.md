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

Next bug number: BUG-193.

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

## BUG-185: Indirect `fn` call does not expand optional or delegate arguments

Date/Time: 2026-09-30 16:45 EDT

Summary:
Optional (`T?`) and `delegate` parameters are expanded into several C parameters
(a presence flag and value, or a function pointer and context). A direct call
expands the matching arguments correctly. A call through an `fn` value with the
same parameter types passes each such argument as a single C argument, so the
generated call has too few arguments and clang rejects it. Array parameters, which
also expand, are handled correctly in the same indirect call.

Steps to Reproduce:

1. Optional parameter. Build `campc build ind_opt.camp`:

   ```camp
   int f(int? value) { return 7; }

   export int main()
   {
       int? a = (int?)3;
       fn int(int?) p = f;
       return p(a);
   }
   ```

2. Delegate parameter:

   ```camp
   int s(int x) { return x; }
   int f(delegate int(int) d) { return d(7); }

   export int main()
   {
       delegate int(int) d = (int v) => s(v);
       fn int(delegate int(int)) p = f;
       return p(d);
   }
   ```

3. For comparison, calling `f(a)` or `f(d)` directly in either program builds, and an
   `fn int(int[])` indirect call with an array argument builds and runs.

Expected:
Steps 1 and 2 build. They exit with 7.

Actual:
clang reports "too few arguments to function call, expected 2, have 1" for
`return p(a);` and for `return p(d);`. A function pointer with several expanded
parameters (an array, an optional and a delegate) reports "expected 6, have 4".

Known Impact:
Indirect calls through `fn` values cannot take optional or delegate arguments.
Workaround: call the function directly, or wrap the indirect call in a function
that takes only scalar and array parameters.

## BUG-186: Integral representation-generic fields and parameters are emitted as `void*` without conversions

Date/Time: 2026-09-30 17:05 EDT

Summary:
A type parameter with an integral representation constraint, such as `T: uint`,
`T: int`, `T: byte` or `T: nint`, is lowered to `void*` in generated C for fields
and parameters. Reading or writing such a value with an ordinary integer does not
insert a conversion, so clang rejects the generated assignment or initialization
("incompatible integer to pointer conversion" and "incompatible pointer to integer
conversion"). The documented `CounterMap<T: uint>` example compiles only because
the literal `0` is a valid C null pointer constant.

Steps to Reproduce:

1. Build and run `campc run slot.camp --show-errorlevel`:

   ```camp
   struct Slot<T: uint> { T value; }

   export int main()
   {
       Slot<uint>* slot = stackalloc Slot<uint>();
       slot.value = 9;
       uint v = slot.value;
       return (int)v;
   }
   ```

2. The same failure occurs for a generic class field:

   ```camp
   class Box<T: int>
   {
       T value;
       Box(T value) { this.value = value; }
   }

   export int main()
   {
       Box<int>* box = new Box<int>(17);
       int v = box.value;
       delete box;
       return v;
   }
   ```

Expected:
Both programs build. The first exits with 9 and the second with 17.

Actual:
The generated C declares the field as `void* value;` and the class constructor as
`Box_create(void* value)`. clang reports "incompatible integer to pointer
conversion assigning to 'void *' from 'int'" for `slot->value = 9;` and
"incompatible pointer to integer conversion initializing 'int' with an
expression of type 'void *'" for `int v = box->value;`. Other integral
constraints (`byte`, `long`, `nuint`, `nint`) fail the same way.

Known Impact:
Generic aggregates whose type parameter has an integral representation constraint
cannot hold or return any value except zero. Workaround: declare the field with
the concrete integer type instead of `T`.

## BUG-187: Explicit casts from an integral representation-generic value are rejected

Date/Time: 2026-09-30 18:55 EDT

Summary:
A value whose type is a type parameter with an integral representation
constraint, such as `T: int`, cannot be explicitly cast to any type. The
compiler reports "Invalid cast from 'T' to 'int'" even for a cast to the
constraint's own carrier type. The carrier type is known, so an explicit cast to
any type that the carrier can be cast to should be accepted, exactly as if the
value had the carrier type.

Steps to Reproduce:

1. Build `campc build cast.camp --nostdlib --out-dir out` with:

   ```camp
   int toInt<T: int>(T value)
   {
       return (int)value;
   }

   export int main()
   {
       return toInt<int>(5) - 5;
   }
   ```

2. Replace the cast with `(long)value` and the return type with `long`. The same
   diagnostic is reported for `'long'`.

Expected:
Both programs build. A cast from `T: int` behaves as a cast from `int` to the
target type, so the first program exits with 0.

Actual:
Both fail with `error: Invalid cast from 'T' to 'int'.` (respectively `'long'`)
at the cast expression.

Known Impact:
A generic function over an integral representation type cannot convert its value
to a concrete integer type. No source-level workaround exists other than not
using the generic.

## BUG-188: Declaring a constructor or destructor requires visible `malloc` and `free` even when nothing allocates

Date/Time: 2026-09-30 19:00 EDT

Summary:
When no function named `malloc` or `free` is visible (for example with the
standard library disabled), merely declaring a constructor on a struct or class
is an error, "Symbol 'malloc' could not be found", reported at the constructor.
Declaring a destructor reports "Symbol 'free' could not be found" in the same way.
The program never uses `new` or the default heap, and a constructor call on value
or `stackalloc` storage does not allocate, so no allocation function should be
needed. The declarations should be accepted; only a `new` or `delete` that
actually uses the default heap should require `malloc` and `free`.

Steps to Reproduce:

1. Build `campc build ctor.camp --nostdlib --out-dir out` with:

   ```camp
   struct Box
   {
       int tag;
       Box(int t) { this.tag = t; }
   }

   export int main()
   {
       return 0;
   }
   ```

2. Change `struct` to `class`. The result is the same.
3. Replace the constructor with `~Box() {}`. The error is now "Symbol 'free' could not
   be found." at the destructor.
4. Add `extern void* malloc(nuint size);` and `extern void free(void* ptr);` at the
   top of the first program. It then builds, and `Box b = Box(4);` also runs correctly.

Expected:
The programs build without any allocation function, because nothing allocates.

Actual:
`ctor.camp(4,2): error: Symbol 'malloc' could not be found.` at the constructor, or
`error: Symbol 'free' could not be found.` at the destructor.

Known Impact:
Freestanding and `--nostdlib` programs must declare `malloc` and `free` externs
just to declare a constructor or destructor, even if they never allocate.
Workaround: declare the two externs.

## BUG-189: A call on a derived class pointer uses the base method's default argument instead of the override's

Date/Time: 2026-09-30 19:20 EDT

Summary:
A class method that overrides a virtual method may declare its own default
argument value. A call made through the derived class type selects the derived
declaration as the source callable surface, so the omitted argument should take
the derived declaration's default. The compiler instead inserts the default from
the base declaration. The same call shape through an interface implemented by a
class correctly uses the default declared on the implementing class's own
surface, so the behavior is inconsistent. A call made through the base type
correctly uses the base default.

Steps to Reproduce:

1. Run `campc run override_default.camp --nostdlib --show-errorlevel` with:

   ```camp
   virtual class Base
   {
       virtual int scale(int value, int factor = 3) { return value * factor; }
   }

   sealed class Derived: Base
   {
       override int scale(int value, int factor = 4) { return value * factor; }
   }

   export int main()
   {
       Derived* derived = stackalloc Derived();
       return derived.scale(2);
   }
   ```

Expected:
`derived.scale(2)` binds to `Derived.scale`, whose default is 4, so the program
exits with 8.

Actual:
The program exits with 6, using the base declaration's default of 3.

Known Impact:
Default arguments declared on an override are silently ignored for calls through
the derived type. Workaround: do not declare different defaults on an override.

## BUG-190: Generic construction through an interface lifecycle contract is not supported

Date/Time: 2026-09-30 19:40 EDT

Summary:
The language reference describes generic APIs that require `T: implements Managed`
for an interface that declares a constructor, and that ask for `vtableof(T: Managed)`
to create and destroy values. Destroying such a value through the contract works,
but constructing one with `new T()` (or `stackalloc T()`) is accepted by analysis
and then fails during C emission with "C emission does not yet support expression
node ConstructionExpression." When the interface constructor also declares value
parameters, for example `Managed(int value, within allocator)` with `new T(value)`,
analysis rejects the construction earlier with "No constructor or create method for
'T' accepts 1 argument(s)."

Steps to Reproduce:

1. Run `campc run create.camp --nostdlib --show-errorlevel` with:

   ```camp
   extern void* malloc(nuint size);
   extern void free(void* ptr);

   class Allocator
   {
       void* alloc(nuint size) { return malloc(size); }
       void free(void* ptr) { free(ptr); }
   }

   interface Managed
   {
       Managed(within allocator);
       ~Managed(within allocator);
   }

   sealed class Buffer: Managed
   {
       Buffer(within allocator) {}
       ~Buffer(within allocator) {}
   }

   T* createOne<T: implements Managed>(Allocator* allocator, sizeof(T), vtableof(T: Managed))
   {
       return within (allocator) new T();
   }

   export int main()
   {
       Allocator* allocator = stackalloc Allocator();
       Buffer* buffer = createOne<Buffer>(allocator);
       within (allocator) delete buffer;
       return 0;
   }
   ```

2. Replace `new T()` with `stackalloc T()` (and drop the `return`). The same error
   is reported.
3. Change the interface constructor to `Managed(int value, within allocator)`, the
   implementing constructor to `Buffer(int value, within allocator)`, and the call to
   `new T(value)` (adding an `int value` parameter to `createOne`). The error is now
   "No constructor or create method for 'T' accepts 1 argument(s)."
4. For comparison, a generic function that only deletes (`within (allocator) delete value;`
   on a `T*` parameter with `vtableof(T: Managed)`) builds and runs.

Expected:
The program builds and exits with 0. The generic construction invokes the
constructor recorded in the `Managed` lifecycle vtable.

Actual:
`C emission does not yet support expression node ConstructionExpression.` for the
parameterless form, and `No constructor or create method for 'T' accepts 1
argument(s).` for the form with a value parameter.

Known Impact:
Generic code cannot create values through an interface constructor contract.
Workaround: have the caller construct the value and pass a `T*`, or pass a factory.

## BUG-191: `delete` of a retained-allocator object frees with the global `free`, not the retained allocator

Date/Time: 2026-09-30 19:55 EDT

Summary:
A class whose constructor declares `within this.allocator` remembers the
allocator it was created with. The owning `delete` path must destroy the object
and then free the complete object pointer through that retained allocator. The
compiler instead emits a call to the global `free` after the destructor, so the
retained allocator's `free` is never called and memory obtained from a custom
allocator is released to the wrong owner. Construction is correct: the allocator's
`alloc` is called.

Steps to Reproduce:

1. Run `campc run retained.camp --nostdlib --show-errorlevel` with:

   ```camp
   extern void __intrinsic_log_i32(int value);
   extern void* malloc(nuint size);
   extern void free(void* ptr);

   interface Allocator
   {
       void* alloc(nuint size);
       void* realloc(void* ptr, nuint newSize);
       void free(void* ptr);
   }

   sealed class CountingAllocator: Allocator
   {
       CountingAllocator() {}
       ~CountingAllocator() {}
       void* alloc(nuint size): Allocator
       {
           __intrinsic_log_i32(1);
           return malloc(size);
       }
       void* realloc(void* ptr, nuint newSize): Allocator { return null; }
       void free(void* ptr): Allocator
       {
           __intrinsic_log_i32(2);
           free(ptr);
       }
   }

   class Counter
   {
       int value;
       Counter(int initial, within this.allocator) { this.value = initial; }
       ~Counter() {}
   }

   export int main()
   {
       CountingAllocator* counting = stackalloc CountingAllocator();
       Allocator* allocator = counting;
       Counter* item = within (allocator) new Counter(9);
       delete item;
       return 0;
   }
   ```

   `__intrinsic_log_i32` must be linked in (for example `--reference` to a library
   that provides it).

Expected:
The program logs `1` (allocation) and then `2` (free through the retained
allocator).

Actual:
Only `1` is logged. The generated delete sequence is
`(Counter_op_delete(target), free((void *)(target)))`, calling the global `free`.

Known Impact:
Retained-allocator objects are released with the wrong deallocator, which is
undefined behavior for any allocator that does not wrap the global heap.

## BUG-192: A type parameter's own `implements` constraint is not accepted when forwarded to another generic call

Date/Time: 2026-09-30 20:20 EDT

Summary:
A generic function constrained with `T: implements Interface` cannot pass its own
`T` on to another generic function that has the same constraint. The compiler
reports "Type 'T' does not implement interface 'Interface'", as if `T` were an
unconstrained type, even though the caller's constraint guarantees it. The
interface vtable capability (`vtableof(T: Interface)`) is also available in the
caller to forward.

Steps to Reproduce:

1. Run `campc run forward.camp --nostdlib --show-errorlevel` with:

   ```camp
   interface Counter { int read(); }

   struct First: Counter
   {
       int value;
       int read(): Counter { return this.value; }
   }

   int readOne<T: implements Counter>(T* value, vtableof(T: Counter))
   {
       return value.read();
   }

   int forward<T: implements Counter>(T* value, vtableof(T: Counter))
   {
       return readOne<T>(value);
   }

   export int main()
   {
       First first = {};
       first.value = 4;
       return forward<First>(&first) - 4;
   }
   ```

2. Replace `readOne<T>(value)` with `readOne(value)` (inferred type argument). The same
   error is reported.

Expected:
The program builds and exits with 0. The constraint on `forward`'s `T` satisfies
the constraint of `readOne`, and the vtable capability is forwarded.

Actual:
`forward.camp(13,19): error: Type 'T' does not implement interface 'Counter'.`

Known Impact:
Generic helpers with interface constraints cannot be composed. No source-level
workaround exists other than not splitting the generic function.
