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

Next bug number: BUG-247.

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

## BUG-243: An argument spelled `context` or `otherContext` drops the length of a slice argument

Date/Time: 2026-10-05 15:41 America/Toronto

Summary:
In a call that passes a slice such as `atoms[..]`, an argument identifier spelled
`context` or `otherContext` makes the emitted C call omit the slice length. Other
spellings (`first`, `ctx`, `declaration`) emit the call correctly.

Steps to Reproduce:

1. Declare `public struct T { bool pick(uint id, uint[] atoms, uint* count) { *count = (uint)atoms.length; return id != 0; } }`.
2. Add a method `public bool implies(uint context, uint declaration)` with
   `fixed uint[16] atoms = default; uint n = 0;` that calls
   `this.pick(context, atoms[..], &n)` and `this.pick(declaration, atoms[..], &n)`.
3. Add `export int main() { T t = default; return t.implies(1, 2) ? 1 : 0; }` and build.

Expected:
Both calls pass `atoms` with its length 16.

Actual:
The call using `context` is emitted as `T_pick(this, context, atoms[0, 16], &n)`, a
comma expression with one argument fewer than the function takes, and Clang fails
with "too few arguments to function call, expected 5, have 4". The call using
`declaration` is emitted correctly. Renaming the argument fixes it.

Known Impact:
A valid call fails in native compilation with no Camp source diagnostic.

## BUG-244: Compound property assignment discards the operator

Date/Time: 2026-10-05 16:30 America/Toronto

Summary:
Compound assignment to a writable property lowers to an ordinary setter call
with the right-hand operand. It does not read the current property value or
apply the compound operator, so valid source silently produces the wrong value.

Steps to Reproduce:

1. Declare `class Holder { int value; int getMask() => this.value; void setMask(int value) { this.value = value; } }`.
2. In an exported entry point, create `Holder holder = default;`, then execute
   `holder.Mask = 7; holder.Mask ^= 1;`.
3. Return `holder.Mask == 6 ? 0 : 1;` and build/run with `--nostdlib`.

Expected:
The compound assignment reads 7, computes `7 ^ 1`, and writes 6. The program
exits with code 0.

Actual:
The emitted C calls `Holder_setMask(&holder, 1)` without reading the getter or
applying XOR. The program exits with code 1. Integer `&=` and `|=` property
assignments also lower to plain setter calls.

Known Impact:
Valid compound property writes silently assign the right-hand operand instead
of the operation's result. Use an explicit read, binary operation, and ordinary
property assignment as a workaround, preserving receiver/index evaluation when
those expressions have side effects.

## BUG-245: Returning a struct payload from an optional-returning function yields an unspecified optional

Date/Time: 2026-10-05 17:10 America/Toronto

Summary:
In a function whose result is `P?` for a struct `P`, `return b;` with a `P` local
returns an optional whose specified bit is false. The same return works for `int?`,
and copying the struct into a `P?` local first works.

Steps to Reproduce:

1. Declare `struct P { int x; }` and `P? make() { P b = { 1 }; return b; }`.
2. In `main`, call `P? c = make();` and log `c.specified ? 1 : 0`.

Expected:
The returned optional is present, so the log prints 1.

Actual:
The log prints 0. `P? make() { P b = { 1 }; P? r = b; return r; }` prints 1, and so does
`int? make() { int b = 1; return b; }`.

Known Impact:
Struct-payload optionals returned from a plain payload silently become empty. Assign
the payload to an optional local and return that local as a workaround.

## BUG-246: An omitted optional parameter with a payload default drops the specified component

Date/Time: 2026-10-05 18:40 America/Toronto

Summary:
A call that omits a parameter declared `int? o = 7` emits the C call with only the
payload, so the specified component is missing and the native build fails. The
default `= default` expands both components correctly.

Steps to Reproduce:

1. Declare `int opt(int? o = 7) { return o.specified ? o.value : -1; }`.
2. Call `opt()` from `main` and log the result.

Expected:
The omitted argument expands to a present optional carrying 7, so the call logs 7.

Actual:
No Camp diagnostic. The C compiler reports "too few arguments to function call,
expected 2, have 1" for `opt(7)`.

Known Impact:
Optional parameters cannot default to a present payload. Pass the optional
explicitly or default to `default` and test `.specified` in the callee.
