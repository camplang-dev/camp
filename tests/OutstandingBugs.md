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

Next bug number: BUG-251.

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

## BUG-250: Native AOT compiler crashes when serializing artifact cache records

Date/Time: 2026-10-06 13:48 EDT

Summary:
The published Native AOT compiler uses reflection-based JSON serialization for
artifact cache records despite disabling reflection-based serialization. A
native build aborts while writing its cache record, and cache reads use the same
unsupported serialization path.

Steps to Reproduce:

1. Publish `campc` with the release publishing project's Native AOT settings.
2. Create a source containing `export int main() { return 0; }`.
3. Run the published compiler with `campc run <source> --out-dir <output>`.

Expected:
The compiler writes its artifact cache and runs the executable successfully.

Actual:
The compiler aborts with an unhandled `InvalidOperationException` stating that
reflection-based serialization has been disabled, originating in
`CampArtifactCache.TryWrite`.

Known Impact:
Native builds and test artifact caching fail with the published AOT compiler.
Managed development builds with JSON reflection enabled do not reproduce it.
Archive smoke scripts may incorrectly report a failed native check as skipped.
