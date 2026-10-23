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

Next bug number: BUG-354.

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

## BUG-353: An initializer list copying a fixed-size array by value is accepted and emits uncompilable C

Date/Time: 2026-10-23 13:07 EDT

Summary:
Fixed-size arrays cannot be copied by value: an assignment from one fixed array
to another is rejected with a diagnostic that says to assign from an array
literal, a string literal, or `default`. An initializer list that supplies a
fixed array for a fixed-array field is accepted without that diagnostic, and the
emitted C initializes the field with an array expression, which does not
compile.

Steps to Reproduce:

1. Declare `struct Source { fixed char[16] characters; uint length; }` and
   `struct Copy { fixed char[16] characters; uint length; }`.
2. Declare `void literal(Copy* destination, Source* source) { *destination = { source.characters, source.length }; }`.
3. Declare `Copy declared(Source* source) { Copy copy = { source.characters, source.length }; return copy; }`.
4. Declare `void assign(Copy* destination, Source* source) { destination.characters = source.characters; }`.
5. Declare `export int main() { return 0; }` and build an executable.

Expected:
The initializer lists in steps 2 and 3 are rejected by the front end with the
same diagnostic as the assignment in step 4: "Fixed-size arrays cannot be copied
by value; assign from an array literal, string literal, or default." The
diagnostic is reported at the field value inside the list. An initializer list
that supplies an array literal, string literal, or `default` for the field
remains valid.

Actual:
Only the assignment in step 4 is rejected. Steps 2 and 3 pass the front end and
the C compiler fails:

```
*destination = (Copy){ source->characters, source->length };
error: incompatible pointer to integer conversion initializing 'char' with an
expression of type 'char[16]'
```

Known Impact:
A struct literal that copies the fixed text of another struct fails at C
compile time with an error that does not mention the Camp source. The
workaround is to copy the characters element by element, or, when both sides are
the same struct type, to copy the whole struct with `*destination = *source`.

Documentation:
No documentation change is needed; the by-value copy rule is already stated by
the assignment diagnostic.
