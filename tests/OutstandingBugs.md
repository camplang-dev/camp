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

Next bug number: BUG-380.

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


## BUG-373: A sealed method can be overridden again in a derived class

Date/Time: 2026-10-26 13:06 EDT

Summary:
A method declared `sealed` in a virtual class is documented as the point where overriding stops. The compiler accepts a later `override` of the sealed method in a class derived from the sealing class, with no diagnostic. The override replaces the sealed implementation at run time.

Steps to Reproduce:

1. Declare `virtual class Base { virtual int get() { return 1; } }`.
2. Declare `virtual class Middle: Base { sealed int get() { return 2; } }`.
3. Declare `sealed class Leaf: Middle { override int get() { return 3; } }`.
4. Compile a program whose `main` creates a `Leaf` with `stackalloc` and returns `get()`.

Expected:
The compiler rejects the override in `Leaf`, because `get` was sealed by `Middle` and can no longer be overridden.

Actual:
The program compiles without any diagnostic and returns 3, so the sealed method is replaced.

Known Impact:
`sealed` on a method has no effect on classes that derive from the class that seals it, so a base class cannot protect a method from being overridden further down the hierarchy.

## BUG-374: Abstract classes can be instantiated

Date/Time: 2026-10-26 13:06 EDT

Summary:
The language guide describes an abstract class as a base class that is not directly constructed. The compiler accepts `new`, `stackalloc` and constructor-shaped construction of an abstract class without a diagnostic, including a class that declares an abstract method with no implementation.

Steps to Reproduce:

1. Declare `abstract class Shape { abstract int area(); }`.
2. In a function, write `Shape* a = stackalloc Shape();`.
3. Declare `extern escaped void* malloc(nuint size);` and write `Shape* b = new Shape();`.
4. Declare `abstract class Other { }` and write `Other c = Other();`.
5. Compile the program.

Expected:
Each construction of an abstract class is rejected with a diagnostic stating that an abstract class cannot be instantiated.

Actual:
The program compiles and links without a diagnostic. Calling `area()` on the instance would dispatch through a vtable slot that has no implementation.

Known Impact:
Nothing prevents a program from creating an instance of an incomplete class, so the guarantee that abstract members are implemented before they are called does not hold.

## BUG-375: An abstract class must restate abstract members it inherits

Date/Time: 2026-10-26 13:06 EDT

Summary:
A class that derives from an abstract class and is itself abstract is rejected unless it repeats `override` for every inherited abstract method. The documented requirement is that an abstract member must be implemented by the concrete classes that derive from it, so an abstract class that leaves the member unimplemented is valid.

Steps to Reproduce:

1. Declare `abstract class Shape { abstract int area(); }`.
2. Declare `abstract class Half: Shape { }`.
3. Declare `sealed class Square: Half { override int area() { return 1; } }`.
4. Compile the program.

Expected:
The program compiles. `Half` is abstract and defers `area` to `Square`, which implements it.

Actual:
The compiler reports `Class 'Half' must use override to implement inherited abstract member 'area()'.` at the name of `Half`.

Known Impact:
An abstract class cannot be used as an intermediate layer of a hierarchy unless it declares an implementation or a forwarding stub for every abstract member of its bases.

## BUG-376: A concrete class must restate an abstract member that a base class already implements

Date/Time: 2026-10-26 13:06 EDT

Summary:
When a concrete class derives from a class that already overrides an abstract member of its own base, the compiler still reports the member as unimplemented. The check collects the abstract declarations of every ancestor and looks for an override only in the class being checked, ignoring implementations inherited from the intermediate class.

Steps to Reproduce:

1. Declare `abstract class Shape { abstract int area(); }`.
2. Declare `virtual class Half: Shape { override int area() { return 1; } }`.
3. Declare `sealed class Square: Half { }`.
4. Compile the program.

Expected:
The program compiles, and `Square` uses the implementation inherited from `Half`.

Actual:
The compiler reports `Class 'Square' must use override to implement inherited abstract member 'area()'.` at the name of `Square`.

Known Impact:
A derived class has to repeat an override that an ancestor already provides, which makes it impossible to extend a concrete implementation without duplicating it.

## BUG-377: Creating a derived class with new and assigning it to a base pointer emits invalid C

Date/Time: 2026-10-26 13:06 EDT

Summary:
Initializing a base class pointer directly from `new` of a derived class type produces C that declares the variable with the base type but allocates, assigns and constructs it as the derived type. The compiler does not report a Camp diagnostic and the native build fails. Allocating into a derived pointer first and then assigning to a base pointer works.

Steps to Reproduce:

1. Declare `virtual class Base { Base() { } virtual int get() { return 1; } }` and `sealed class Leaf: Base { Leaf() { } override int get() { return 2; } }`.
2. Declare `extern escaped void* malloc(nuint size);` and `extern void free(void* ptr);`.
3. In `main`, write `Base* b = new Leaf();`, call `b.get()` and `delete b;`.
4. Compile and run the program.

Expected:
The program compiles and `b.get()` returns 2 through virtual dispatch.

Actual:
The native build fails: the generated C initializes `Base *b` from a `Leaf *`, assigns `*b = (Leaf){0};` (`assigning to 'Base' from incompatible type 'Leaf'`) and passes `b` to the `Leaf` initializer.

Known Impact:
A natural way to create an object behind its base type does not compile. The workaround is `Leaf* l = new Leaf(); Base* b = l;`.

## BUG-378: Calls through an inherited generic interface return wrong values

Date/Time: 2026-10-26 13:06 EDT

Summary:
A type that implements a constructed generic interface which inherits from another generic interface can be used through the derived interface, and the inherited members are documented to be callable on it or through a conversion to the base construction. The calls compile, but the inherited members return uninitialized values.

Steps to Reproduce:

1. Declare `interface Source<T> { T read(); }` and `interface Counted<T>: Source<T> { int count(); }`.
2. Declare `struct Ints: Counted<int> { int n; int read(): Source<int> { return this.n; } int count(): Counted<int> { return 2; } }`.
3. In `main`, create an `Ints` with `n = 8`, convert it to `Counted<int>*`, and call `count()` and `read()` on it.
4. Convert the `Counted<int>*` to `Source<int>*` and call `read()`.

Expected:
`count()` returns 2 and both `read()` calls return 8.

Actual:
`count()` returns 2. The `read()` call through `Counted<int>*` returns an arbitrary value (32759 in one run) and the `read()` call through `Source<int>*` returns 0.

Known Impact:
Inherited members of a generic interface cannot be used, and nothing diagnoses it.

## BUG-379: An implementation marker naming the derived interface for an inherited member emits invalid C

Date/Time: 2026-10-26 13:06 EDT

Summary:
A type that implements an interface inheriting a method from a base interface can mark the inherited method with the name of the derived interface. The compiler accepts the marker without a diagnostic, and the generated C calls a member that does not exist on the implementing struct. Whichever rule applies to the marker of an inherited member, the compiler should either generate working code or report a diagnostic.

Steps to Reproduce:

1. Declare `interface Reader { int read(); }` and `interface Seekable: Reader { void seek(int position); }`.
2. Declare `struct Wrong: Seekable { int read(): Seekable { return 1; } void seek(int position): Seekable { } }`.
3. In `main`, create a `Wrong`, convert it to `Seekable*`, convert that to `Reader*`, and call `read()` on both pointers.
4. Compile the program.

Expected:
The program either compiles and returns 2, or the marker `: Seekable` on `read` is rejected with a diagnostic.

Actual:
Semantic analysis reports nothing and the native build fails: the generated slot function for `read` contains `return instance->read();` and the struct has no member named `read`.

Known Impact:
A valid-looking program ends in a native compiler error with no Camp diagnostic pointing at the marker.

## BUG-380: A derived destructor does not run the base destructor

Date/Time: 2026-10-26 14:20 EDT

Summary:
When a class in a virtual hierarchy overrides a virtual destructor, deleting an instance runs only the most derived destructor body. The destructors of the base classes never run, so resources a base class owns are leaked. The destructor of a derived class is expected to be followed automatically by the base destructor, as if the derived body were wrapped in a `finally` that runs the base destructor, including when the derived body exits early with `return`.

Steps to Reproduce:

1. Declare `virtual class Root { Root() { } virtual ~Root() { log(1); } }`, where `log` records its argument.
2. Declare `virtual class Middle: Root { Middle() { } override ~Middle() { log(2); } }` and `sealed class Leaf: Middle { Leaf() { } override ~Leaf() { log(3); } }`.
3. In `main`, create a `Leaf` with `stackalloc`, store it in a `Root*` and delete the pointer.
4. Repeat with a `Leaf*` pointer, and with a `Leaf` destructor whose body ends in an early `return;`.

Expected:
The deletes log 3, 2, 1 in that order (derived body first, then each base destructor), including when the body returns early.

Actual:
Only 3 is logged. The generated override for `Leaf` contains the body of `~Leaf` and nothing else, and no code calls `~Middle` or `~Root`.

Known Impact:
Base classes cannot reliably release what they own. The only way to reach a base destructor is an explicit call that the language does not document.

## BUG-381: A derived destructor in a non-virtual hierarchy is rejected with a misleading diagnostic

Date/Time: 2026-10-26 14:20 EDT

Summary:
A class whose ultimate base declares a non-virtual destructor may not have derived classes that declare destructors, because the derived destructor could not be dispatched or chained. The compiler does reject the program, but with an unrelated message about a duplicate generated method rather than a diagnostic that names the rule. A derived class that declares no destructor works.

Steps to Reproduce:

1. Declare `class Plain { Plain() { } ~Plain() { log(5); } }`.
2. Declare `class Sub: Plain { Sub() { } ~Sub() { log(6); } }`.
3. Compile the program.

Expected:
A diagnostic at the destructor of `Sub` says that a derived class cannot declare a destructor because the ultimate base class `Plain` declares a non-virtual destructor (and that the base would need a virtual destructor).

Actual:
The compiler reports `Duplicate method name 'destroy' inherited from base class.`, which names a generated helper the program never declares.

Known Impact:
The error does not point at the rule that was broken or at a fix.

## BUG-382: A sealed destructor can be overridden by a derived class

Date/Time: 2026-10-26 14:20 EDT

Summary:
A destructor declared `sealed` in a virtual hierarchy should end the chain of overrides, like any sealed method. The compiler accepts an `override` destructor in a class derived from the sealing class, and the override is the one that runs.

Steps to Reproduce:

1. Declare `virtual class Root { Root() { } virtual ~Root() { log(1); } }`.
2. Declare `virtual class Middle: Root { Middle() { } sealed ~Middle() { log(2); } }`.
3. Declare `sealed class Leaf: Middle { Leaf() { } override ~Leaf() { log(3); } }`.
4. In `main`, create a `Leaf` with `stackalloc`, store it in a `Root*` and delete the pointer.

Expected:
The override in `Leaf` is rejected because the destructor of `Middle` is sealed.

Actual:
The program compiles and logs 3, so the sealed destructor is replaced.

Known Impact:
`sealed` has no effect on destructors.

## BUG-383: delete base is accepted and emits invalid C

Date/Time: 2026-10-26 14:20 EDT

Summary:
The expression `delete base;` is accepted in a class destructor. It is not described in the language or semantic documentation. The generated C calls the base destructor helper without the receiver argument, so the native build fails. Destructor chaining is performed automatically after the derived body, so `delete base;` is not part of the language and should be rejected.

Steps to Reproduce:

1. Declare `virtual class Root { Root() { } virtual ~Root() { log(1); } }`.
2. Declare `sealed class Leaf: Root { Leaf() { } override ~Leaf() { log(3); delete base; } }`.
3. In `main`, create a `Leaf` with `stackalloc` and delete it.
4. Compile the program.

Expected:
The compiler reports a diagnostic at `base` in `delete base;` saying it is not valid. The base destructor already runs automatically.

Actual:
Semantic analysis accepts the program and the native build fails with `too few arguments to function call, single argument 'this' was not specified` for the generated call `Root__op_delete();`.

Known Impact:
An undocumented form compiles to broken C instead of being rejected, and it would conflict with automatic destructor chaining if it were fixed.

## BUG-384: The semantic documentation does not define destructor chaining, sealing or the boundary call

Date/Time: 2026-10-26 14:20 EDT

Summary:
The documentation for destructors says only that lowering must preserve base destruction order, and that virtual hierarchies need a root destructor slot. It does not state when a base destructor runs, which classes may declare a destructor, that a destructor can be sealed, or how callers in other modules delete an object. These rules are intended: only the ultimate base class introduces a destructor; if it is not virtual, no derived class may declare one; if it is virtual, derived classes override it and the base destructor is called automatically (non-virtually) after the derived body, also when the body exits early; a destructor may be `sealed`; destructor implementations are not visible across a module or artifact boundary, and a caller outside the module deletes through the generated `destroy` helper, whether it dispatches virtually or not being an implementation detail; in-module `delete` calls the destructor entry point inline and then frees.

Steps to Reproduce:

1. Read the Destructors section of the construction and destruction semantic document, and the Virtual Class Dispatch section of the interface and dispatch semantic document.
2. Look for the chaining order, the declaring-class rules, `sealed` destructors and the cross-module deletion call.

Expected:
The semantic documents describe the rules above, matching the compiler, and the language guide's class lifetime chapter mentions that a base destructor runs after a derived one.

Actual:
The rules are absent. The only related statement is that destructor lowering must preserve base destruction order, which does not say whether base destructors run.

Known Impact:
A reader of the documents cannot tell what a derived destructor does with its base, so independent implementations cannot be checked against the intended behavior.
