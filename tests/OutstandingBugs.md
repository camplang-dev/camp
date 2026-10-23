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

## BUG-358: A parameter argument for unscoped(destination) is rejected when the destination is a local

Date/Time: 2026-10-23 17:52 EDT

Summary:
A parameter (or a field of a by-value or pointer parameter) passed as an argument
for a parameter declared `unscoped(destination)` is rejected when the destination
argument is a local of the calling function. The same call is accepted when the
value is a local, and the equivalent receiver form (`unscoped(destination) this`
with a parameter receiver) is also accepted. A parameter's content outlives every
local of the calling function, so the argument form should accept it, as the
receiver form does. Related: BUG-351 (receiver lifetime enforcement).

Steps to Reproduce:

1. Declare `struct Atom { const char[] name; }` and `struct Opts { Atom[] items; uint n; }`.
2. Declare `bool collectDirect(unscoped(destination) Atom[] source, Atom[] destination) { destination[0] = source[0]; return true; }`.
3. Declare callers, each with a local `fixed Atom[4] d = default;` passed as `d[..]`:
   - `void callerSlice(Atom[] items) { ...; collectDirect(items, d[..]); }`
   - `void callerField(Opts options) { ...; collectDirect(options.items, d[..]); }`
   - `void callerPtr(Opts* options) { ...; collectDirect(options.items, d[..]); }`
4. For comparison declare `struct Box { fixed Atom[4] atoms; uint n; uint copyViews(unscoped(destination) this, Atom[] destination) { destination[0] = this.atoms[0]; return this.n; } }` and
   `void callerReceiverParam(Box* box) { fixed Atom[4] d = default; box.copyViews(d[..]); }`,
   plus a caller whose source is a local `fixed Atom[4] s` passed to `collectDirect`.
5. Compile.

Expected:
All callers compile. The local destination is retained only for the calling
function's activation, and the parameter content outlives that activation.
Two unrelated parameters (`collectDirect(items, dest)` with both parameters)
remain rejected, because no relation is known.

Actual:
The three parameter-argument callers are rejected with "Argument cannot satisfy
parameter lifetime 'unscoped(destination)'". The receiver form and the
local-source form compile. `CheckLifetimeReceiverRetention` has the rule
"anything that is not a local of this function outlives the function's locals";
`CheckLifetimeParameterArgument` has no equivalent. The message names neither the
argument nor the destination and gives no suggestion.

Known Impact:
Beta `compileProject` passes `options.definitions` (a field of a by-value
parameter) to a helper that copies elements into a local `fixed` array, and cannot
compile. There is no annotation that fixes the caller.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md`: state that arguments
that are not locals of the calling function satisfy `unscoped(anchor)` with a
local anchor argument. The diagnostic should read like the receiver one: name the
argument and the destination and say what to change.

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
