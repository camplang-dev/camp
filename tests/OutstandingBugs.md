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

Next bug number: BUG-120.

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

## BUG-116: Conditional expression can leave a slice result uninitialized

Date/Time: 2026-09-03 America/Toronto

Summary:
A conditional expression whose branches produce borrowed array slices can lower
to generated C that declares the result slice without assigning either branch.
The resulting program may read an invalid pointer and crash despite compiling
without a diagnostic.

Steps to Reproduce:

1. Declare a local borrowed array-slice variable initialized by a conditional
   expression whose two branches are distinct slices of a source array.
2. Pass that local to another method.
3. Compile and run the program.

Expected:
The generated program assigns the selected branch slice, preserving its pointer
and length, and the called method receives that selected slice.

Actual:
The generated C declares the slice local but leaves its pointer and length
uninitialized before passing it to the called method. The executable can crash.

Known Impact:
Code using a conditional expression to select a slice is unsafe until fixed.
An equivalent branch that calls the consumer separately for each selected slice
avoids the faulty lowering.

## BUG-119: Incremental build succeeds without restoring missing native output

Date/Time: 2026-09-03 18:28 America/Toronto

Summary:
The incremental native build cache can report a project current after its
declared native output directory has been moved aside. The build command exits
successfully but does not recreate the requested executable, library, or test
result artifact. This makes a clean-output rebuild impossible without changing
unrelated source timestamps.

Steps to Reproduce:

1. Build a response-file project that emits a native artifact.
2. Move only its ignored output directory to a temporary sibling location.
3. Run the same `campc build` or `campc test` command without changing source
   files or the response file.

Expected:
The missing declared artifact marks the project dirty and the compiler
recreates the output directory and requested artifact.

Actual:
The compiler returns success while leaving the output path absent. An
`--out-dir` attempt cannot redirect the response-file output because the
compiler diagnoses the duplicate option.

Known Impact:
Fresh-output validation, recovery after generated-artifact cleanup, and
reproducible target testing cannot rely on the normal build command. Do not
modify source only to force a rebuild; artifact presence must participate in
freshness evaluation.
