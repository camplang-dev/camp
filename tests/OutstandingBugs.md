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

Next bug number: BUG-390.

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

## BUG-389: An out-of-range integer literal passed in `base(...)` to a carried generic constructor parameter is not diagnosed

Date/Time: 2026-10-27 07:30 EDT

Summary:
An integer literal that does not fit its target type is rejected with "Integer literal '300' is outside the range of type 'byte'." for declarations, assignments, ordinary calls, `new` constructor arguments and `base(...)` calls whose parameter has a plain type. When the parameter is the carried form of a generic class parameter (`T: int` constructed as `byte`, so `int<byte>`), the same literal is accepted silently in `base(...)`, while an ordinary call to a function taking `int<byte>` and `new Seed<byte>(300)` are diagnosed.

Steps to Reproduce:

1. Declare `virtual class Seed<T: int> { T first; Seed(T first) { this.first = first; } }` and `virtual class Plain { byte first; Plain(byte first) { this.first = first; } }` (with the usual `malloc` and `free` externs).
2. Declare `sealed class A: Seed<byte> { A() { base(300); } }` and `sealed class B: Plain { B() { base(300); } }`.
3. Declare `void takes(int<byte> value) { }` and call `takes(300)` and `new Seed<byte>(300)` from `main`.
4. Build with `campc run`.

Expected:
Every occurrence of `300` is rejected with "Integer literal '300' is outside the range of type 'byte'." at the literal, including the one in `A`.

Actual:
The literals in `B`, in `takes(300)` and in `new Seed<byte>(300)` are diagnosed. The literal in `A` is not, and the program is accepted when the other three are removed.

Known Impact:
A value that does not fit is silently truncated into the base class field. Workaround: pass a value of the carrier type, or an in-range literal.
