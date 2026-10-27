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

Next bug number: BUG-392.

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

## BUG-389: An out-of-range integer literal passed in `base(...)` to a carried generic constructor parameter is not diagnosed

Date/Time: 2026-10-27 07:30 EDT

Summary:
An integer literal that does not fit its target type is rejected with "Integer literal '300' is outside the range of type 'byte'." for declarations, assignments, ordinary calls, `new` constructor arguments and `base(...)` calls whose parameter has a plain type. When the parameter is the carried form of a generic class parameter (`T: int` constructed as `byte`, so `int<byte>`), the same literal is accepted silently in `base(...)`, while an ordinary call to a function taking `int<byte>` and `new Seed<byte>(300)` are diagnosed.

Steps to Reproduce:

1. Declare `virtual class Seed<T: int> { T first; Seed(T first) { this.first = first; } }` and `virtual class Plain { byte first; Plain(byte first) { this.first = first; } }` (with the usual `malloc` and `free` externs).
2. Declare `sealed class A: Seed<byte> { A() { base(300); } }` and `sealed class B: Plain { B() { base(300); } }`.
3. Declare `void takes(int<byte> value) { }` and call `takes(300)` and `new Seed<byte>(300)` from `main`.
4. Build with `campc run`.

Expected:
Every occurrence of `300` is rejected with "Integer literal '300' is outside the range of type 'byte'." at the literal, including the one in `A`.

Actual:
The literals in `B`, in `takes(300)` and in `new Seed<byte>(300)` are diagnosed. The literal in `A` is not, and the program is accepted when the other three are removed.

Known Impact:
A value that does not fit is silently truncated into the base class field. Workaround: pass a value of the carrier type, or an in-range literal.

## BUG-390: A virtual or abstract method of a generic class cannot mention the class's type parameter in its signature

Date/Time: 2026-10-27 07:57 EDT

Summary:
In a generic class, a `virtual` or `abstract` method whose parameter or result type mentions the class's type parameter (`virtual T* get()`, `virtual T next(T x)`, `abstract T* take()`) is rejected with "Unknown type 'T'." at that use. Fields and non-virtual methods of the same class may use `T`, and the same virtual method without `T` in its signature is accepted. As a consequence, an override in a class derived from a construction, written with the substituted types (`override int* get()` for `Box<int>`, `override int<byte> next(int<byte> x)` for an integer-represented `Num<byte>`), is also rejected, and a concrete class implementing an inherited abstract `T`-typed method is reported as not implementing it.

Steps to Reproduce:

1. Declare `virtual class Box<T> { T* held; virtual T* get() { return this.held; } virtual ~Box() { } }` (with the usual `malloc` and `free` externs).
2. Declare `sealed class IntBox: Box<int> { override int* get() { return null; } override ~IntBox() { } }`.
3. Declare `virtual class Num<T: int> { T value; virtual T next(T x) { return x; } virtual ~Num() { } }` and `sealed class ByteNum: Num<byte> { override int<byte> next(int<byte> x) { return x; } override ~ByteNum() { } }`.
4. Declare `abstract class Src<T> { abstract T* take(); virtual ~Src() { } }` and `sealed class IntSrc: Src<int> { override int* take() { return null; } override ~IntSrc() { } }`.
5. In `main`, construct each derived class, call the method through a pointer to its base construction (`Box<int>* b = new IntBox(); b.get();`, and so on), and delete it through that pointer. Build with `campc run`.

Expected:
The program builds and runs. Each `T` in a virtual or abstract signature resolves to the class's type parameter, and each override written with the base construction's substituted types (`int*`, `int<byte>`) overrides the inherited member.

Actual:
"Unknown type 'T'." at each `T` in a virtual or abstract signature, followed by "Override method 'get' must match an inherited virtual or abstract method." for each override and "Class 'IntSrc' must use override to implement inherited abstract member 'take()'." for the abstract case. `Box<int>` alone, with no derived class, already fails.

Known Impact:
A generic class hierarchy cannot dispatch virtually on any member whose signature mentions a type parameter. Workaround: keep virtual signatures free of type parameters and reach `T` through non-virtual members.

## BUG-391: Dereferencing an inherited type-parameter pointer field through `this` in a class derived from a concrete construction emits invalid C

Date/Time: 2026-10-27 07:57 EDT

Summary:
A class derived from a concrete construction of a generic class (`class IntBox: Box<int>`, where `Box<T>` declares `T* held`) sees the inherited field as `int*`. Reading it into a local and dereferencing that works, but dereferencing the field directly (`*this.held`) emits C that dereferences the erased `void*` field, and the native build fails.

Steps to Reproduce:

1. Declare `class Box<T> { T* held; }` (with the usual `malloc` and `free` externs).
2. Declare `class IntBox: Box<int> { int readHeld() { return *this.held; } }`.
3. In `main`, write `int a = 5; IntBox* b = stackalloc IntBox(); b.held = &a;` and log `b.readHeld()`.
4. Build with `campc run`.

Expected:
The program prints `5`. `*this.held` has type `int`, as it does when written `int* p = this.held; return *p;`.

Actual:
The native build fails: "returning 'void' from a function with incompatible result type 'int'" for the emitted `return *this->held;`, with the warning "ISO C does not allow indirection on operand of type 'void *'".

Known Impact:
Derived classes of concrete generic constructions cannot use inherited pointer fields directly in expressions. Workaround: copy the field into a typed local first.
