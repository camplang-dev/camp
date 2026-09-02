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

Next bug number: BUG-100.

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

## BUG-099: Conditional implementing types fail availability conformance

Date/Time: 2026-09-02 15:15 EDT

Summary:
A type under a file-wide availability requirement cannot implement an
always-available abstract class or interface. Its otherwise unconditional
overrides are reported as less available than the inherited members, including
when the conditional declarations are inactive in the current build.

Steps to Reproduce:

1. Declare an always-available public abstract class with a public abstract
   method in a library source file.
2. In another source file, add a file-wide `requires (TEST_MODULE);` and declare
   a concrete test helper derived from that class with an unconditional
   `override` of the method.
3. Include both files in the library `.campbuild`.
4. Build that library as a production static project reference.

Expected:
The conditional concrete type and its implementation are absent when its
requirement is false. Where the containing type is available, an override with
no stronger declaration requirement covers the inherited slot throughout the
type's availability domain.

Actual:
The production dependency build reports that the override must be at least as
available as the inherited member, then reports that the concrete class does
not implement the abstract member.

Known Impact:
Test helper types guarded by the required file-level `TEST_MODULE` condition
prevent their owning libraries from being consumed as production
`.campbuild` references. Moving the helper into production source would weaken
the intended test-module boundary.
