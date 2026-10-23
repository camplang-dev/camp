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

Next bug number: BUG-360.

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

## BUG-359: unscoped(destination) does not permit interior views of the retained input

Date/Time: 2026-10-23 17:52 EDT

Summary:
`unscoped(destination)` on a retained input (a parameter or the receiver) does not
permit storing an interior view of that input's inline storage into `destination`.
The example in `docs/language/07-lifetimes-allocation-and-within.md`
(`uint copyViews(unscoped(destination) this, NameView[] destination)` storing
`this.entries[i].name()`) is rejected. A plain element copy under the same
annotation is accepted (`destination[0] = source[0];`).

Steps to Reproduce:

1. Declare `struct Name { fixed char[8] chars; uint length; scoped(this) const char[] view() => this.chars[..this.length]; }`
   and `struct Atom { const char[] name; }`.
2. Declare `struct Settings { fixed Name[4] names; uint count; uint copyViews(unscoped(destination) this, Atom[] destination) { for (uint i = 0; i < this.count; i++) destination[i] = { this.names[i].chars[..this.names[i].length] }; return this.count; } }`.
3. Declare `bool collect(unscoped(destination) Name[] facts, Atom[] destination) { destination[0] = { facts[0].chars[..facts[0].length] }; return true; }`.
4. Declare `bool collectViaMethod(unscoped(destination) Name[] facts, Atom[] destination) { destination[0] = { facts[0].view() }; return true; }`.
5. Compile.

Expected:
All three compile. The annotation says the input is retained by `destination` and
outlives it, so views into the input's own storage are valid in `destination`.
This matches the documented `copyViews` example. A caller that violates the
relation is rejected at the call (see the related bug on parameter arguments).

Actual:
Steps 2, 3 and 4 are each rejected with "Assignment cannot store a scoped
pointer-bearing value in storage tied to 'destination'". The message names no
cause and offers no suggestion, although the correct annotation is already on the
parameter.

Known Impact:
Beta `compilationCollectImportedAtoms` copies views of `facts[index].name`
characters into `destination` and cannot compile; with the annotation it is
rejected anyway. Any helper that fills a destination with views of a caller's
array elements is affected.

Documentation:
`docs/language/07-lifetimes-allocation-and-within.md`: the `copyViews` example
already describes the intended behavior. Add the array-parameter form if the
fixer wants it. Related: BUG-349, BUG-351.
