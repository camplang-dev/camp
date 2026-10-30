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

Next bug number: BUG-450.

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

## BUG-449: Static archive membership cache can accept externally changed archives

Date/Time: 2026-10-30 11:00 EDT

Summary:
Static archive membership manifests are accepted based on their timestamp and
expected object names without checking the archive contents. An external archive
change can therefore leave an obsolete membership manifest trusted when the
archive timestamp does not advance past the manifest timestamp.

Steps to Reproduce:

1. Build a static project reference B that depends on static reference A.
2. Insert A's object into B's archive with the native archiver, retaining B's
   original archive timestamp.
3. Change A's source and rebuild the application that references A and B.

Expected:
Static archive membership validation detects the foreign object and rebuilds B
with only its own objects, as documented in the compiler artifact cache rules.

Actual:
B can reuse the corrupted archive because its object manifest is newer. The WSL
full suite failed `Static_project_reference_rebuilds_dirty_transitive_archive`
with B's archive containing both `b.o` and `a.o` despite a manifest listing `b.o`.

Known Impact:
Incremental builds can retain stale transitive objects after an archive is
externally replaced or modified. Removing the archive or its `.objects` manifest
forces membership validation or rebuilding.
