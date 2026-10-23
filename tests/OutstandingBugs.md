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

Next bug number: BUG-346.

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

## BUG-345: `(new)` on a transformed prep call is rejected inside a loop as implicit stack allocation

Date/Time: 2026-10-23 01:20 EDT

Summary:
A transformed prep call written without `(new)` allocates its prepared result in
implicit dynamic stack storage, and using one inside a loop is rejected with a
diagnostic that recommends `(new)` for heap-allocated prepared storage. The
same diagnostic is also reported when the call is written with `(new)` directly
before it, so the recommended fix does not remove the error. The rejection
should apply only to a call whose prepared result is implicitly stack allocated.

Steps to Reproduce:

1. Declare a function with an omitted-by-default prep parameter, for example
   `nuint render(int value, prep char[] buffer = default)`.
2. Declare allocation support (`malloc` and `free`) so that `(new)` is allowed.
3. In a function, write `for (int i = 0; i < 2; i++) { char[] heap = (new) render(3); }`.
4. Compile it. Repeat with the assignment form `outer = (new) render(3);` and
   with a `while` loop.

Expected:
The loop body compiles. `(new)` selects allocated lifetime with the ordinary
allocation context, so no implicit dynamic stack allocation happens on the
repeated path, and the language documentation names `(new)` as the way to keep
a prepared result produced in a loop. The same call without `(new)` is still
rejected inside the loop.

Actual:
Each loop body reports "Prepared result cannot use implicit dynamic stack
allocation inside a loop; use explicit stackalloc outside the repeated path or
heap allocation with '(new)'." at the call, even though the call is written with
`(new)`. The call is analyzed again as an implicit transformed call and the loop
check runs without regard to the allocation prefix.

Known Impact:
Prepared results cannot be allocated on the heap from inside a loop, which is the
case the diagnostic points users to. The only workaround is to move the call into
a separate function that is not itself called in a loop body of the same
function, or to supply an explicit buffer argument.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md` (Prepared Result
Lifetimes) already says `(new)` uses ordinary allocated lifetime. No change is
needed unless the fix decides that `(stackalloc)` is also allowed in a loop, in
which case that section should say so.
