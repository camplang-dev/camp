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

Next bug number: BUG-373.

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
