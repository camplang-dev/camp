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

Next bug number: BUG-414.

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

## BUG-412: Constructing a shadow class whose constructor takes a `within` parameter produces C that does not compile

Date/Time: 2026-10-28 15:10 EDT

Summary:
The shadow class semantics allow a shadow constructor to accept a `within` allocator parameter, and describe how `delete shadow` interacts with it. A program that actually constructs such a class with `new` passes semantic analysis, but the generated C passes the resolved allocator to the generated constructor helper twice, so the helper call has one more argument than the helper declares and the native build fails.

Steps to Reproduce:

1. Declare `interface Allocator` with `alloc`, `realloc` and `free`, and a class that implements it.
2. Declare `class Host` with an `escaped void* shadowData` field, a constructor, and `@getshadow` and `@setshadow` methods that read and write `shadowData`.
3. Declare `shadow class Pooled: Host` with an `int value` field and a constructor `Pooled(int value, within allocator)`, plus a method containing `delete shadow;`.
4. In `main`, write `Pooled* p = new Pooled(5);`. The same failure occurs with `within (allocator) new Pooled(5)`.
5. Compile the program.

Expected:
The program compiles. The constructor receives the allocation context once, as it does for a non-shadow class with a `within` constructor parameter.

Actual:
Semantic analysis reports no error. The native build fails with `too many arguments to function call, expected 3, have 4` at the generated call of the constructor helper, where the resolved allocator appears twice in the argument list.

Known Impact:
Any shadow class with a `within` constructor parameter cannot be constructed. A workaround is to pass the allocator as an ordinary parameter, which can be stored in a shadow field and used by `within (this.field) delete shadow;`.

## BUG-413: A non-virtual class derived from a virtual shadow class is accepted and crashes at run time

Date/Time: 2026-10-28 15:40 EDT

Summary:
A class that derives from a virtual or abstract class must itself be declared virtual, abstract, or sealed, and the compiler reports an error for an ordinary class that does not. That check is skipped when the derived class is a shadow class. Because a shadow class cannot be `sealed`, an ordinary shadow class that derives from a virtual shadow class is accepted. When it adds no override of its own, no dispatch table is installed for it, so calling an inherited virtual method through it crashes the program.

Steps to Reproduce:

1. Declare `class Host` with an `escaped void* shadowData` field, a constructor, and `@getshadow` and `@setshadow` methods that read and write `shadowData`.
2. Declare `virtual shadow class Shape: Host` with an `int side` field, a constructor that sets it, `virtual int area() => this.side * this.side;`, and a method containing `delete shadow;`.
3. Declare `shadow class Square: Shape` (not virtual) with a constructor `Square(int side) { base(side); }` and no override.
4. In `main`, write `Square* s = new Square(4); __intrinsic_log_i32(s.area());` and call the `delete shadow;` method.
5. Compile and run the program.

Expected:
The compiler reports an error on `Square`, as it does for a non-shadow class that derives from a virtual class without being marked virtual, abstract, or sealed. Declaring `Square` as `virtual shadow class` makes the program compile and print 16.

Actual:
Semantic analysis reports no error for the ordinary `Square`, and the program exits with a segmentation fault instead of printing 16. If `Square` is declared `virtual shadow class`, or if it declares its own override, the program prints the expected value.

Known Impact:
Any ordinary shadow class under a virtual or abstract shadow base. A workaround is to declare such classes `virtual` or `abstract`.
