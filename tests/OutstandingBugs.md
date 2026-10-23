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

Next bug number: BUG-347.

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

## BUG-346: Pointers to escaped classes and escaped interfaces are not escaped by type

Date/Time: 2026-10-23 07:40 EDT

Summary:
An `escaped class` or `escaped interface` puts the lifetime on the type: an
instance is never bound to the call stack, so every pointer to it is escaped
without an annotation, as if written `escaped T*`. The compiler applies the
escaped rule only to an escaped class's fields and receivers. A plain `T*`
parameter or local of an escaped type is still treated as scoped, so it cannot
be stored in a global or passed to an escaped parameter. In the other direction,
stack storage of an escaped type is accepted, and scoped struct storage converts
to an escaped interface pointer without a diagnostic.

Steps to Reproduce:

1. Declare `escaped class Widget { int value; }`, a global `Widget* kept;`, and
   `void keep(escaped Widget* widget) { kept = widget; }`.
2. Write `void store(Widget* widget) { kept = widget; }` and
   `void pass(Widget* widget) { keep(widget); }`.
3. Write `void local() { Widget onStack = default; }` and
   `void scratch() { Widget* w = stackalloc Widget(); }`.
4. Declare `escaped interface Sink { void put(int value); }`, a struct
   `StackSink: Sink` that implements `put`, a global `Sink* keptSink;`, and
   `void storeSink(Sink* sink) { keptSink = sink; }`.
5. Write `void adapt() { StackSink local = default; Sink* view = local; }`.
6. Compile.

Expected:
Steps 2 and 4 compile: `Widget*` and `Sink*` are escaped by type. Step 3 is
rejected at each declaration, because storage of an escaped type cannot be
declared in an activation frame. Step 5 is rejected at the conversion, because a
pointer to an escaped interface needs an escaped source; converting a global
struct, or one allocated with `new`, is accepted. Writing `escaped` on a pointer
to an escaped type is accepted and redundant; writing `scoped` or
`unscoped(anchor)` on it is rejected.

Actual:
Step 2 reports "Assignment cannot store a non-escaped pointer-bearing value in
escaped storage." and "Argument cannot satisfy parameter lifetime 'escaped'.";
step 4 reports the same assignment error. Steps 3 and 5 compile without
diagnostics.

Known Impact:
Every API that keeps a pointer to an escaped type must repeat `escaped` on each
parameter, and code that forgets it gets errors that the type was meant to
prevent. The standard allocator interface is meant to be an escaped interface,
so that every `Allocator*`, including the implicit type of `within allocator`,
is escaped and a heap construction that retains its allocator (for example a
list built inside a `within` function) needs no annotation. Until this is fixed
that interface cannot be declared escaped, and each such `within` parameter has
to be written `within escaped Allocator* allocator`. Stack-allocated allocators
currently convert to `Allocator*` and hand out memory that dies with the frame
while their results are treated as escaped. The fix should also declare the
standard allocator interface `escaped`, and any compiler-generated allocator,
such as a test runner's tracking allocator, must live in static or heap storage.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md` (Declaration Defaults,
Fields, Escaped Types, and the interface conversion paragraph),
`docs/semantics/09-interface-vtables-and-dynamic-dispatch.md`, and language
chapters 05, 07, 12, and 16 now describe this rule. Not yet decided: by-value
fields of an escaped type inside a struct, by-value parameters of an escaped
type, and a non-escaped class derived from an escaped class.
