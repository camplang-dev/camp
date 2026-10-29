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

Next bug number: BUG-430.

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

