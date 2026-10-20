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

Next bug number: BUG-321.

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

## BUG-320: A `while` condition that propagates a thrown error clears the error

Date/Time: 2026-10-20 01:45 EDT

Summary:
A call in a `while` condition whose `thrown` slot is forwarded to the caller's own `thrown` slot leaves the loop and returns, but the generated exit writes the default error first. The caller sees success even though the condition's call failed. The same call in an `if` condition, a declaration, or a `return` expression propagates the error unchanged.

Steps to Reproduce:

1. Declare `enum Fault { OK = 0, BAD }`, `int fail(thrown Fault error) { throw Fault.BAD; }`, and `int loop(thrown Fault error) { int count = 0; while (fail() > 0) count += 1; return count; }`.
2. Call `loop(catch fault)` from `main` with `Fault fault = default;` and log `(int)fault`.

Expected:
`1`: `fail()` threw `Fault.BAD`, and `loop` passes that error through its own `thrown` slot untouched.

Actual:
`0`. The generated loop body ends its error check with `(*error) = 0; return 0;`, which erases the error that `fail` stored.

Known Impact:
Errors raised while evaluating a loop condition are silently dropped. Workaround: call the throwing function before the loop or inside its body and test the result.
