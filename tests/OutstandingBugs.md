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

Next bug number: BUG-320.

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

## BUG-319: Inline string constant cannot use another inline string as an interpolation hole

Date/Time: 2026-10-20 00:45 EDT

Summary:
An interpolated inline string folds constant text and character holes. A hole that names another inline string constant in the same file is rejected as if the symbol came from another file.

Steps to Reproduce:

1. Write `inline string PREFIX = $"a{"bc"}";` and `inline string BOTH = $"{PREFIX}-x";` in one file with a trivial `main`.
2. Compile the file.

Expected:
`BOTH` folds to `abc-x`; a hole naming an inline string is constant text.

Actual:
`error: Symbol 'PREFIX' is declared in another file but is not exported.`

Known Impact:
Constant text cannot be built from other constants. Workaround: repeat the text.
