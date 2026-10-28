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

Next bug number: BUG-416.

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

## BUG-411: Fields declared by the non-shadow base cannot be accessed through a shadow class

Date/Time: 2026-10-28 12:47 EDT

Summary:
A shadow class pointer has the same representation as a pointer to its first non-shadow base object, and the shadow class inherits that base's members. Calling a base method through a shadow class instance works, but reading or writing an instance field that the non-shadow base declares, through a shadow class pointer or through `this` inside a shadow class method, passes semantic analysis and then produces C that does not compile, because the generated C declares the shadow class only as an incomplete type and accesses the field through it.

Steps to Reproduce:

1. Declare `class Host` with an `escaped void* shadowData` field, an `int hostValue` field, a constructor, and `@getshadow` and `@setshadow` methods that read and write `shadowData`.
2. Declare `shadow class S: Host` with an `int v` field, a constructor and a method containing `delete shadow;`.
3. In `main`, write `S* s = new S(); __intrinsic_log_i32(s.hostValue);`. The same failure occurs for `this.hostValue` inside a method of `S`, and for a write such as `s.hostValue = 1;`.
4. Compile the program.

Expected:
The program compiles. `hostValue` is a field of the base object that `s` points at, so the access reads or writes that field and does not involve the shadow data.

Actual:
Semantic analysis reports no error, and the native build fails with `error: incomplete definition of type 'S'` at the generated field access `s->hostValue`.

Known Impact:
Any shadow class that reads or writes a field of its non-shadow base. A workaround is to access the field only through a method of the base class.

## BUG-415: A local may reuse the name of a visible outer local or parameter

Date/Time: 2026-10-28 16:35 EDT

Summary:
Declaring a local in a function body is rejected when the name is already declared in the same scope, but it is accepted when the name belongs to a local or parameter of an enclosing scope. The inner declaration silently shadows the outer one. Locals are not allowed to shadow other locals or parameters, so every one of these shapes should be an error. When the shadowed name is a parameter, semantic analysis reports nothing and the generated C does not compile.

Steps to Reproduce:

1. Write `int shadowLocal() { int n = 1; { int n = 2; return n; } }`.
2. Write `int shadowLoop() { for (int i = 0; i < 2; i++) { int i = 5; } return 0; }`. The same applies to the variable of a `foreach` loop and to a `for` loop that declares a variable already declared before the loop.
3. Write `int shadowParameter(int a) { int a = 1; return a; }`.
4. Compile a program that contains these functions and a `main`.

Expected:
Each inner declaration is reported as already declared, the same way a second declaration of `n` in a single scope is.

Actual:
The first two functions compile and the inner variable shadows the outer one. The third function passes semantic analysis and the native build fails with a redefinition error for the parameter name in the generated C.

Known Impact:
Any function that reuses a local or parameter name in a nested scope. A workaround is to use distinct names.
