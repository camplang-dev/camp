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

Next bug number: BUG-151.

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

## BUG-142: `finally` owner in a loop returns after the first iteration

Date/Time: 2026-09-07 16:52 EDT

Status:
More information required - cannot reproduce on current HEAD using the listed
repro instructions. Do not mark fixed until a reproducible case is found or the
original failure is otherwise confirmed resolved.

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

Status:
More information required - cannot reproduce on current HEAD using the listed
repro instructions. Do not mark fixed until a reproducible case is found or the
original failure is otherwise confirmed resolved.

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

## BUG-148: Conditional return expression is evaluated when its branch is false

Date/Time: 2026-09-07 20:18 EDT

Status:
More information required - cannot reproduce on current HEAD using the listed
repro instructions. Do not mark fixed until a reproducible case is found or the
original failure is otherwise confirmed resolved.

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

## BUG-150: Conditional slice return omits the returned length

Date/Time: 2026-09-08 08:01 EDT

Summary:
A function returning an array slice through a conditional expression can emit
the selected data pointer without assigning the companion result length. The
native caller consequently observes a zero-length nonempty slice. This violates
the array-return ABI, which requires the elements and length components to be
returned together.

Steps to Reproduce:

1. Save this source as `repro.camp`:

   ```camp
   requires (TEST_MODULE);

   namespace Repro;

   string text = "A";

   const char[] value() => text == null ? "" : text;

   @test
   void conditionalSliceReturnPreservesLength(thrown Assertion*)
   {
       const char[] result = value();
       assert(result.length == 1);
   }
   ```

2. Save this project as `repro.campbuild` beside it:

   ```text
   --sourcefile-root .
   --out-dir bin
   --name repro
   repro.camp
   ```

3. Run `campc test repro.campbuild` with
   `v0.11.0-preview.1+f6feb41bd542c54884c9c29eded7db3575a22da4`.
4. Inspect the generated C for `value` if needed. The function returns the
   selected pointer but does not assign `*result_length`.

Expected:
The conditional expression returns both components of the selected slice, and
the test observes `result.length == 1`.

Actual:
The generated function returns the pointer to `"A"` without setting the result
length. The test observes a zero-length slice and fails. Callers that assume a
non-null pointer has a valid length may instead fail later or access invalid
state.

Known Impact:
Conditional expressions cannot safely be used as the return expression for an
array or slice result. An explicit `if` with a direct return in each branch is a
safe, low-debt workaround until array-return lowering assigns both ABI
components on every return path.
