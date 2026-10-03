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

Next bug number: BUG-218.

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

## BUG-217: An unnamed prep slot in a callable type does not parse

Date/Time: 2026-10-03 18:17 EDT

Summary:
A function, delegate or callable newtype type may list its parameters without
names, and unnamed slots with modifiers such as `out` or `const` parse. An
unnamed slot carrying the `prep` modifier does not: the parser reports a series
of syntax errors. The same slot parses when it is given a name, and the
return-position spelling `fn prep char[](int)` also parses.

Steps to Reproduce:

1. Declare `nuint render(int value, prep char[] buffer = default) { return (nuint)value; }`.
2. In a function body, write `fn nuint(int, prep char[]) indirect = render;`.
3. Compile.

Expected:
The declaration compiles. Prep is a callable contract modifier on the slot, and
the documented callable types spell it without a name, for example
`fn nuint(prep char[]) formatter;`.

Actual:
`error: Expected ')'.`, `error: Expected ';'.` and `error: Expected statement.`
at the `prep` slot.

Known Impact:
Prep-bearing callable types must name the prep slot
(`fn nuint(int, prep char[] buffer)`) or use the return-position spelling.
