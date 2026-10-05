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

Next bug number: BUG-242.

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

## BUG-236: Prefix increment and decrement do not check the operand type

Date/Time: 2026-10-05 11:57 America/Toronto

Summary:
`++target` and `--target` accept a `bool`, a pointer or a struct, while the
postfix forms are rejected ("Update operator requires a numeric operand").

Steps to Reproduce:

1. Compile an exported entry point with `bool flag = true; ++flag; --flag;`,
   `int* pointer = &value; ++pointer;` and `Box box = default; ++box;`.
2. Build it.

Expected:
Each prefix update is rejected the way `flag++`, `pointer++` and `box++` are.

Actual:
No diagnostic for the prefix forms. (Together with BUG-229 the prefix forms skip
both the const check and the operand type check.)

Known Impact:
A bool, pointer or struct can be incremented with the prefix operator, which the
language does not allow.

## BUG-237: Compound bitwise assignment accepts bool operands

Date/Time: 2026-10-05 11:57 America/Toronto

Summary:
`flag &= other`, `flag |= other` and `flag ^= other` on `bool` operands are
accepted, while `flag & other`, `flag | other` and `flag ^ other` are rejected
("Bitwise operators require integral operands, not 'bool' and 'bool'"). Under
the rule that a bool is not a number, the compound forms should be rejected too.

Steps to Reproduce:

1. Compile an exported entry point with `bool flag = true; bool other = false;
   flag &= other; flag |= other; flag ^= other;`.
2. Build it.

Expected:
Each compound assignment is rejected like the plain bitwise operator.

Actual:
No diagnostic. (`flag <<= 1` is rejected as an int-to-bool assignment.)

Known Impact:
The two forms of the same operation disagree. The beta compiler currently
accepts the compound forms as well, so a golden cannot cover them until the
bootstrap decides.

## BUG-238: Newtypes accept arithmetic, ordering, bitwise, shift, unary and update operators

Date/Time: 2026-10-05 12:37 America/Toronto

Summary:
A newtype is not a numeric type even when its carrier is numeric. The only
operators defined on a newtype are `==` and `!=` between two values of the same
newtype. The compiler accepts every other operator on a newtype whose carrier is
an integer.

Steps to Reproduce:

1. Declare `newtype Meters: int;` and an exported entry point with `Meters a =
   (Meters)1; Meters b = (Meters)2;`.
2. Add `a < b`, `a + b`, `a - b`, `a * b`, `a / b`, `a % b`, `a & b`, `a | b`,
   `a << 1`, `-a`, `~a`, `+a`, `a++`, `++a`, `a += b` and `a |= b`.
3. Build it.

Expected:
Each of these is rejected; only `a == b` and `a != b` are accepted.

Actual:
No diagnostic for any of them. (Using a newtype as a condition, and converting it
to a bool, are rejected.)

Known Impact:
A newtype can be used as a number, which defeats its purpose. Beta rejects all of
these with SEMANTIC_INVALID_NEWTYPE_OPERATION.

## BUG-239: Character literal escapes do not follow the C# rules

Date/Time: 2026-10-05 12:37 America/Toronto

Summary:
Camp uses the C# escape rules: \' \" \\ \0 \a \b \e \f \n \r \t \v, \x
with one to four hex digits, \u with exactly four and \U with exactly eight.
The compiler accepts some invalid escapes and rejects a valid one.

Steps to Reproduce:

1. Compile an exported entry point with `char a = '\q';`, `char b = '\u12';`,
   `char c = '\u123';`, `wchar d = '\U00041';` and `char f = '\1';`.
2. Compile `wchar g = '\x0041';`.

Expected:
The first five are rejected ("Character literal must contain exactly one Unicode
scalar value"). The sixth is accepted as U+0041.

Actual:
The first five are accepted (`'\q'` is 'q', `'\u12'` is U+0012, `'\1'` is 1). The
sixth is rejected: a \x escape is limited to two hex digits.

Known Impact:
Malformed escapes are silently reinterpreted. Beta follows the C# rules.

## BUG-240: String literal escapes are copied to C unchanged

Date/Time: 2026-10-05 12:37 America/Toronto

Summary:
The escapes in a string literal are not validated or translated by Camp; the text
is emitted into the C source as written, so C's escape rules apply instead of the
C# rules Camp uses. A malformed escape becomes a C compiler warning or error, and
a valid one can change meaning.

Steps to Reproduce:

1. Compile `const char[] a = "\x0041";`, `const char[] b = "\u12";`,
   `const char[] c = "\q";` and `const char[] d = "\x41B";` in an exported entry
   point.
2. Build it.

Expected:
`"\x0041"` is "A"; `"\u12"`, `"\q"` are rejected; `"\x41B"` is U+041B.

Actual:
The native build fails ("hex escape sequence out of range", "incomplete universal
character name") or only warns ("unknown escape sequence '\q'"). In C a \x escape
consumes every following hex digit.

Known Impact:
Escape errors have no Camp source location, and string and character literals
disagree on what an escape means.

## BUG-241: Direct indexing of an array returned by a property lowers as an accessor argument

Date/Time: 2026-10-05 15:06 America/Toronto

Summary:
Directly indexing an array view returned by a parameterless property getter is
accepted by body analysis, but lowering passes the index to the getter instead
of indexing its returned array. Reads and updates fail during native compilation.

Steps to Reproduce:

1. Declare `class Holder { int[] items; int[] getItems() => this.items; }`.
2. In an exported entry point, create `fixed int[1] items = [3];`, a default
   `Holder` and pointer `h`, and assign `h.items = items[..];`.
3. Build `return h.Items[0] == 3 ? 0 : 1;`, or an update such as `h.Items[0]++;`.

Expected:
The getter is called without source arguments, then the returned view is indexed.
The read returns the stored element, and an update changes the backing storage.

Actual:
The read emits `Holder_getItems(receiver, 0, &length)`, which Clang rejects as
having too many arguments. Updates also apply an update operator to the getter
call rather than an array element and fail with "expression is not assignable".

Known Impact:
Valid direct array-property indexing fails without a Camp source diagnostic.
Copying the getter result into a local view and indexing that view works.

## BUG-242: A type alias hides the unavailability of a gated target

Date/Time: 2026-10-05 15:40 America/Toronto

Summary:
Using an ordinary alias whose target type is declared under a `requires` condition
skips the availability check that the target type itself would get. The unproven
requirement is accepted, and the build then fails during native compilation.

Steps to Reproduce:

1. Declare `requires (FA) struct H { int x; }` and the unconditional `alias HA = H;`.
2. Add `int f(HA a) { return 0; }` and `export int main() { return 0; }`.
3. Build with `-d FA=false`.

Expected:
The use of `HA` in an unconditional declaration is rejected like a use of `H`:
"Type 'H' requires configuration 'FA', but that requirement is not proven here."

Actual:
No Camp diagnostic. The C compiler reports "unknown type name 'H'" for the
generated declaration of `f`.

Known Impact:
Availability errors surface as native compiler failures when the type is named
through an alias. Naming `H` directly is rejected correctly.

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
