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

Next bug number: BUG-281.

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

## BUG-280: Target-typed enum member lookup fails when a global is the left comparison operand

Date/Time: 2026-10-10 20:54 EDT

Summary:
In an equality comparison, an unqualified enum member on the right resolves from
the left operand's enum type when the left operand is a local variable, but not
when the left operand is a global variable of the same enum type.

Steps to Reproduce:

1. Declare `enum Color: int { RED, GREEN }` and a global `Color g;`.
2. In a function, write `Color l = RED; if (l != RED) return 1;` and
   `if (g != RED) return 2;`.
3. Build the file.

Expected:
Both comparisons resolve `RED` through the enum type of the left operand.

Actual:
The local comparison compiles. The global comparison reports
`Symbol 'RED' could not be found.`

Known Impact:
Programs must copy the global to a local or qualify the member (`Color.RED`).

