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

Next bug number: BUG-101.

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

## BUG-100: Indexing an imported array-returning call overflows the compiler stack

Date/Time: 2026-09-02 15:48 EDT

Summary:
Lowering an index expression whose receiver is an array returned by a public
method imported through a static project reference recurses indefinitely in
expression lowering. The compiler terminates with a native stack overflow
instead of compiling the valid source or reporting a diagnostic.

Steps to Reproduce:

1. Create a static library with a public struct method such as
   `public const byte[] payload()`.
2. Reference that library's `.campbuild` from a second project.
3. In the consumer, evaluate an indexed call result such as
   `value.payload()[0]`.
4. Build or test the consuming project.

Expected:
The imported source-level array result is lowered once into its ABI components,
and the index expression reads the requested element.

Actual:
The compiler repeatedly enters `LowerExpression` through indexed params/array
component expansion until the native process reports a stack overflow and
aborts. No Camp diagnostic is produced.

Known Impact:
Valid public array-returning APIs cannot be indexed directly by consumers using
static `.campbuild` references. Because the compiler crashes without a
diagnostic, callers cannot safely rely on this source pattern.
