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

Next bug number: BUG-221.

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

## BUG-217: An unnamed prep slot in a callable type does not parse

Date/Time: 2026-10-03 18:17 EDT

Summary:
A function, delegate or callable newtype type may list its parameters without
names, and unnamed slots with modifiers such as `out` or `const` parse. An
unnamed slot carrying the `prep` modifier does not: the parser reports a series
of syntax errors. The same slot parses when it is given a name, and the
return-position spelling `fn prep char[](int)` also parses.

Steps to Reproduce:

1. Declare `nuint render(int value, prep char[] buffer = default) { return (nuint)value; }`.
2. In a function body, write `fn nuint(int, prep char[]) indirect = render;`.
3. Compile.

Expected:
The declaration compiles. Prep is a callable contract modifier on the slot, and
the documented callable types spell it without a name, for example
`fn nuint(prep char[]) formatter;`.

Actual:
`error: Expected ')'.`, `error: Expected ';'.` and `error: Expected statement.`
at the `prep` slot.

Known Impact:
Prep-bearing callable types must name the prep slot
(`fn nuint(int, prep char[] buffer)`) or use the return-position spelling.

## BUG-218: Omitting the prep slot when calling a delegate fails to compile

Date/Time: 2026-10-03 18:17 EDT

Summary:
Calling a delegate whose type carries a prep slot, without supplying that slot,
should select the prepared-result call. Instead compilation fails with a
diagnostic that reports an argument as already supplied and gives no source
location. This happens whether the delegate targets a plain function or a
bound method. The same omission through an `fn` value or a callable newtype
works, and the delegate call works when the prep slot is supplied explicitly.

Steps to Reproduce:

1. Declare a struct `Box` with an `int size` field and a method
   `nuint render(int value, prep char[] buffer = default) { return (nuint)(value + this.size); }`.
2. In a function body, create `Box box = { 10 };` and
   `delegate nuint(int, prep char[] buffer) bound = box.render;`.
3. Write `char[] text = bound(2);` and compile.

Expected:
The call compiles and `text.length` is 12. Omitting the prep slot on a call
through any prep-bearing callable surface, including a bound delegate, selects
the transformed prepared-result call.

Actual:
`(no line,column) error: Argument 'arg1' was already supplied.`

Known Impact:
Prepared results cannot be requested through delegates. Supplying the buffer
explicitly and sizing the storage by hand works for bound-method targets.

## BUG-219: A plain function assigned to a prep-bearing delegate type produces invalid C

Date/Time: 2026-10-03 18:17 EDT

Summary:
A plain function can be assigned to a delegate type; the compiler supplies the
hidden context parameter the delegate target expects. When the delegate type
carries a prep slot, that adaptation is skipped: the function is assigned
directly to a function pointer whose first parameter is the context, and the C
compiler rejects the incompatible pointer types. The same assignment to a
delegate type without `prep` works, as does assigning a bound method to the
prep-bearing delegate type.

Steps to Reproduce:

1. Declare `nuint render(int value, prep char[] buffer = default) { return (nuint)value; }`.
2. In a function body, write
   `delegate nuint(int, prep char[] buffer) bound = render;` and
   `nuint length = bound(5, default);`.
3. Build.

Expected:
The program builds and `length` is 5.

Actual:
Native compilation fails with an `incompatible function pointer types` error:
the delegate's function pointer takes a leading `void*` context parameter and
the function does not.

Known Impact:
Plain functions cannot be used as prep-bearing delegates. Assigning to an `fn`
type or a callable newtype with the same prep slot works.

## BUG-220: A function that returns an fn value produces invalid C

Date/Time: 2026-10-03 18:28 EDT

Summary:
A function whose declared result type is an `fn` callable type compiles through
semantic analysis, but the generated C declares it with a malformed declarator:
the function's own parameter list is placed inside the pointer declarator and
the returned function type's parameter list is lost, so the C compiler reports
that a function cannot return a function type.

Steps to Reproduce:

1. Declare `int twice(int value) { return value * 2; }`.
2. Declare `fn int(int) choose() { return twice; }`.
3. Call `choose()(4)` and build.

Expected:
The program builds and the call returns 8.

Actual:
Native compilation fails: `error: function cannot return function type 'int (void)'`
at a declaration of the form `static int (* choose)(int arg0)(void);`.

Known Impact:
Functions cannot return `fn` values. Keeping the function value in an `fn`
local or passing it as an argument works.

