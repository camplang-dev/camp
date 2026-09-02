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

Next bug number: BUG-114.

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

## BUG-112: Inactive imported-result indexing overflows through a rooted build file

Date/Time: 2026-09-02 19:16 EDT

Summary:
The BUG-110 correction does not cover the reusable build-file form that sets a
parent source-file root and includes both production and conditionally inactive
test globs. Building that project through a static reference still overflows
while lowering indexing on an imported method result in the inactive test file.

Steps to Reproduce:

1. Create adjacent static-library modules using one flat namespace.
2. In the consumer build file, set [--sourcefile-root ..], reference the first
   module, and include both [src/*.camp] and [tests/*.camp].
3. Begin the test file with [requires(TEST_MODULE);] and index an array returned
   by an imported instance method.
4. Build the consumer as a static artifact without [TEST_MODULE].

Expected:
The test file is inactive and the ordinary static build succeeds.

Actual:
Lowering repeatedly enters reference-namespace lookup, generic property lookup,
and expanded-component indexing until the compiler terminates with a native
stack overflow.

Known Impact:
Modules cannot keep production and guarded test sources in one reusable rooted
build file when tests exercise this common imported-array pattern.

## BUG-113: Same-namespace layered API still loses an imported array projection

Date/Time: 2026-09-02 19:16 EDT

Summary:
The BUG-111 correction does not cover a library, downstream library, and
consumer that all use the same flat namespace. The downstream generated API
still names the upstream struct's source identity for an array parameter while
the consumer correctly sees the imported ABI projection.

Steps to Reproduce:

1. Build a static library that exports a struct in a namespace.
2. Build a second static library in the same namespace that references the
   first and exports a function accepting [const ExportedStruct[]].
3. Rebuild both libraries with the current compiler.
4. Build a consumer in the same namespace that references both libraries,
   constructs an [ExportedStruct[]], and passes it to the second library.

Expected:
The downstream API preserves the upstream projected type identity and the
consumer compiles.

Actual:
The consumer reports that its ABI-projected struct array cannot convert to the
unprojected struct array named by the downstream API.

Known Impact:
Products whose modules intentionally share a flat namespace cannot layer public
array-view APIs over common record types.
