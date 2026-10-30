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

Next bug number: BUG-449.

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

## BUG-447: An unqualified enum member beside a qualified one in an untargeted conditional or array literal is not found

Date/Time: 2026-10-30 EDT

Summary:
In a conditional or array literal that no target type reaches, such as the initializer of an `auto` local, an enum-typed operand is meant to act as the target for the unqualified enum members beside it, as part of the best common type (semantics 14, Enum Member Target Typing). The compiler instead looks the unqualified name up on its own, finds nothing, and reports that the symbol could not be found. With a target type, for example `Kind[] kinds = [A, B];`, the same members resolve.

Steps to Reproduce:

1. Declare `enum Kind { A, B }`.
2. In a function with a `bool ready` parameter, write `auto kind = ready ? Kind.A : B;`.
3. Also write `auto kinds = [Kind.A, B];`.
4. Compile the program.

Expected:
Both initializers compile. `B` names `Kind.B` because the other operand has the enum type `Kind`, and each local has type `Kind` (`Kind[]` for the array literal).

Actual:
The compiler reports `Symbol 'B' could not be found.` at `B` in each initializer.

Known Impact:
Unqualified enum members cannot be mixed with a qualified one in an untargeted join. Qualify every member (`ready ? Kind.A : Kind.B`) or give the declaration a type (`Kind kind = ready ? A : B;`).

## BUG-448: A generic argument in an `out` slot does not take part in inferring the type parameter it shares with an `in` argument

Date/Time: 2026-10-30 EDT

Summary:
When one type parameter appears in both an `in T` slot and an `out T` slot, inference fixes the parameter from the `in` argument alone. An `out T` slot is meant to give an exact bound taken from its argument's type, which the `in` arguments of the same parameter must convert to (semantics 06, Type Argument Inference). The `out` argument is instead checked afterwards, in the opposite direction.

Steps to Reproduce:

1. Declare `void exchange<T>(in T source, out T destination, sizeof(T), typenameof(T))` with a body that assigns `destination = default;` and logs `typenameof(T)`.
2. In a function, declare `int small = 1;` and `long wide = 2;`.
3. Call `exchange(small, out wide);`.
4. Compile and run the program.

Expected:
`T` is inferred as `long`, the exact type of the `out` argument, and the `int` converts to it. The program logs `Long`.

Actual:
`T` is inferred as `int` from the `in` argument and the call is accepted. The program logs `Int`. The opposite call, `exchange(wide, out small)`, is rejected with `Out argument cannot convert 'long' to 'int'` at `small`, which agrees with the expected rule only by accident of the checking direction.

Known Impact:
The inferred type, and so `sizeof(T)` and `typenameof(T)`, differs from the documented rule when the two arguments have different types. Write the type argument explicitly (`exchange<long>(small, out wide)`) to get the documented result.
