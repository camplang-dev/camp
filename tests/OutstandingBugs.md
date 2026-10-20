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

Next bug number: BUG-328.

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

## BUG-327: Project-reference freshness disagrees with the content artifact cache

Date/Time: 2026-10-20 04:59 EDT

Summary:
Project-reference reuse checks timestamps even though native artifact reuse
validates content fingerprints. When unchanged source inputs have newer
timestamps than a valid archive, the reference is repeatedly marked rebuilding
and the child compiler then reports every output unchanged.

Steps to Reproduce:

1. Build a consumer of a static project reference with `--verbose`.
2. Advance the referenced source file's timestamp without changing its content.
3. Build the consumer twice more with `--verbose`.

Expected:
The recorded compiler, request, outputs, and input contents validate the current
reference, including when a timestamp is in the future.

Actual:
The reference repeatedly reports rebuilding while the child reports unchanged
outputs. The static project-reference graph regression also fails its final
unchanged-build assertion on affected artifacts.

Known Impact:
Unnecessary child builds and inconsistent incremental-build behavior. Repeated
builds do not repair a timestamp that remains newer than the unchanged output.
