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

Next bug number: BUG-313.

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

## BUG-310: `classtype` is rejected in result positions of virtual, abstract and override methods

Date/Time: 2026-10-19 09:48 EDT

Summary:
The language semantics allow `classtype` in a virtual, abstract, override or
sealed method only in result positions: the return type and a direct `out`
parameter type. Input parameters and callable types nested in the signature are
the rejected positions. The compiler rejects every use of `classtype` in a
virtual or abstract method, including the permitted result positions, with a
diagnostic that claims `classtype` is outside a class declaration. A second
diagnostic with no source location ("`classtype` may not be used in fields or
static fields") is also produced.

Steps to Reproduce:

1. Compile a program containing an abstract class that declares
   `abstract classtype* clone();` and
   `abstract void getPeer(out classtype* peer);`.
2. Compile a program containing a virtual class that declares
   `virtual classtype* clone() { return this; }`.
3. Declare a class derived from either one with
   `override classtype* clone() { return this; }`.

Expected:
Steps 1 and 2 compile. The override in step 3 is accepted and keeps `classtype`
in its result. A call through a derived static receiver type rebinds the result
to that type, and a call through the base static type yields the base type.
Only `classtype` in an input parameter, or inside a nested callable, iterator or
async type of a virtual, abstract, override or sealed signature, is diagnosed
(for example `abstract void compareTo(classtype* other);`).

Actual:
Each declaration in steps 1 and 2 fails with "'classtype' is valid only inside
class declarations", even though it is inside a class declaration. The
location-less "'classtype' may not be used in fields or static fields" error
accompanies it, and follow-on errors report that the override does not match an
inherited virtual or abstract method. The permitted result positions therefore
cannot be used, and the rejection of input and nested-callable positions cannot
be observed separately from the rejection of valid ones.

Known Impact:
Virtual and abstract class-relative result contracts, such as a polymorphic
`clone`, cannot be declared. The workaround is to return the declaring class
type and cast at each call site.

## BUG-311: A plain class pointer converts implicitly to `classtype*`

Date/Time: 2026-10-19 09:52 EDT

Summary:
Inside a class, a value of the enclosing class type may widen to `classtype`,
but the reverse direction is not implicit: a `classtype*` is more precise than
the enclosing class pointer, so converting the other way is an unchecked
assertion that needs an explicit cast. `this` is the one safe implicit
producer. The compiler accepts the implicit narrowing conversion in a return
statement and in a local initializer.

Steps to Reproduce:

1. Declare a class `Control` with an `int` field and the methods
   `classtype* bad(Control* item) { return item; }` and
   `void local(Control* item) { classtype* x = item; }`.
2. Add a derived class `Button: Control` and a method
   `classtype* viaBase(Control* item) { return item; }`.
3. Compile.

Expected:
Both `return item;` and `classtype* x = item;` are rejected as conversions that
cannot be made implicitly. `return (classtype*)item;` and `return this;` are
accepted.

Actual:
The program compiles without diagnostics. The same narrowing is rejected when
it happens at a call site (a `Control*` result assigned to a `Button*`), so only
the declaration-side conversions are unchecked.

Known Impact:
Class-relative contracts can be violated without a cast. The unsafe assertion
the language asks the programmer to write explicitly is silently implied.

## BUG-312: `classtype` inside a callable type is emitted unlowered in generated C

Date/Time: 2026-10-19 09:56 EDT

Summary:
`classtype` composes with ordinary type syntax and is replaced by the enclosing
class type in lowered signatures. When it appears inside a callable type
(`fn classtype*()`) in a non-virtual method parameter, result or local, the
generated C still contains the literal name `classtype`, so the native build
fails. The same `classtype` inside a pointer or array (`classtype*`,
`classtype*[]`) in the same positions lowers correctly. The semantics only
forbid callable-nested `classtype` in virtual, abstract, override and sealed
signatures, so the non-virtual use should either work or be diagnosed in
source.

Steps to Reproduce:

1. Declare `class Control { int v; void use(fn classtype*() source) { } }`.
2. In a second program declare
   `class Control { int v; fn classtype*() make() { return default; } }`.
3. In a third program declare
   `class Control { int v; void run() { fn classtype*() f = default; } }`.
4. Compile each with a native backend.

Expected:
Each program compiles, with the enclosing class substituted for `classtype` in
the lowered callable type, or the compiler reports a source diagnostic for the
unsupported position.

Actual:
The Camp front end accepts each program without diagnostics. The generated C
fails to compile with "unknown type name 'classtype'" for steps 1 and 2 and
"use of undeclared identifier 'classtype'" for step 3.

Known Impact:
Callable types over a class-relative type cannot be used. The workaround is to
name the enclosing class instead of `classtype` inside the callable type.
