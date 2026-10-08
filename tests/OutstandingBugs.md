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

Next bug number: BUG-279.

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

## BUG-278: An optional cast argument loses its specified component at an erased reference

Date/Time: 2026-10-08 09:43 America/Toronto

Summary:
A cast that creates an optional value is accepted as an `in void<P>?` argument,
but the generated call omits the optional's specified component. Other optional
rvalues, including a function result, are expanded correctly at this boundary.

Steps to Reproduce:

1. Run `campc run repro.camp --nostdlib --show-errorlevel` with compiler
   `c6825ae6` on the following source:
   ```camp
   int read(in void<int>? value) => value.specified ? value.value : -1;
   export int main() => read((int?)7) == 7 ? 0 : 1;
   ```
2. Inspect the generated C call and compare passing a function result
   from `int? make() => 7;` instead of the cast expression.

Expected:
The optional cast creates a specified value containing seven. The caller supplies
addresses for both its payload and specified components, and the program exits
with zero. Proposal 024 sections 3 and 7 preserve those optional components.

Actual:
C declares `read(const void *value, bool *value_specified)` but calls
`read(&(int){(int)(7)})`. Clang rejects the call for having too few arguments.

Known Impact:
Optional cast rvalues cannot be passed to these erased reference parameters.
A function returning the same optional value passes both components correctly
and safely preserves an rvalue-reference test's purpose.
