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

Next bug number: BUG-356.

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

## BUG-354: Copying an element of an array parameter into the same array is rejected

Date/Time: 2026-10-23 15:10 EDT

Summary:
Copying an element of an array parameter into another slot of the same array is
rejected when the element type is pointer-bearing. An element read from an array
is something the array retains, so storing it back into the same array cannot
make it outlive its owner. The compiler gives the read value a scoped fact tied
to the array, and the store into storage tied to that array then fails as a
scoped value stored in the array's own storage. This blocks in-place algorithms
such as insertion sort and swapping on slices of structs that contain views.

Steps to Reproduce:

1. Declare `struct Entry { const char[] path; int order; }`.
2. Declare `void shift(Entry[] entries) { entries[1] = entries[0]; }`.
3. Declare `void restore(Entry[] entries) { Entry current = entries[1]; entries[0] = current; }`.
4. Declare `void between(Entry[] to, Entry[] from) { to[0] = from[0]; }`.
5. Declare `struct Plain { int a; int b; }` and `void plainShift(Plain[] items) { items[1] = items[0]; }`.
6. Compile.

Expected:
Steps 2 and 3 compile: the stored element comes from the same array, so it is
already retained by it. Step 5 compiles. Step 4 is rejected, because `from` may
hold elements that do not outlive `to`; the diagnostic names both arrays and
suggests relating them, for example by declaring `from` with `unscoped(to)`,
or copying the element's data.

Actual:
Steps 2, 3 and 4 are rejected with "Assignment cannot store a scoped
pointer-bearing value in storage tied to 'entries'" (and `'to'` for step 4). The
message gives no reason and no suggestion. Step 5 compiles, and so does the same
copy through two pointers to elements.

Known Impact:
In-place sorting and rotation of arrays of structs that hold views cannot be
written without a lifetime cast that claims the element is escaped. A cast makes
the code compile but removes the check for every other use of the value.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md`, Expression Facts, says
indexing a slice keeps the target fact and marks the source as an element view.
State what that means for a store back into the same array: an element read from
an array is retained by that array.

## BUG-355: An out argument written by a method on a local value receiver is tied to the receiver's address

Date/Time: 2026-10-23 15:10 EDT

Summary:
A method result with the default lifetime now means "what the receiver
retains", so a result taken from a local struct that was built from a caller's
data can be returned. An `out` parameter of a method does not follow the same
rule. The value written to an `out` argument by a method on a local value
receiver is treated as tied to the receiver's own memory, so the caller cannot
return it, although the same data returned as a result can be. An explicit
`out unscoped(this)` annotation on the parameter is accepted and has no effect.

Steps to Reproduce:

1. Declare `struct Part { const byte[] data; }` and
   `struct View { const byte[] bytes; Part direct() => { this.bytes }; bool findOut(out Part part) { part = { this.bytes }; return true; } }`.
2. Declare `Part viaResult(const byte[] image) { View view = { image }; return view.direct(); }`.
3. Declare `Part viaOut(const byte[] image) { View view = { image }; Part part = default; view.findOut(out part); return part; }`.
4. Declare `struct Wrapper { View view; }` and
   `Part viaNested(const byte[] image) { Wrapper wrapper = { { image } }; Part part = default; wrapper.view.findOut(out part); return part; }`.
5. Add a method `bool findAnnotated(out unscoped(this) Part part)` with the same body and call it as in step 3.
6. Compile.

Expected:
Steps 2, 3 and 4 compile: the view was initialized from a caller-provided value,
and an `out` value that the receiver retains has that lifetime. A method that
writes the address of the receiver's own memory into an `out` argument declares
that parameter `scoped(this)`, and its callers cannot return the value from a
local receiver. The annotation in step 5 has the meaning it states.

Actual:
Step 2 compiles. Steps 3 and 4 are rejected with "Return expression cannot
return a pointer-bearing value tied to local storage 'view'" (and `'wrapper'`).
The annotated form in step 5 is rejected the same way. The message does not say
that the value came from an `out` argument or what to change.

Known Impact:
Reader types that fill an `out` view of their retained data cannot have that view
returned by the function that builds the reader locally. The workaround is a
lifetime cast on the returned value, which claims more than is true.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md`, Call-Site Relation
Solving: say that `out` parameters follow the same address-versus-content rule as
results.
