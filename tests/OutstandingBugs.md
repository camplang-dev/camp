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

Next bug number: BUG-337.

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

## BUG-336: Equivalent output-directory forms invalidate a native artifact cache

Date/Time: 2026-10-22 14:28 EDT

Summary:
A library built directly is rebuilt when later consumed as a project reference,
even when its sources, configuration, compiler, and resolved output directory
are unchanged. Cache identity includes the original output prefix and whether
it was treated as a direct directory instead of the resolved artifact directory.

Steps to Reproduce:

1. Build a static library with its ordinary output prefix.
2. Consume that library as a static project reference from a shared library.
3. Build the consumer without changing any library input.

Expected:
The existing library artifact is reused when both requests resolve to the same
artifact directory. Different resolved output directories remain distinct.

Actual:
The request fingerprint changes and the static archive is regenerated. Both
profiles of `Shared_project_reference_absorbs_static_dependency` reproduce it.

Known Impact:
Unchanged static dependencies are rebuilt when switching between standalone
and project-reference builds.
