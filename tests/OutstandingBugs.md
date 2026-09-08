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

Next bug number: BUG-149.

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

## BUG-146: Conditional expression eagerly evaluates a nullable receiver branch

Date/Time: 2026-09-07 19:05 EDT

Summary:
A conditional expression that selects an alternate result when a receiver is
null can still lower a method call on that null receiver before evaluating the
condition. The source expression is required to avoid dereferencing the null
branch, but generated native code performs the dereference unconditionally.

Steps to Reproduce:

1. Compile and run this Camp source through the native C backend:

   ```camp
   class Value
   {
       int getNumber() => 1;
   }

   int reproduce(Value* value) => value == null ? 0 : value.getNumber();

   @test
   void nullBranchDoesNotCallMember(within Allocator* allocator, thrown Assertion*)
   {
       assert(reproduce(null) == 0);
   }
   ```

2. Run the generated native test artifact.

Expected:
The null condition selects `0` and does not call `getNumber`.

Actual:
The generated native code evaluates the member call before selecting the
conditional result, dereferences null, and exits with an access violation.

Known Impact:
Nullable receivers inside conditional expressions can crash native artifacts.
Use an explicit `if`/`return` guard before the member call until lowering keeps
conditional branches lazy.

## BUG-147: Successful nested `finally` scope returns before outer cleanup

Date/Time: 2026-09-07 19:33 EDT

Summary:
When a function owns a resource with `finally delete`, enters a nested scope
that owns another resource with `finally delete`, and completes the nested
scope with a `return`, native lowering can return immediately after the nested
cleanup. This bypasses the outer cleanup that must run before the function
returns.

Steps to Reproduce:

1. Compile and run this Camp source through the native C backend:

   ```camp
   class Value { }

   int reproduce(bool nested, within allocator)
   {
       Value* outer = new Value() finally delete;
       if (nested)
       {
           Value* inner = new Value() finally delete;
           return 1;
       }
       return 2;
   }

   @test
   void nestedReturnCleansBothOwners(within Allocator* allocator,
       thrown Assertion*)
   {
       assert(reproduce(true) == 1);
   }
   ```

2. Run the generated native test artifact with allocation tracking enabled.

Expected:
Both `inner` and `outer` are destroyed before `reproduce` returns.

Actual:
Generated C performs the inner cleanup and returns its generated temporary
directly from the nested scope. The outer cleanup label is bypassed, leaving
the outer allocation live.

Known Impact:
Any successful return from a nested `finally` scope can leak outer owned
resources. Restructure the function so the nested scope records a result and
falls through to the outer cleanup, or use explicit deterministic deletion
before returning, until cleanup lowering chains through every enclosing scope.

## BUG-148: Conditional return expression is evaluated when its branch is false

Date/Time: 2026-09-07 20:18 EDT

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

Known Impact:
Any conditionally returned expression with side effects can run on a path where
the source program does not execute it. Split the false guard into an early
return, then evaluate the expression unconditionally only on the remaining
path until lowering preserves branch evaluation order.
