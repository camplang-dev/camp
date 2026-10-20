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

Next bug number: BUG-317.

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

## BUG-315: Transformed prep call propagating `thrown` passes arguments in the wrong order

Date/Time: 2026-10-19 22:27 EDT

Summary:
When a caller with a compatible `thrown` slot calls a prep-bearing function that
has its own trailing `thrown` slot, omitting the prep slot should transform the
call and propagate the error. The front end accepts the program, but the
generated sizing and write calls pass the length, error and buffer arguments in
the wrong order, so the native build fails.

Steps to Reproduce:

1. Declare `nuint render(int value, prep char[] buffer = default, thrown int
   error) { if (value < 0) throw 7; return (nuint)value; }`.
2. Declare `int wrapper(thrown int error) { char[] ok = render(3); return
   (int)ok.length; }`.
3. Call `wrapper(catch caught)` from `main` and build with a native backend.

Expected:
The program compiles and returns 3. Both protocol calls forward the caller's
`thrown` slot, and no write follows a sizing failure.

Actual:
clang rejects the generated C: `render(3, NULL, error, 0)` passes the error
pointer where the buffer length belongs, and the prepared result is assigned
from a scalar return type. The front end reports no diagnostics.

Known Impact:
Prepared results cannot be requested from a throwing prep function through
propagation. Writing the prep argument explicitly is the workaround.

## BUG-316: A scoped prepared result can be stored into a field without a diagnostic

Date/Time: 2026-10-19 22:30 EDT

Summary:
A transformed prep call produces scoped storage with the lifetime of an
equivalent `stackalloc` array, and the existing escape rules apply to it.
Returning the result is rejected, but storing it into a field reached through a
pointer parameter is accepted. Storing a `stackalloc` array into the same field
is rejected with "Assignment cannot store a scoped pointer-bearing value in
storage tied to 'this'."

Steps to Reproduce:

1. Declare `struct Holder { char[] kept; }` and `nuint render(int value, prep
   char[] buffer = default) { return (nuint)value; }`.
2. Declare `void store(Holder* holder) { holder.kept = render(3); }`.
3. Compile the program.

Expected:
The assignment is rejected with the same diagnostic as a `stackalloc` array,
because the prepared storage dies when `store` returns.

Actual:
The program compiles without diagnostics and leaves a dangling array in the
field.

Known Impact:
Dangling prepared storage can be retained silently through fields. Using `(new)`
or an explicit buffer is the safe pattern.
