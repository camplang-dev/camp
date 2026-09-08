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

Next bug number: BUG-154.

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
