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

Next bug number: BUG-145.

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

## BUG-121: Re-exported dependency types are misnamed in module source

Date/Time: 2026-09-03 America/Toronto

Summary:
Using a dependency type through its local name after an `export Type as Alias`
declaration can emit a doubly prefixed C type name. The source declaration is
accepted, but the generated C cannot compile because the emitted type name was
never declared.

Steps to Reproduce:

1. In a namespace, write `export Std::Allocator as LocalAllocator;`.
2. Declare a local `LocalAllocator*` variable or parameter.
3. Build the module with the native C emitter.

Expected:
The generated C uses the declared ABI spelling for the re-exported allocator
type and compiles successfully.

Actual:
The generated C uses an undeclared double-prefixed type such as
`NamespaceLocalAllocator` when the available declaration is the standard
allocator ABI type.

Known Impact:
Implementations cannot use a re-export alias as a source type until this is
fixed. Use the original dependency type name internally and reserve the
re-export for an exported signature that requires the dependency ABI type.

## BUG-141: Aggregate return storage is undeclared with a later `finally` array

Date/Time: 2026-09-07 15:48 EDT

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

## BUG-142: `finally` owner in a loop returns after the first iteration

Date/Time: 2026-09-07 16:52 EDT

Summary:
An owner declared with `finally delete` inside a loop body can cause the native
C lowering to return from the containing function after the first iteration.
The source has no return statement in the loop. Cleanup must end the local
owner's iteration scope and then continue the loop.

Steps to Reproduce:

1. Compile and run this Camp source through the native C backend:

   ```camp
   int reproduce(within allocator)
   {
       int completed = 0;
       for (int index = 0; index < 4; index++)
       {
           int[] values = new int[1] finally delete;
           values[0] = index;
           completed++;
       }
       return completed;
   }
   ```

2. Inspect the generated C or observe the returned value.

Expected:
`reproduce` destroys each iteration's local array and returns `4` after all four
iterations.

Actual:
The generated C emits `return _return...;` after cleanup at the end of the
first loop body, so the function returns the default result before the loop
continues.

Known Impact:
Loops that use `finally` ownership can silently skip later iterations. Declare
the owner outside the loop when practical, or use an explicit nested scope with
deterministic deletion after each iteration until loop cleanup lowering is
corrected.

## BUG-143: Test construction of an internal class doubles its emitted name

Date/Time: 2026-09-07 17:03 EDT

Summary:
When a test source constructs an `internal` class declared in another source
file of the same module, native lowering can emit a doubled namespace prefix
for the constructor call. The class is correctly visible to the test source,
but the generated C calls an undeclared constructor symbol.

Steps to Reproduce:

1. Put this declaration in one source file of a Camp module:

   ```camp
   namespace Sample;
   internal sealed class TraceSink
   {
       TraceSink() { }
   }
   ```

2. Put this maintained `@test` in another source file of the same module:

   ```camp
   requires (TEST_MODULE);
   namespace Sample;
   @test void reproduce(within Allocator* allocator, thrown Assertion*)
   {
       TraceSink* sink = new TraceSink() finally delete;
       assert(sink != null);
   }
   ```

3. Compile the module's native test artifact.

Expected:
The generated test calls the constructor using the same emitted symbol declared
for `TraceSink`.

Actual:
The generated C can call an undeclared doubled name such as
`SampleSampleTraceSink_op_initnew`, while the declared symbol has only one
namespace prefix. Clang rejects the test artifact.

Known Impact:
Valid same-module tests cannot instantiate internal helper classes. Keep the
test operation behind a same-file helper or avoid direct construction until
constructor name lowering is corrected.

## BUG-144: `within` allocator is corrupted through a nested command dispatch

Date/Time: 2026-09-07 17:25 EDT

Summary:
A `within` allocator can be corrupted when a command dispatcher calls a nested
function that then calls another `within`-aware helper. The helper receives a
non-null invalid allocator pointer and crashes while allocating, although a
direct call to that helper with the same caller allocator succeeds.

Steps to Reproduce:

1. Create a `within`-aware helper that allocates a copied string.
2. Call it from a second `within`-aware function selected by a `switch` in a
   third `within`-aware command dispatcher.
3. Invoke the dispatcher from a test with a valid allocator.

Expected:
Every nested call receives the original valid allocator context.

Actual:
The generated native call can pass an invalid non-null allocator address. The
callee crashes while evaluating the allocator's `alloc` slot.

Known Impact:
Nested CLI command handlers that perform owned allocations can crash before
returning a status. Keep the command path out of qualification until allocator
context lowering is corrected.
