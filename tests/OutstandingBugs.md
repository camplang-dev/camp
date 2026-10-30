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

Next bug number: BUG-439.

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

## BUG-442: `null` as the first of two arguments for one generic parameter emits an undeclared type

Date/Time: 2026-10-29 23:46 EDT

Summary:
When a generic call passes `null` first and a pointer second for the same top-level parameter, the parameter is inferred correctly from the pointer, but the `null` argument is emitted as a temporary of a type named `_NULL`, which the C output never declares, so the build fails with a native compiler error and no source diagnostic. With the arguments the other way round, the `null` is emitted as a temporary of the inferred pointer type and the call works.

Steps to Reproduce:

1. Declare `void two<T>(in T a, in T b, sizeof(T), typenameof(T)) { __intrinsic_log_string(typenameof(T)); }`.
2. In `main`, declare `int v = 1;` and `int* p = &v;`, then call `two(null, p);`.
3. Also call `two(p, null);`.
4. Compile and run the program.

Expected:
Both calls infer `T` as `int*` and pass a null pointer for the `null` argument.

Actual:
`two(p, null)` compiles and logs `Int`. `two(null, p)` fails to build with `error: use of undeclared identifier '_NULL'` at the generated `two(&(_NULL){NULL}, &p, sizeof(int *), "Int");`.

Known Impact:
A generic call cannot write `null` before the pointer that fixes the type. Write the pointer first, or pass the type argument explicitly.

## BUG-443: A generic parameter that only `null` supplies is inferred as `#NULL` instead of failing inference

Date/Time: 2026-10-29 23:46 EDT

Summary:
When the only argument for a generic parameter is `null`, inference gives the parameter the internal type `#NULL` instead of reporting that the type arguments cannot be inferred. The program then fails in the native compiler with an undeclared `_NULL` type, and no source diagnostic is reported. `default` and a brace list in the same position are reported correctly at the called name.

Steps to Reproduce:

1. Declare `void one<T>(in T a, sizeof(T), typenameof(T)) { __intrinsic_log_string(typenameof(T)); }` and `void two<T>(in T a, in T b, sizeof(T), typenameof(T)) { __intrinsic_log_string(typenameof(T)); }`.
2. Declare `void pair<A, B>(in A a, in B b, sizeof(A), sizeof(B), typenameof(A), typenameof(B)) { }`.
3. In `main`, call `one(null);`, `two(null, null);` and `pair(1, null);`. Also call `one(default);`.
4. Compile the program.

Expected:
Each of the three `null` calls is reported at the called name with `The type arguments for method '...' cannot be inferred from the usage. Try specifying the type arguments explicitly.`, as `one(default)` is.

Actual:
`one(default)` is reported. The three `null` calls compile to C such as `one(&(_NULL){NULL}, sizeof(_NULL), "#NULL");` and the build fails with `error: use of undeclared identifier '_NULL'`.

Known Impact:
A generic call whose parameter is supplied only by `null` fails in the native compiler with no source location. Pass the type argument explicitly (`one<int*>(null)`).
