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

Next bug number: BUG-336.

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

## BUG-335: Callable ascription does not preserve the overload selector name or the pre-selector shape

Date/Time: 2026-10-22 12:20 EDT

Summary:
For a callable surface that participates in an overload family, the semantics
require a compatible candidate to preserve whether the declaration is an
overload-family entry, the selector parameter name, the selector position, and
the pre-selector shape including parameter names. Virtual overrides enforce the
selector name and the family category, but callable newtype ascription does not:
a candidate with a renamed selector, with an ordinary parameter in the selector
position, or with renamed pre-selector parameters is accepted. A moved selector
and a changed pre-selector type are rejected.

Steps to Reproduce:

1. Declare `newtype fn void Setter(const char[] key, overload int value);`.
2. Declare `void renamedSelector(const char[] key, overload int other) : Setter {}`.
3. Declare `void ordinaryEntry(const char[] key, int value) : Setter {}`.
4. Declare `void renamedBefore(const char[] name, overload int value) : Setter {}`.
5. Compile.

Expected:
Each of steps 2 to 4 is rejected: the selector is renamed, the declaration is
not an overload-family entry, and the pre-selector parameter name changed.

Actual:
All three declarations are accepted. An override that renames the selector is
rejected with `Override 'setInt' must preserve the base declaration's overload selector name.`

Known Impact:
A callable newtype for an overload family can be satisfied by a declaration
with a different selector contract, so the shared keys and metadata the
selector is meant to keep stable can differ between the target and the
candidate.
