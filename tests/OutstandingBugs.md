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

Next bug number: BUG-428.

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

## BUG-420: The common type of an untargeted conditional or array literal depends on operand order

Date/Time: 2026-10-28 23:01 EDT

Summary:
When no target type applies, such as under `auto`, the type of a conditional expression or array literal that mixes operand types depends on the order of the operands, and can silently change a value. For mixed `int` and `uint` operands the result is the type of the second arm of a conditional or of the first element of an array literal, regardless of which type can hold all the values. Mixes where one type widens to the other (`int` with `long`, an integer with `double`) are handled correctly in both orders.

Steps to Reproduce:

1. Declare a generic helper `void show<T>(in T value, sizeof(T), typenameof(T))` that logs `typenameof(T)` with the string log intrinsic.
2. In `main`, declare `uint u = 4000000000; bool c = true;`, then `auto a = c ? u : 0;`, `auto b = c ? 0 : u;`, `auto d = [u, 2];` and `auto e = [2, u];`.
3. Call `show` on `a`, `b`, `d` and `e`, and log `a` and `d[0]` with the 32-bit integer log intrinsic.
4. Also declare `int i = 4;` and `auto f = c ? u : i;`, `auto g = c ? i : u;` and `auto h = [i, u];`.

Expected:
The common type is a type among the operand types to which every operand converts implicitly, where an unsuffixed integer literal converts to any integer type that holds its value. If several types qualify, the one to which all the others convert is chosen. The result never depends on the order of the operands. So `a` and `b` are both `uint`, `d` and `e` are both `uint[]`, and `a` holds `4000000000`. Neither `uint` nor `int` converts to the other, so `f`, `g` and `h` are rejected as having no common type, at the first operand that does not convert to the widest candidate.

Actual:
`a` is `int` and logs `-294967296`, `b` is `uint`, `d` is `int[]` and `d[0]` logs `-294967296`, and `e` is `uint[]`. `f` is accepted as `int`, and `g` and `h` are accepted as `uint` and `uint[]`. None of these reports a diagnostic.

Known Impact:
A conditional or array literal over mixed signedness silently changes values, and the same expression gets a different type when its operands are reordered. Declare the destination type explicitly, or cast the operands to a common type.

## BUG-421: Generic type argument inference is fixed by the first argument

Date/Time: 2026-10-28 23:01 EDT

Summary:
When one type parameter is inferred from several by-value arguments, the first argument fixes the type argument and each later argument must then convert to it. Whether a call is accepted therefore depends on argument order: a call that passes the wider type first succeeds, and the same call with the arguments swapped fails with a conversion error at the later argument.

Steps to Reproduce:

1. Declare `void two<T>(in T a, in T b, sizeof(T), typenameof(T))` that logs `typenameof(T)` with the string log intrinsic.
2. In `main`, declare `int i = 1; long l = 2;` and call `two(l, i);`, `two(i, l);`, `two(l, 2);` and `two(2, l);`.
3. Compile and run the program.

Expected:
When several by-value arguments contribute to one type parameter, the type argument is the common type of the argument types: a type among them to which every argument converts implicitly, where an unsuffixed integer literal converts to any integer type that holds its value, and the one to which all the others convert if several qualify. The result does not depend on argument order. All four calls are accepted with `T` equal to `long`. If no argument type qualifies, for example `two(u, i)` and `two(i, u)` with a `uint` `u`, inference fails with the type arguments reported as not inferable.

Actual:
`two(l, i)` is accepted with `T` equal to `long`. `two(i, l)` is rejected with `Argument cannot convert 'long' to 'int'` at the second argument, and `two(2, l)` is rejected the same way. `two(u, i)` and `two(i, u)` are both accepted, with `T` equal to `uint` and `int`.

Known Impact:
Generic calls with mixed argument types succeed or fail depending on argument order. Reorder the arguments, or supply the type argument explicitly (`two<long>(i, l)`).

## BUG-422: `auto` initialised from an empty array literal emits invalid C

Date/Time: 2026-10-28 23:02 EDT

Summary:
An empty array literal has no element type, so it cannot supply a type to an `auto` local. The compiler accepts `auto a = [];` without a diagnostic and generates C that declares the variable with an unresolved error type, so the build fails in the native compiler. Other initializers that supply no type, such as `null`, are rejected at the initializer with "The type of this expression cannot be inferred; specify a type." An empty array literal with a target type (`int[] a = [];`) is accepted and has length 0.

Steps to Reproduce:

1. In `main`, declare `auto a = [];`.
2. Compile the program.

Expected:
Semantic analysis reports "The type of this expression cannot be inferred; specify a type." at the initializer `[]`, as it does for `auto a = null;`.

Actual:
Semantic analysis reports nothing. The native build fails with `use of undeclared identifier '_ERROR'` for the generated declaration `_ERROR *a = NULL;`.

Known Impact:
The mistake is reported as a native compiler error instead of a source diagnostic. Declare the element type explicitly (`int[] a = [];`).

## BUG-423: `null` is accepted where an optional is expected and emits invalid C

Date/Time: 2026-10-28 23:02 EDT

Summary:
`null` is the null pointer literal; an empty optional is written `default`. The compiler accepts `null` as an initializer, assignment value, argument or return value of an optional type such as `int?` without a diagnostic, and generates C that uses a pointer where the optional's payload and specified flag are expected. The native compiler then rejects the generated code. A plain integer (`int x = null;`) is correctly rejected with "Declaration initializer cannot convert '#NULL' to 'int'."

Steps to Reproduce:

1. Declare `void take(int? v) { }` and `int? give() { return null; }`.
2. In `main`, declare `int? o = default; o = null; take(null);` and `int? p = null;`.
3. Compile the program.

Expected:
Semantic analysis rejects each use of `null` (the initializer of `p`, the assignment to `o`, the argument to `take` and the return value of `give`) at the `null` expression, as a conversion error from `null` to `int?`. `int? o = default;` remains the way to write an empty optional.

Actual:
Semantic analysis reports nothing. The native build fails with errors such as `incompatible pointer to integer conversion returning 'void *' from a function with result type 'int'` and `too few arguments to function call, expected 2, have 1`, reported against the generated C file.

Known Impact:
Misusing `null` for an optional is reported as a native compiler error. Use `default` for an empty optional.

## BUG-424: `default` as the only argument for a type parameter infers an internal placeholder type

Date/Time: 2026-10-28 23:02 EDT

Summary:
`default` takes its type from its destination, so it cannot supply a type argument for a generic type parameter. When a call passes `default` as the only argument that mentions the type parameter, the compiler does not report that inference failed. It infers an internal placeholder type named `#TARGET` and compiles the call. Passing `null` in the same position is correctly rejected with "The type arguments for method ... cannot be inferred from the usage."

Steps to Reproduce:

1. Declare `void tn<T>(in T v, sizeof(T), typenameof(T))` that logs `typenameof(T)` with the string log intrinsic.
2. In `main`, call `tn(default);`.
3. Compile and run the program.

Expected:
Semantic analysis rejects the call at the called name with "The type arguments for method 'tn<T>' cannot be inferred from the usage. Try specifying the type arguments explicitly." Writing `tn<int>(default);` is accepted. A call that also passes an argument of known type for the same parameter, such as `two(5, default)` for `two<T>(in T a, in T b, ...)`, infers from that argument.

Actual:
The program compiles and runs, and logs `#TARGET` as the name of the inferred type.

Known Impact:
An internal placeholder type name leaks into user-visible output, and a call that cannot be inferred is accepted. Supply the type argument explicitly.

## BUG-425: A common pointer type is accepted for candidates with different lifetime annotations

Date/Time: 2026-10-28 23:02 EDT

Summary:
When a conditional expression, an array literal, or generic type argument inference combines pointer values, the candidates are joined into one pointer type without comparing their lifetime annotations. A `scoped` pointer and an `escaped` pointer are accepted together and silently produce a value whose lifetime is the weaker of the two. The explicit lifetime casts `(escaped)` and `(scoped)` already exist for making the lifetimes agree, but nothing requires them.

Steps to Reproduce:

1. Declare `void two<T>(in T a, in T b, sizeof(T), typenameof(T))` that logs `typenameof(T)` with the string log intrinsic.
2. Declare `void f(scoped int* s, escaped int* e, bool c)` whose body contains `auto r = c ? s : e;`, `auto a = [s, e];`, `two(s, e);` and `two(e, s);`.
3. Compile the program. Then replace the first conditional with `c ? (escaped)s : e` and with `c ? s : (scoped)e`.

Expected:
A common pointer type requires the lifetime annotations of all candidates to match exactly, so a `scoped` pointer never joins an `escaped` one. Each of the conditional, the array literal, `two(s, e)` and `two(e, s)` is rejected at the operand whose lifetime differs from the others, with a diagnostic that asks for an explicit lifetime cast. The two cast forms in step 3 are accepted, as are candidates whose annotations already match (`c ? s : t` for two `scoped` parameters).

Actual:
All four forms in step 2 compile without a diagnostic.

Known Impact:
A pointer whose lifetime is only borrowed can be mixed with a retained one without any explicit acknowledgement, which hides lifetime decisions that the language otherwise requires in the source. Add the explicit lifetime cast on the operand that should change.

## BUG-426: An enum member named by the target type hides a local variable or parameter of the same name

Date/Time: 2026-10-28 23:03 EDT

Summary:
A name that is visible as a local variable or parameter should resolve to that variable. Unqualified enum members are looked up through the target type only when ordinary name lookup finds nothing. The compiler instead prefers the target enum's member, so a local variable or parameter that has the same name as an enum member is silently ignored wherever the expression has that enum as its target. The program then runs with the enum member's value, or accepts a variable of an unrelated type.

Steps to Reproduce:

1. Declare `enum Kind { A, B }` and `void take(Kind A) { Kind k = A; __intrinsic_log_i32((int)k); }`.
2. In `main`, declare `Kind A = Kind.B; Kind k = A; __intrinsic_log_i32((int)k);` and call `take(Kind.B);`.
3. In a separate function, declare `int A = 5;` followed by `Kind j = A;`.
4. Compile and run the program.

Expected:
Ordinary lookup wins. In `main`, `A` is the local `Kind` variable with value `Kind.B`, so the program logs `1` for the local and `1` inside `take`. In step 3 `A` is an `int` variable and the declaration is rejected with a conversion error from `int` to `Kind` (an `int` never converts implicitly to an enum). An enum member is chosen from the target type only when no local, parameter, field or other declaration is visible under that name; the qualified form `Kind.A` always names the member.

Actual:
The local and the parameter are ignored: both log `0`, the value of `Kind.A`. The step 3 declaration is accepted, and `j` holds `Kind.A`.

Known Impact:
Renaming or adding a local or parameter can silently change which value an expression has, and an `int` variable can reach an enum without a diagnostic. Qualify the member (`Kind.A`) or avoid reusing enum member names as variable names.

## BUG-427: A floating literal is accepted for an integer target and truncated

Date/Time: 2026-10-28 23:09 EDT

Summary:
A floating literal such as `1.5` is accepted wherever an integer type is expected, and its fractional part is silently discarded. A floating variable is correctly rejected in the same position (`double d = 1.5; int i = d;` is an invalid conversion), so only literals are affected. The truncation happens for declarations, assignments, arguments and return values, for every integer target, and for negative literals.

Steps to Reproduce:

1. Declare `void take(int v)` that logs `v` with the 32-bit integer log intrinsic, and `int give() { return 2.75; }`.
2. In `main`, declare `int a = 1.5; byte b = 2.5; long c = 3.5; uint d = 4.5; char e = 5.5; int f = -1.5;`, call `take(6.5);`, assign `a = 7.5;`, and log `a`, `b`, `f` and `give()`.
3. Compile and run the program.

Expected:
A floating literal, with or without a suffix, takes only a `float` or `double` target. Each declaration, the assignment, the argument and the return value above is rejected at the literal as an invalid conversion to an integer type. An integer literal remains valid for a floating target (`float x = 3;`, `double y = 4;`).

Actual:
The program compiles and runs. The values are truncated toward zero: `take(6.5)` logs `6`, and `a`, `b`, `f` and `give()` log `7`, `2`, `-1` and `2` after the assignment. No diagnostic is reported.

Known Impact:
Fractional values are discarded silently in code that looks like a floating computation. Write the target as `float` or `double`, or cast the literal explicitly (`(int)1.5`).
