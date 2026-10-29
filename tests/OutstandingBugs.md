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

Next bug number: BUG-435.

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

## BUG-428: A static field is visible by its bare name outside its declaring type

Date/Time: 2026-10-29 07:09 EDT

Summary:
A static field or static constant declared inside a class or newtype can be named without its type qualifier from code that is not in that type, including global functions and methods of unrelated types. Static members are documented as members of their type, reached as `Type.member`. Static methods are correctly not found by bare name, so only static fields are affected. Because ordinary lookup now wins over an enum member named by the target type (BUG-426), such a bare name also hides a same-named enum member in `switch` labels and declarations.

Steps to Reproduce:

1. Declare `public class Holder` containing `public static const int LIMIT = 7;` and `public static int twice(int v) => v * 2;`.
2. Declare `public struct Other` with a method `public int get() => LIMIT;`.
3. In `main`, declare `int a = LIMIT;` and log `a` and `Other.get()` through an assigned `Other` value.
4. Separately, declare `public newtype Span: long` with `public static const Span INVALID = (Span)(long)-1;` and `enum Kind { NONE, INVALID, OTHER }`, then write `switch (k) { case INVALID: ... }` on a `Kind` value `k`.
5. Compile.

Expected:
`LIMIT` is not found in `main` or in `Other.get`, because it is a member of `Holder`; the program must write `Holder.LIMIT`. The call to `twice` is rejected in the same way. In step 4, `case INVALID` selects `Kind.INVALID`.

Actual:
`LIMIT` resolves in both places with no diagnostic. A call to `twice` reports that the symbol cannot be found. In step 4, the case label resolves to `Span.INVALID` and the compiler reports `Switch case type 'const Span' is not compatible with switch type 'Kind'`; `Kind k = INVALID;` likewise reports that `const Span` cannot convert to `Kind`.

Known Impact:
Static fields leak into every scope that can see their declaring type, so unrelated code can silently bind to them, and a bare enum member can be shadowed by an unrelated type's static field. Qualify enum members (`Kind.INVALID`) or rename the static field until this is fixed.

## BUG-429: An unsuffixed literal on the left of arithmetic with a `uint` operand gives type `int`

Date/Time: 2026-10-29 07:09 EDT

Summary:
In `4 + u`, with `u` a `uint`, the literal does not adapt to the other operand and the expression is typed `int`, while `u + 4` is typed `uint`. The result depends on operand order. The value is computed in unsigned arithmetic but labelled `int`, so values above `int` range change sign silently under `auto`, and an assignment to a `uint` is rejected. The same holds for `-`, `*`, `|` and `&`. `nuint` operands and comparisons (`4 == u`) are not affected.

Steps to Reproduce:

1. In `main`, declare `uint u = 4000000000;`.
2. Declare `auto a = 4 + u;` and `auto b = u + 4;`.
3. Print `typenameof` of each (for example through a generic function with a `typenameof(T)` parameter) and log `a` with the 32-bit integer log intrinsic.
4. Also write `uint c = 4 + u;` and `uint d = 4 - u;` and compile them.

Expected:
A fitting unsuffixed literal takes the other operand's type, so `a` and `b` are both `uint` and both hold `4000000004`. `uint c = 4 + u;` and `uint d = 4 - u;` are accepted.

Actual:
`a` is `int` and logs `-294967292`; `b` is `uint`. `uint c = 4 + u;` is rejected with `Declaration initializer cannot convert 'int' to 'uint'`, and `size += 4 + u;` with `Assignment cannot convert 'int' to 'uint'`.

Known Impact:
Literal-first arithmetic on `uint` values changes sign silently under `auto` and is rejected where a `uint` is required. Put the literal on the right (`u + 4`) or cast the literal (`4u`) until this is fixed.

## BUG-430: Arithmetic with a `byte`, `ushort` or `short` left operand and a `uint` right operand gives type `int`

Date/Time: 2026-10-29 07:16 EDT

Summary:
In `b * u`, with `b` a `byte` and `u` a `uint`, the expression is typed `int`, while `u * b` is typed `uint`. The same holds for a `ushort` left operand (`s + u`) and for the other arithmetic and bitwise operators. The result depends on operand order. The operation runs in unsigned arithmetic but the result is labelled `int`, so values above `int` range change sign silently under `auto`, and an assignment to a `uint` is rejected. With a `short` left operand (`h + u`) the expression is also typed `int` and accepted, although neither operand type converts implicitly to the other. A `ulong` or `nuint` right operand is not affected.

Steps to Reproduce:

1. In `main`, declare `byte b = 1; ushort s = 1; short h = 1; uint u = 4000000000;`.
2. Declare `auto x = b * u; auto y = u * b; auto z = s + u; auto q = h + u;`.
3. Print `typenameof` of each (for example through a generic function with a `typenameof(T)` parameter) and log `x`, `y`, `z` and `q` with the 32-bit integer log intrinsic.
4. Also write `uint v = b * u;` and compile it.

Expected:
`x`, `y` and `z` are all `uint`, because `byte` and `ushort` convert implicitly to `uint`; `x` and `y` both hold `4000000000`. `h + u` is rejected because neither `short` nor `uint` converts implicitly to the other. `uint v = b * u;` is accepted.

Actual:
`x`, `z` and `q` are `int`, and they log `-294967296`, `-294967295` and `-294967295`; `y` is `uint`. `h + u` is accepted. `uint v = b * u;` is rejected with `Declaration initializer cannot convert 'int' to 'uint'`.

Known Impact:
Mixed-width unsigned arithmetic with the narrower operand first changes sign silently under `auto` and is rejected where a `uint` is required. Put the `uint` operand first (`u * b`) or cast the narrower operand to `uint` until this is fixed.

## BUG-431: `out default` is accepted as an argument and emits invalid C

Date/Time: 2026-10-29 07:31 EDT

Summary:
An `out` argument written as `default` passes semantic analysis and is emitted as the address of a literal, so the native compiler rejects the generated code. An `out` argument must name writable storage, and the documented way to ignore an `out` value is the write-only discard `_`, which compiles and runs. The failure happens for scalar, struct and pointer parameters. An older bootstrap build compiled this form and ran it.

Steps to Reproduce:

1. Declare `struct Facts { int a; int b; }`, `int readInt(out int v) { v = 3; return 1; }` and `int readFacts(out Facts f) { f = default; return 2; }`.
2. In `main`, log `readInt(out default)` and `readFacts(out default)` with the 32-bit integer log intrinsic.
3. Compile and run the program.
4. Replace each `out default` with `out _` and compile again.

Expected:
Semantic analysis rejects `out default` at the argument, because `default` is not writable storage, and the diagnostic points to the `_` discard. With `out _` the program compiles and logs `1` and `2`.

Actual:
Semantic analysis reports no error. The native compiler fails on the generated call, for example `error: cannot take the address of an rvalue of type 'int'` for `readInt(&0)`. With `out _` the program compiles and runs.

Known Impact:
A call that ignores an `out` result with `default` fails late with a native compiler error that does not name the Camp source. Write the discard `_` instead.

## BUG-432: A malformed numeric literal suffix is not diagnosed

Date/Time: 2026-10-29 07:41 EDT

Summary:
The valid suffixes are `u`, `l`, `ul` and `lu` on integer literals (in either case), and `f` or `d` on decimal digits with or without a `.`. Any other suffix is accepted without a diagnostic. Some forms reach the native compiler and fail there with a message about C literal syntax, and others compile silently with a type the suffix does not describe.

Steps to Reproduce:

1. In `main`, declare `auto a = 5uu;`, `auto b = 1.5u;`, `auto c = 5uf;` and `auto d = 5ud;` in separate programs, since each fails to build.
2. In one program, declare `auto e = 5ll;`, `auto f = 5lul;`, `auto g = 1.5l;` and `auto h = 1.5fd;`, print the type of each (for example through a generic function with a `typenameof(T)` parameter), and log `g` with the 64-bit integer log intrinsic.
3. Compile and run each program.

Expected:
Each literal above is rejected at the literal with a diagnostic that names the invalid suffix.

Actual:
`5uu`, `1.5u`, `5uf` and `5ud` pass semantic analysis and fail in the native compiler, for example `invalid suffix 'uu' on integer constant` and `invalid suffix 'u.0' on integer constant`. `5ll` and `5lul` compile as `int`, `1.5l` compiles as `long` and holds `1`, and `1.5fd` compiles as `double`.

Known Impact:
A mistyped suffix either fails late with a native compiler message that does not name the Camp source, or silently produces a different type or a truncated value. Check literal suffixes by hand until this is fixed.

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

