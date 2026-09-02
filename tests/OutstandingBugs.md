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

Next bug number: BUG-098.

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

## BUG-097: Camp API headers lower array parameters to ABI components

Date/Time: 2026-09-02 13:48 EDT

Summary:
Camp API headers generated for static project references serialize an array
parameter as separate pointer and length parameters. Canonical metadata and API
semantics require a Camp API header to preserve the source-level array parameter;
only native C emission should expand it into ABI components.

Steps to Reproduce:

1. Create a static library project with a public function such as
   `public void consume(const byte[] source, byte[] destination)`.
2. Build the library and inspect its generated `_api.camp` file.
3. Reference the library's `.campbuild` from a second Camp project and call
   `consume(source, destination)` with arrays.
4. Build the consuming project.

Expected:
The Camp API header declares the original two array parameters, and the consumer
binds the two-argument source-level call. Pointer/length expansion occurs only
when the native ABI is emitted.

Actual:
The Camp API header declares four parameters: a pointer and length for each
source array parameter. The consuming source call fails with conversion and
missing-argument diagnostics.

Known Impact:
Static `.campbuild` references cannot naturally consume public Camp APIs that
accept arrays. Callers can use the generated pointer/length shape explicitly,
but that leaks an alpha ABI-lowering defect into source and is not an acceptable
long-term workaround.
