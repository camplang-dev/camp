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

Next bug number: BUG-118.

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

## BUG-117: Windows lowering corrupts nested boolean-array initialization state

Date/Time: 2026-09-03 17:14 America/Toronto

Summary:
On the Windows native target, code that tracks covered ranges in a newly
allocated boolean array can observe entries as already set when they have not
been written. The same source behaves correctly on macOS and Linux. Compilation
completes without a diagnostic, so this is a silent lowering or generated-C
defect rather than a source availability error.

Steps to Reproduce:

1. In a method with a `within` allocator, allocate a byte array and a same-size
   boolean array, and initialize every boolean entry to `false`.
2. Parse two non-overlapping byte ranges, one at offset zero and one at a later
   offset, checking `covered[offset + index]` before assigning it `true`.
3. Compile and run the program for the Windows x64 native target.

Expected:
The second non-overlapping range observes only `false` entries and succeeds.

Actual:
The Windows executable reports an overlap while processing the second range.
The equivalent native executables on macOS and Linux succeed.

Known Impact:
Range-validation and similar boolean-array tracking logic can reject valid
input on Windows despite compiling successfully. Do not rewrite the source to
avoid the normal array pattern; the lowering must preserve the language's
array semantics.
