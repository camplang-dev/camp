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
