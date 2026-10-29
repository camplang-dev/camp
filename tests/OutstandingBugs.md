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

Next bug number: BUG-417.

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
