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

Next bug number: BUG-420.

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

## BUG-416: An unsuffixed integer literal that does not fit `int` is truncated to `int` when no target type applies

Date/Time: 2026-10-28 23:00 EDT

Summary:
When an integer literal without a type suffix is used where no target type applies, such as the initializer of an `auto` local or an operand of untargeted arithmetic, and its value does not fit `int`, the compiler gives it type `int` and silently truncates the value. No diagnostic is reported, even for a value that does not fit `long`. Literals with a target type are handled correctly (`long x = 5000000000;` and `long y = 1 + 5000000000;` both keep their values).

Steps to Reproduce:

1. Declare a generic helper `void show<T>(in T value, sizeof(T), typenameof(T))` that logs `typenameof(T)` with the string log intrinsic.
2. In `main`, declare `auto a = 2147483648;`, `auto b = 5000000000;`, `auto c = 1 + 5000000000;`, `auto d = 0xFFFFFFFF;`, `auto e = -2147483649;`, `auto f = 4294967296;` and `auto g = 9223372036854775808;`, and call `show` on each. Also log `a`, `b`, `c`, `d`, `e` and `f` with the 32-bit integer log intrinsic.
3. Compile and run the program.

Expected:
An integer literal without a suffix and without a target type has type `int` when its value fits `int`, and `long` otherwise. Hexadecimal literals follow the same rule, and a negated literal is checked as a signed value. So `a`, `b`, `c`, `d`, `e` and `f` are `long` and hold `2147483648`, `5000000000`, `5000000001`, `4294967295`, `-2147483649` and `4294967296` (the 32-bit log calls in step 2 are then rejected as `long` to `int` conversions and should be removed or cast). A value that does not fit `long`, such as `g`, is reported as out of range at the literal.

Actual:
Every variable is inferred as `int` and no diagnostic is reported. `a` is `-2147483648`, `b` is `705032704`, `c` is `705032705`, `d` is `-1`, `e` is `2147483647`, `f` is `0`, and `g` is also `int`.

Known Impact:
Untargeted literals silently change value, and arithmetic built on them is wrong without any warning. Give the literal a target type (`long x = 5000000000;`) or cast it.

## BUG-417: Numeric literal suffixes are accepted but do not affect the literal's type

Date/Time: 2026-10-28 23:03 EDT

Summary:
The integer suffixes `u`, `l`, `ul` (in either case and either order) and the floating suffix `f` are accepted by the lexer, but they have no effect on the type of the literal. Without a target type, a suffixed integer literal is inferred as `int` and a suffixed floating literal as `double`. With a target type, any suffix is accepted regardless of the target, so a suffix that contradicts the target is not diagnosed.

Steps to Reproduce:

1. Declare a generic helper `void show<T>(in T value, sizeof(T), typenameof(T))` that logs `typenameof(T)` with the string log intrinsic.
2. In `main`, declare `auto a = 5u;`, `auto b = 5L;`, `auto c = 5UL;`, `auto d = 5lu;`, `auto e = 0x10u;` and `auto f = 1.5f;`, and call `show` on each.
3. Separately declare `byte g = 5u;`, `int h = 5L;`, `uint i = 5L;` and `int j = 1.5f;`.
4. Compile and run the program.

Expected:
A suffix fixes the type of the literal. `5u` is `uint` (`ulong` when the value does not fit `uint`), `5L` is `long`, `5UL` is `ulong`, and `1.5f` is `float`; suffixes are case-insensitive and `lu` means `ul`. A suffixed literal converts to a target only through the ordinary implicit conversions, so `long k = 5u;` is accepted, while each declaration in step 3 is rejected at the literal as an invalid conversion.

Actual:
`a`, `b`, `c`, `d` and `e` are inferred as `int`, and `f` is inferred as `double`. Every declaration in step 3 compiles without a diagnostic; `j` holds `1`.

Known Impact:
A suffix cannot be used to select a literal's type, and a wrong suffix is never reported. Use a typed declaration or an explicit cast to select the type.

## BUG-418: The `d` suffix and a floating suffix after digits with no decimal point are passed to the C compiler unrecognised

Date/Time: 2026-10-28 23:00 EDT

Summary:
A floating literal written with a `d` suffix (`2.5d`), or with an `f` or `d` suffix directly after decimal digits (`1f`, `2d`), is neither accepted nor diagnosed by the Camp front end. Its source text is copied into the generated C, where the native compiler rejects it. The build then fails with a C error that does not point at Camp source.

Steps to Reproduce:

1. In `main`, declare `double x = 2.5d;`, `float f = 1f;` and `double g = 2d;`.
2. Compile the program.

Expected:
A floating literal may end in `f` (type `float`) or `d` (type `double`). The suffix may follow a literal with a decimal point or plain decimal digits, so `2.5d`, `1f` and `2d` are valid. A suffix never applies to a hexadecimal literal, where `f` and `d` are digits, so `0x1f` stays an integer. Compilation succeeds and each variable holds its value.

Actual:
Semantic analysis reports nothing. The native build fails with `invalid suffix 'd' on floating constant` for `2.5d`, `invalid digit 'f' in decimal constant` for `1f`, and `invalid digit 'd' in decimal constant` for `2d`, reported against the generated C file.

Known Impact:
These literal spellings cannot be used, and failing to compile is reported at the wrong level. Write `2.5`, `1.0f` and `2.0` instead.

## BUG-419: Implicit conversions accept sign changes and integer-to-character conversions

Date/Time: 2026-10-28 23:01 EDT

Summary:
The implicit numeric conversion rules are too permissive. Conversions between signed and unsigned integer types are accepted in both directions at every width, as are several conversions that narrow or reinterpret the value: an integer type of at most 16 bits to `char` or `wchar`, `wchar` to `char` or `short`, `uint` to `nint`, and between `nint` and `nuint`. Each of these changes the value of negative or large operands without a cast or a diagnostic.

Steps to Reproduce:

1. In `main`, declare `int i = -1; uint u = i;` and log whether `u == 4294967295`.
2. Declare `uint big = 4000000000; int back = big;` and log `back`.
3. Declare `sbyte s = -1; ulong w = s;` and `byte b = 200; sbyte sb = b;`, and log `sb`.
4. Declare `short sh = -1; char c = sh; wchar wc = sh; nuint nu = i;`.
5. Declare `wchar w2 = 65; char c2 = w2; short s2 = w2;`, `uint u3 = 1; nint n = u3;` and `nint n2 = 1; nuint nu2 = n2;`.
6. Compile and run the program.

Expected:
A variable converts implicitly only to a type that holds every value of its own type. In particular, signed to unsigned is never implicit at any width, nor is unsigned to a signed type of the same width, and an integer type never converts implicitly to a character type. The implicit integer conversions are:

- `sbyte` to `short`, `int`, `long`, `nint`;
- `byte` to `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `nint`, `nuint`;
- `short` to `int`, `long`, `nint`;
- `ushort` to `int`, `uint`, `long`, `ulong`, `nint`, `nuint`;
- `int` to `long`, `nint`;
- `uint` to `long`, `ulong`, `nuint`;
- `nint` to `long`;
- `nuint` to `ulong`.

Every integer type converts implicitly to `float` and `double`. `wchar` converts to `ushort` and wider integer types but not to `short` or `char`, and `char` converts to `wchar`. `nint` and `nuint` are treated as between 32 and 64 bits wide. All the other declarations above are rejected at the initializer as requiring an explicit cast.

Actual:
The program compiles and runs. `u == 4294967295` is true, `back` is `-294967296`, `w == 18446744073709551615` is true, and `sb` is `-56`. The other declarations compile without a diagnostic. Beyond the cases above, the same permissive rule accepts every sign change between `int`/`uint`, `long`/`ulong` and `short`/`ushort`.

Known Impact:
Negative or large values change silently at implicit conversions, and an integer can be assigned to a character type without a cast. Cast explicitly where the conversion is intended. Tightening the rules will require explicit casts in existing code that relies on them.
