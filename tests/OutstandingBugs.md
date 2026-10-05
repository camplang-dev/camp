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

Next bug number: BUG-229.

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

## BUG-230: Writes through a const array view are not diagnosed

Date/Time: 2026-10-05 10:53 America/Toronto

Summary:
Assigning to, or incrementing, an element of a `const` array view is accepted by
analysis. Writes through a `const` pointer and to a `const` struct are
diagnosed.

Steps to Reproduce:

1. Compile `void touch(const int[] items) { items[0] = 7; items[1]++; ++items[1]; }`
   and call it from an exported entry point.
2. Build it.

Expected:
Each write is rejected because the elements of a `const` view are read-only.

Actual:
No diagnostic. The native build fails with "read-only variable is not
assignable".

Known Impact:
Same as BUG-229: the error is reported by the C compiler without a Camp source
location, and a different backend could accept the write.

## BUG-231: Updating a getter-only property is not diagnosed

Date/Time: 2026-10-05 10:53 America/Toronto

Summary:
`value.Name = next` and `value.Name += next` are rejected when the type has a
`getName()` but no `setName()` ("Property 'Name' is not writable"), but
`value.Name++` is accepted.

Steps to Reproduce:

1. Declare `class Holder { int stored; int getTotal() => this.stored; }` and
   compile an exported entry point containing `Holder* h = &holder; h.Total++;`.
2. Build it.

Expected:
The update is rejected because the property has no setter.

Actual:
No diagnostic. The generated C is `Holder_getTotal(...)++`, which fails with
"expression is not assignable".

Known Impact:
Same as BUG-229.

## BUG-232: Assigning to an inline constant is not diagnosed

Date/Time: 2026-10-05 10:53 America/Toronto

Summary:
An `inline` constant can be the target of an assignment without a diagnostic.

Steps to Reproduce:

1. Compile `inline int LIMIT = 10;` with an exported entry point containing
   `LIMIT = 11;`.
2. Build it.

Expected:
The assignment is rejected because an inline constant is a compile-time value.

Actual:
No diagnostic. The native build fails with "expression is not assignable".

Known Impact:
Same as BUG-229.

## BUG-233: Integer literals outside the range of their target are accepted

Date/Time: 2026-10-05 11:21 America/Toronto

Summary:
An integer literal that does not fit the primitive type it initializes or is
assigned to is accepted and silently truncated by the C compiler. Enum values and
newtype literals are range-checked; ordinary primitive targets are not.

Steps to Reproduce:

1. Compile an exported entry point containing `byte a = 256;`, `short b = 32768;`,
   `int c = 2147483648;`, `uint d = 4294967296;` and `ulong e = 18446744073709551616;`.
2. Build it.

Expected:
Each literal is rejected because its value is outside the target type's range.

Actual:
No diagnostic for the first four; clang warns that the value changes (256 becomes
0). The last fails in the C compile ("integer literal is too large to be
represented in any integer type") because the emitted C literal is invalid.

Known Impact:
Wrong values are stored silently. A literal wider than 64 bits surfaces as a C
compiler error with no Camp source location.

## BUG-234: auto with no inferable type has no Camp diagnostic

Date/Time: 2026-10-05 11:21 America/Toronto

Summary:
`auto` on a local whose initializer gives no type (no initializer, `null`, or
`default`) is not diagnosed by analysis. The error surfaces later in C emission
or in clang. The expected diagnostic is a generic one: the type cannot be
inferred, so a type must be written.

Steps to Reproduce:

1. Compile an exported entry point containing `auto missing;`, or
   `auto nothing = null;`, or `auto fromDefault = default;` (one per build).
2. Build it.

Expected:
A source-located error that the type of the expression cannot be inferred and a
type must be specified.

Actual:
`auto missing;` and `auto fromDefault = default;` stop with "C emission aborted
because DeclarationTarget ... has unresolved type '#ERROR'". `auto nothing = null;`
emits `_NULL nothing = NULL;` and fails in clang with "use of undeclared
identifier '_NULL'". (`auto x = { 1, 2 };` is diagnosed: "Initializer expression
requires a target type.")

Known Impact:
The error has no Camp source location, and different forms fail at different
stages.

## BUG-235: Negative integer literals are rejected for sbyte and short targets

Date/Time: 2026-10-05 11:48 America/Toronto

Summary:
A negated integer literal whose value is in range is rejected as an `int` that
does not convert to a narrower signed type. The same value written without the
minus sign (`sbyte b = 127;`) is accepted, so the literal is target-typed but its
negation is treated as a general `int` expression.

Steps to Reproduce:

1. Compile an exported entry point containing `sbyte a = -128;`,
   `short d = -32768;` and `sbyte l = -5;`.
2. Build it.

Expected:
Each declaration is accepted, because -128, -32768 and -5 fit the target.

Actual:
"Declaration initializer cannot convert 'int' to 'sbyte'." (and to 'short').
`int f = -2147483648;` and `long i = -9223372036854775808;` are accepted.

Known Impact:
The smallest value of `sbyte` and `short`, and any negative constant, cannot be
written without a cast. Once BUG-233 is fixed the same minus handling must
range-check the negated literal. The beta compiler rejects these the same way.

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
