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

Next bug number: BUG-349.

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

## BUG-348: By-value escaped interface values are scoped, and non-escaped classes may hold escaped classes by value

Date/Time: 2026-10-23 08:20 EDT

Summary:
A by-value interface type such as `Sink` is implicitly a pointer to a vtable
even though it is written without `*`. When the interface is escaped, that
value is escaped too: escaped data can only contain escaped pointers, so the
vtable it points to is escaped. The compiler treats a by-value escaped interface
parameter as scoped, so it cannot be stored in a global or passed to an escaped
parameter without an annotation.

Separately, only an escaped class may hold an escaped class by value. A
non-escaped class can be a local or other frame storage, so a by-value field of
an escaped class inside it would put an escaped instance in a frame. The
compiler accepts such a field without a diagnostic.

Steps to Reproduce:

1. Declare `escaped interface Sink { void put(int value); }` and a global
   `Sink keptSink;`.
2. Write `void storeValue(Sink sink) { keptSink = sink; }`.
3. Write `void keepValue(escaped Sink sink) { keptSink = sink; }` and
   `void passValue(Sink sink) { keepValue(sink); }`.
4. Declare `escaped class Widget { int value; }`, `class Box { Widget widget; }`,
   and `escaped class Panel { Widget widget; }`.
5. Compile.

Expected:
Steps 2 and 3 compile: a by-value escaped interface value is escaped by type,
like a pointer to an escaped interface. The same code with a non-escaped
interface is still rejected. In step 4 the `widget` field of `Box` is rejected,
because a non-escaped class cannot hold an escaped class by value; `Panel`
compiles.

Actual:
Step 2 reports "Assignment cannot store a non-escaped pointer-bearing value in
escaped storage." Step 3 reports "Argument cannot satisfy parameter lifetime
'escaped'." at the call in `passValue`. Step 4 compiles without diagnostics.

Known Impact:
APIs that take a by-value escaped interface must still write `escaped` on each
parameter that is retained. A non-escaped class holding an escaped class by
value places an instance of an escaped type on the stack whenever the
containing class is a local. Related to BUG-346 (pointers to escaped types are
escaped by type) and BUG-347 (hierarchy and struct field rules).

Documentation:
The rules are not yet in the docs. The lifetime analysis supplement (Escaped
Types) needs the by-value interface rule and the class field rule, and the
language chapters on classes and interfaces should mention them.
