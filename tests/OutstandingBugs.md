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

Next bug number: BUG-398.

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

## BUG-392: Listing an interface that the base class already implements fails with an unlocated duplicate-method error

Date/Time: 2026-10-27 07:57 EDT

Summary:
A derived class may list an interface that its base class already implements, or an interface whose base interface the base class already implements; the derived class inherits the base class's implementation of those members. The compiler instead fails with an unlocated "Duplicate method name 'getX()' inherited from base class." naming the generated interface accessor. It happens for non-generic and generic interfaces alike.

Steps to Reproduce:

1. Declare `interface Counter { int next(); }` and `class Impl: Counter { int n; int next(): Counter { return 1; } }` (with the usual `malloc` and `free` externs).
2. Declare `class Relists: Impl, Counter { }`.
3. In `main`, write `Relists* r = new Relists(); Counter* c = r;`, log `c.next()` and delete `r`.
4. Separately, declare `interface IBase { int echo(int b); }`, `interface IDer: IBase { int twice(int b); }`, `class BaseImpl: IBase { int echo(int b): IBase => b; }` and `class DerImpl: BaseImpl, IDer { int twice(int b): IDer => b; }`, convert a `DerImpl*` to both `IDer*` and `IBase*`, and call each member.
5. Build each with `campc run`.

Expected:
Both programs build. The first prints `1`; the second prints the results of `twice` and `echo`. Re-listing an interface is not an error, and the inherited implementation satisfies it.

Actual:
"(1,1) (no line,column) error: Duplicate method name 'getCounter()' inherited from base class." for the first program and "Duplicate method name 'getIBase()' inherited from base class." for the second. The generic forms (`Check<T>`, `IDer<T>: IBase<T>`) fail the same way.

Known Impact:
A derived class cannot add an interface that extends one its base implements, and cannot restate an inherited interface. Workaround: list the interface only on the base class.

## BUG-393: A base constructor's capability argument is matched by the base's parameter name instead of the substituted type argument

Date/Time: 2026-10-27 07:57 EDT

Summary:
When a derived class constructs a generic base whose constructor takes a capability (`Cap(sizeof(T))`), the compiler looks for evidence named after the base's own parameter (`sizeof_T`) instead of evidence for the type argument the derived class passes. A derived class whose type parameter has another name (`CapOnly<U>: Cap<U>` declaring `sizeof(U)`) is rejected. A class derived from a concrete construction (`CapOnly: Cap<long>`) is also rejected, although the compiler can supply every argument itself for a concrete construction and such a class needs no constructor.

Steps to Reproduce:

1. Declare `class Cap<T> { Cap(sizeof(T)) { } int width() { return (int)sizeof(T); } }` (with the usual `malloc` and `free` externs).
2. Declare `class Renamed<U>: Cap<U> { Renamed(sizeof(U)) { } }`.
3. Declare `class Concrete: Cap<long> { }`.
4. In `main`, construct `Renamed<long>` and `Concrete`, log `width()` of each and delete them.
5. Build with `campc run`.

Expected:
The program prints `8` twice. `Renamed`'s `sizeof(U)` is the evidence for the base's `sizeof(T)` because `U` is the argument for `T`; `Concrete` needs no constructor because `sizeof(long)` is known.

Actual:
"(1,1) (no line,column) error: sizeof(T) requires parameter 'sizeof_T'." for each class. Renaming the derived parameter to `T` removes this error for the forwarded case only, after which the program fails as in BUG-395.

Known Impact:
Derived classes must reuse the base's type parameter names, and concrete constructions of a capability-taking base cannot be derived from at all. Workaround: name the derived parameter as the base does.

## BUG-394: `sizeof(T)` of an integer-represented type parameter requires a declared capability

Date/Time: 2026-10-27 07:57 EDT

Summary:
For a type parameter with an integer representation (`T: int`), the compiler supplies `sizeof(T)` without a declaration, both as an expression and for a `sizeof(T)` slot of a called constructor, including a base constructor. The compiler instead requires a declared `sizeof(T)` capability in each case, as it does for a void-represented parameter.

Steps to Reproduce:

1. Declare `int sz<T: int>() { return (int)sizeof(T); }` (with the usual `malloc` and `free` externs) and log `sz<short>()` from `main`.
2. Separately, declare `class Foo<T: int> { T value; Foo(T seed) { this.value = seed; } int size() { return (int)sizeof(T); } }`, construct `Foo<short>` with a `short` seed and log `size()`.
3. Separately, declare `virtual class Num2<T: int> { T value; Num2(T seed, sizeof(T)) { this.value = seed; } int width() { return (int)sizeof(T); } virtual ~Num2() { } }` and `sealed class Carried<T: int>: Num2<T> { Carried(T seed) { base(seed); } }`, construct `Carried<byte>` with `250` and log `width()`.
4. Build each with `campc run`.

Expected:
Each program builds and logs the size of the type argument: `2` for `short` and `1` for `byte`.

Actual:
"sizeof(T) requires parameter 'sizeof_T'." at the `sizeof(T)` expression in the first two programs and at the `base(seed)` call in the third.

Known Impact:
Integer-represented generic code must declare `sizeof(T)` wherever it uses it. Workaround: declare the capability.

## BUG-395: A derived class that retains the same capability as its base emits a duplicate C struct member

Date/Time: 2026-10-27 07:57 EDT

Summary:
A generic class whose constructor takes a capability (`sizeof(T)`, `typenameof(T)`) retains it in the object. When a generic derived class's constructor declares the same capability, which it must do to supply it to the base constructor, the derived class retains it too, and the emitted C struct has the same member twice (`_sizeof_T`, `_typenameof_T`). Omitting the declaration is rejected as missing evidence, so no class can derive from a class whose constructor takes a capability for the forwarded parameter.

Steps to Reproduce:

1. Declare `class Cap<T> { Cap(sizeof(T)) { } int width() { return (int)sizeof(T); } }` (with the usual `malloc` and `free` externs).
2. Declare `class CapOnly<T>: Cap<T> { CapOnly(sizeof(T)) { } }`.
3. In `main`, construct `CapOnly<long>`, log `width()` and delete it.
4. Build with `campc run`. Repeat with `base(sizeof(T))` written in `CapOnly`'s constructor, and with `typenameof(T)` in place of `sizeof(T)`.

Expected:
The program prints `8`. The capability is retained once for the object, and both the base and derived methods read that one value.

Actual:
The native build fails with "duplicate member '_sizeof_T'" (or "duplicate member '_typenameof_T'") in the generated private header. Declaring `CapOnly() { }` without the capability is rejected with "sizeof(T) capability needs evidence for 'T' in the caller; declare sizeof(T) in its signature or pass a pointer.", which is correct.

Known Impact:
Generic hierarchies cannot use capability-taking base constructors. No workaround short of removing the capability from the base constructor.

## BUG-396: Explicit capability evidence in `base(...)` from a differently named type parameter is rejected

Date/Time: 2026-10-27 07:57 EDT

Summary:
A derived constructor may pass its own capability evidence explicitly to a base constructor's capability slot, by expression (`sizeof(U)`) or by generated name (`sizeof_U`). When the derived type parameter has a different name from the base's (`CapOnly<U>: Cap<U>`), the evidence for `U` is the evidence for the base's `T`, but the compiler rejects it as evidence for another operand.

Steps to Reproduce:

1. Declare `class Cap<T> { Cap(sizeof(T)) { } int width() { return (int)sizeof(T); } }` (with the usual `malloc` and `free` externs).
2. Declare `class CapOnly<U>: Cap<U> { CapOnly(sizeof(U)) { base(sizeof(U)); } }`.
3. In `main`, construct `CapOnly<long>`, log `width()` and delete it.
4. Build with `campc run`.

Expected:
The program prints `8`.

Actual:
"Capability slot 'sizeof_T' requires evidence for exact operand 'T', not an ordinary payload or another operand." at `sizeof(U)` in the `base(...)` call. A witness written as `base(vtableof_U_Counter, typenameof_U)` for `Dispatch(vtableof(T: Counter), typenameof(T))` is rejected the same way.

Known Impact:
Explicit evidence in `base(...)` works only when the derived parameter reuses the base's name. Workaround: name the derived parameter as the base does.

## BUG-397: Calling through an interface pointer field that holds a sealed implementer crashes

Date/Time: 2026-10-27 07:57 EDT

Summary:
A heap object of a `sealed` class that implements an interface, converted to the interface pointer and stored in a field of another heap object, crashes when a method is called through that field. The same program with the implementing class not `sealed` runs correctly.

Steps to Reproduce:

1. Declare `interface Store { int put(int item); }` and `sealed class Impl: Store { int pad; int put(int item): Store { return item + 1; } }` (with the usual `malloc` and `free` externs).
2. Declare `sealed class Inner { Store* store; int run(int item) { return this.store.put(item); } }`.
3. In `main`, write `Impl* a = new Impl(); Inner* inner = new Inner(); inner.store = a;` and log `inner.run(5)`.
4. Build with `campc run`. Repeat with `class Impl` instead of `sealed class Impl`.

Expected:
Both programs print `6`.

Actual:
With `sealed class Impl` the program exits with status 139 (segmentation fault) at the call; without `sealed` it prints `6`.

Known Impact:
Sealed classes cannot be stored as interface pointers in fields. Workaround: do not mark interface implementers `sealed`.
