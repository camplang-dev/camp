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

Next bug number: BUG-308.

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

## BUG-307: An else after an unbraced if that is itself the body of a loop is reported as detached

Date/Time: 2026-10-18 13:53 EDT

Summary:
When the body of a `while` or `for` is an unbraced `if` and that `if` has an `else`, the `else` is not attached to the `if`. The compiler reports it as an `else` with no `if`.

Steps to Reproduce:

1. Write `while (i < 4) if (i++ == 1) continue; else total += 5;` with the declarations it needs.
2. Compile the file.
3. Repeat with a `for` loop in place of the `while`.

Expected:
The `else` belongs to the nearest `if`, as it does inside a braced body.

Actual:
`(no line,column) error: 'else' must follow an if statement.` is reported with no position, for both loop kinds.

Known Impact:
Dangling-else bodies that C accepts cannot be written without braces, and the diagnostic has no location.
