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

Next bug number: BUG-438.

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

## BUG-411: Access to fields declared by a non-shadow base is not rejected through a shadow class

Date/Time: 2026-10-28 12:47 EDT

Summary:
Instance fields declared by a non-shadow base are not accessible through a shadow class. The compiler currently accepts reads and writes of those fields through a shadow class pointer or through `this` inside a shadow class method, instead of reporting an accessibility diagnostic. Sharing the base object's pointer representation does not make its fields accessible to the shadow class.

Steps to Reproduce:

1. Declare `class Host` with an `escaped void* shadowData` field, an `int hostValue` field, a constructor, and `@getshadow` and `@setshadow` methods that read and write `shadowData`.
2. Declare `shadow class S: Host` with an `int v` field, a constructor and a method containing `delete shadow;`.
3. In `main`, write `S* s = new S(); __intrinsic_log_i32(s.hostValue);`. Also try `this.hostValue` inside a method of `S`, and a write such as `s.hostValue = 1;`.
4. Compile the program.

Expected:
Semantic analysis rejects each read or write at the field access with a diagnostic explaining that `hostValue` is declared in non-shadow base class `Host` and is not accessible to shadow classes.

Actual:
Semantic analysis reports no accessibility error. Compilation proceeds to native code generation, where the build fails with `error: incomplete definition of type 'S'` at the generated field access `s->hostValue`. The missing source diagnostic is the defect; generating compilable C for this access would still be incorrect.

Known Impact:
Invalid access to a non-shadow base's fields is accepted by semantic analysis and reported later as a native compiler error. Use a method of the base class to expose the required operation instead of accessing its field through the shadow class.

## BUG-433: A built-in configuration flag name hides a same-named target enum member

Date/Time: 2026-10-29 10:26 EDT

Summary:
Configuration flags can only be queried with `configured(...)` and are not names in ordinary code. When an expression or `case` label names a member of the target enum type and the name is also a built-in configuration flag, such as `X64`, lookup finds the flag and reports the flag rule instead of using the enum member. Since BUG-426, ordinary name lookup beats a target enum member, and the flag is found at that step. The qualified form `Arch.X64` works.

Steps to Reproduce:

1. Declare `enum Arch { UNKNOWN, X86, X64, ARM64 }`.
2. In a function that returns `Arch`, write `return X64;`. Also try `Arch b = X64;` and `case X64:` in a `switch` over an `Arch` value.
3. Compile.

Expected:
Each use names the `Arch` member, because a configuration flag is not visible outside `requires` declarations and `configured(...)`. The programs compile.

Actual:
Each use is rejected with `Configuration flag 'X64' can only be queried with configured(...).`. Other built-in flag names that are not defined for the current target, such as `X86` and `ARM64`, resolve to the enum member in the same program.

Known Impact:
An enum with a member named like a built-in flag (`X64`, and likely `WINDOWS`, `LINUX` and the like) cannot use the member by its bare name in target-typed positions on a target that defines the flag. Qualify the member with the enum name.

## BUG-434: An expression made only of unsuffixed literals does not take its target or operand type

Date/Time: 2026-10-29 11:02 EDT; widened with the owner's permission 2026-10-29 11:20 EDT, before any fix started

Summary:
A single unsuffixed literal takes the type of its target, or of the other operand of an operator, when its value fits that type. A literal-shaped expression should do the same. A literal-shaped expression is one of:

- an unsuffixed integer or floating literal;
- a constant expression built only from such literals, such as `256 * 1024 * 1024`, `5 + 45` or `0.1 + 0.2`;
- a conditional whose two arms are both literal-shaped (the condition can be anything), such as `f ? 1 : 2` or `f ? 1 : (5 + 45)`;
- one of these in parentheses.

The constant parts are computed in their natural types, as C computes them. A literal-shaped integer expression then converts to any integer target or operand type that holds every value it can produce: the computed value of a constant, or each arm of a conditional. A literal-shaped floating expression converts only to `float` and `double`. The bootstrap already does this for single literals, and for conditionals of single literals with a declared target. It does not do it for constant expressions, for a literal-shaped operand on the left of arithmetic, or for either side of a comparison.

Steps to Reproduce:

1. Declare `inline uint BUDGET = 256 * 1024 * 1024;` at global scope.
2. In `main`, declare `uint a = 256 * 1024 * 1024;`, `nuint b = 64 * 1024;`, `ulong c = 1024 * 1024;` and `uint d = (1 + 2);`.
3. In `main`, with `uint v = 7;`, write `v = 2 * 8;` and `v += 4 * 4;`.
4. Declare `void arg(uint value)` and `uint ret() { return 4 * 1024; }`, and call `arg(8 * 8);`.
5. Declare `struct Opts { int mode; nuint budget; }` and pass `{ 1, 256 * 1024 * 1024 }` as an `Opts` argument.
6. In `main`, with `bool f = true;`, declare `uint x = f ? 1 : (5 + 45);`.
7. In `main`, with `uint k = 3;`, declare `bool p = k < (f ? 1 : 2);`, `bool q = k == (f ? 1 : 2);`, `bool r = k < 2 * 3;` and `uint s = (f ? 2 : 3) * k;`.
8. In `main`, declare `float h = 0.5 * 3;` and `float w = 0.1 + 0.2;`.
9. Compile.

Expected:
Every use above compiles. Constant parts are computed in their natural types and the result is converted, so `256 * 1024 * 1024` is computed as `int` and then converted to `uint`, and `0.1 + 0.2` is computed as `double` and then converted to `float`. The comparisons in step 7 compare as `uint`, and `(f ? 2 : 3) * k` is `uint`. A computed value or conditional arm that does not fit is rejected at that value, for example `byte b = f ? 1 : 300;` at `300`, and `uint n = f ? -1 : 2;` at `-1`.

These are not literal-shaped and stay as they are:

- A suffixed literal keeps its fixed type.
- A named inline constant lowers to `#define NAME ((T)value)`, so it acts as `(T)value` with the fixed type `T`. With `inline int PAGE = 4096;`, `nuint n = PAGE * 4;` stays an error.
- Arithmetic that is not constant stays in its natural type even when every operand is literal-shaped. So `uint z = (f ? 1 : 5) + (g ? 10 : 20);` is an `int` expression and an error, and so is `uint m = 1 << (uint)n;`.
- With a `float` `a`, `a * 0.1` is `double`, as in C.

Actual:
Steps 1 to 5 are rejected with `cannot convert 'int' to 'uint'` (or `'nuint'` or `'ulong'`), as a `Global initializer`, `Declaration initializer`, `Assignment`, `Argument`, `Return expression` or `Initializer field`. Step 6 reports `Conditional expression cannot convert 'int' to 'uint'.`. In step 7, each comparison reports `Cannot compare 'uint' and 'int'.`, and `(f ? 2 : 3) * k` reports `Declaration initializer cannot convert 'int' to 'uint'.`. Step 8 reports `Declaration initializer cannot convert 'double' to 'float'.`. Already correct: `uint a = f ? 1 : 0;`, `byte b = f ? 1 : 33;`, `float c = f ? 1.5 : 2.5;`, `k + 2 * 3` and `k * (f ? 2 : 3)`.

Known Impact:
Sizes and limits written as products of literals need a redundant cast when the target is unsigned or 64-bit. A conditional of literals compared with an unsigned value, or on the left of arithmetic with one, needs `u` suffixes. Write the cast or suffix until this is fixed.

## BUG-435: Overflow in an integer constant expression is not diagnosed

Date/Time: 2026-10-29 11:24 EDT

Summary:
An integer constant expression, built from literals and named inline constants, is computed in its natural type, as in C: `int`, or `long` when an operand is `long`. When a step overflows that type, or a shift count is not smaller than the width of its left operand, the program is accepted. The generated C then has signed overflow or an oversized shift, which is undefined behaviour, and the value is wrong or unpredictable.

Steps to Reproduce:

1. Declare `inline int PAGE = 4096;` at global scope.
2. In `main`, declare `int x = 65536 * 65536;`, `int z = 2147483647 + 1;`, `long y = 1 << 40;` and `int w = PAGE * 1048576;`.
3. Log each value, and compile and run.

Expected:
Each initializer is rejected at the operation that overflows, naming the type it is computed in. A wider or unsigned operand is the fix, for example `65536L * 65536` or `1L << 40`. This holds even when the target could hold the true result: `uint u = 65536 * 65535;` overflows `int` and is rejected.

Actual:
The program compiles and logs `0`, `-2147483648`, an unpredictable value for `y` (for example `140704628754960`), and `0`. The generated C keeps each expression as written, for example `int64_t y = (1 << 40);`.

Known Impact:
A constant that overflows silently gives a wrong value, or an unpredictable one for an oversized shift, with no diagnostic. Use a `l`, `u` or `ul` suffix on an operand when a constant can exceed `int`.


## BUG-436: A `uchar` does not convert implicitly to `int` or `nint`

Date/Time: 2026-10-29 12:40 EDT

Summary:
A `uchar` holds a code point in `0..0x10FFFF`, so every value fits in `int` and in `nint` (at least 32 bits). A character type converts implicitly to every integer type that holds its whole logical range and is at least 16 bits wide, so `uchar` converts to `int` and `nint`, as it already does to `uint`, `long`, `ulong` and `nuint`. The conversion is rejected for `int` and `nint` only, as if `uchar` were an unsigned 32-bit integer.

Steps to Reproduce:

1. In a function with a `uchar u` parameter, write `int i = u;` and `nint n = u;`.
2. Also write `uint ui = u;` and `long l = u;`.
3. Compile.

Expected:
All four declarations compile.

Actual:
`int i = u;` reports `Declaration initializer cannot convert 'uchar' to 'int'.` and `nint n = u;` reports the same for `'nint'`. The `uint` and `long` declarations compile.

Known Impact:
A `uchar` code point cannot be stored in an `int` without a cast. Write `(int)u` until this is fixed.

## BUG-437: `achar` converts implicitly to and from the other character types

Date/Time: 2026-10-29 12:40 EDT

Summary:
`achar` is an ASCII-encoded character with the same logical range as `char` (`0..0x7F`) but a different encoding, so it has no implicit conversion to or from `char`, `wchar` or `uchar`. A cast is required. It converts implicitly only to the integer and floating types that hold its range. The compiler accepts `char` to `achar`, and `achar` to `char`, `wchar` and `uchar`. A `wchar` or `uchar` to `achar` is already rejected.

Steps to Reproduce:

1. In a function with `char c`, `achar a` parameters, write `achar a2 = c;`, `char c2 = a;`, `wchar w = a;` and `uchar u = a;`.
2. Also write `int i = a;` and `double d = a;`.
3. Compile.

Expected:
The first four declarations are rejected as conversions that need a cast. The last two compile.

Actual:
All six declarations compile.

Known Impact:
Text in two encodings is mixed without a cast, so a conversion that should be explicit is silent. Do not rely on it.
