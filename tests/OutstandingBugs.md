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

Next bug number: BUG-327.

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


## BUG-326: The documentation does not describe a body that assigns its own thrown slot

Date/Time: 2026-10-20 17:50 EDT

Summary:
The language reference says successful paths write the default error value to a function's `thrown` slot automatically. The compiler skips that automatic write when the function body assigns its own `thrown` parameter directly or passes it as a `catch` target, so the value the body stored is what the caller sees. The behavior is intentional and tests depend on it, but no document states it.

Steps to Reproduce:

1. Declare `enum Fault { OK = 0, BAD }` and `void inner(thrown Fault error) { throw Fault.BAD; }`.
2. Write `void outer(thrown Fault error) { inner(catch error); }` and call it with `outer(catch fault)`.
3. Read the error-handling chapter of the language reference for what `fault` holds afterwards.

Expected:
The reference states that a body which assigns its own `thrown` slot, or passes it as a `catch` target, takes over the slot: the automatic success write is not performed after it, so the stored value is what the caller observes.

Actual:
The reference describes only the automatic success write. `fault` is `BAD` at run time, which the reference does not explain.

Known Impact:
Implementers of other compilers cannot tell from the reference whether a trailing automatic write must overwrite the value the body stored. Workaround: none needed for users, since the behavior is consistent.
