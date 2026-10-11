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

Next bug number: BUG-289.

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

## BUG-286: Interface call with a written `void<V>` result passes the result storage in the wrong position

Date/Time: 2026-10-11 19:18 EDT

Summary:
A function or method may declare a `void<V>` result, which returns through caller-owned storage placed after the authored parameters. When such a method is called through an interface, the generated C passes the result storage before the authored arguments instead of after them, so the call does not compile.

Steps to Reproduce:

1. Declare an interface with a method that has a written `void<int>` result and one `int` parameter.
2. Implement the interface on a struct, with the method returning an `int` value through the `void<int>` result.
3. Take a pointer to the struct as the interface type and call the method through the interface, for example `source.next(5)`.
4. Build and run the program.

```camp
interface Source { void<int> next(int step); }
struct Counter: Source
{
	int value;
	void<int> next(int step): Source { return this.value + step; }
}
export int main()
{
	Counter counter = { 10 };
	Source* source = &counter;
	int result = source.next(5);
	return 0;
}
```

Expected:
The call compiles and reads the `int` result, as a direct call to the method does. Proposal 025 section 3 places the result storage after the authored parameters.

Actual:
The C compiler rejects the generated call: `(*source)->next(source, &_value1, 5);` passes an `int *` where the `int step` parameter is expected (`incompatible pointer to integer conversion`).

Known Impact:
Any interface method with a written `void<V>` result cannot be called through the interface. Direct calls and generic dispatch to non-interface callables are unaffected. No workaround other than calling the concrete type or returning an ordinary value.

## BUG-287: Distinct callable newtypes with identical shapes convert implicitly

Date/Time: 2026-10-11 19:18 EDT

Summary:
Callable newtypes are nominal: two newtypes with the same calling shape remain different types, and a value of one needs an ascription, cast or source rule to become the other. The compiler accepts an implicit conversion between two distinct callable newtypes that have equal shapes, and also accepts a raw callable value converted to a callable newtype.

Steps to Reproduce:

1. Declare `newtype fn int First(int value);` and `newtype fn int Second(int value);`.
2. Declare `int read(int value): First => value;`.
3. In a function body, write `First first = read;` and then `Second second = first;`.
4. Build the program.

Expected:
`Second second = first;` is rejected with an invalid implicit conversion, because the two newtypes are nominally distinct (see the callable newtype nominal boundary in the conversion semantics: a named newtype requires an ascription, cast or source rule to cross the boundary).

Actual:
The program compiles with no diagnostic. A callable newtype instantiated from a generic callable newtype (`Equals<int>` to `Matches<int>`) and a raw `fn` value assigned to a callable newtype are accepted as well. Conversions between different instantiations of one generic callable newtype (`Equals<int>` to `Equals<long>`) are rejected.

Known Impact:
A callback intended for one role can be passed where another role is required without any diagnostic. A golden that expects the rejection fails against this compiler.

## BUG-288: Unconstrained generic parameter in a callable result emits mismatched C types

Date/Time: 2026-10-11 19:18 EDT

Summary:
A generic parameter with no constraint carries its values as `nint`, so a callable type formed from it uses the carrier `nint<A>` for the argument `A`. The generic body and the call site disagree about the C representation: the body is emitted with `void *` while the callable passed in is emitted with `intptr_t`.

Steps to Reproduce:

1. Declare `enum Color: int { RED = 10, GREEN = 20 }`.
2. Declare `T make<T>(fn T() source) => source();` with no constraint on `T`.
3. Declare `nint<Color> green() { return Color.GREEN; }`.
4. In `main`, call `Color color = make<Color>(green);`.
5. Build and run the program.

Expected:
The program compiles and `make<Color>(green)` produces `Color.GREEN`; an unconstrained parameter is the `nint` carrier in a callable type.

Actual:
The C compiler rejects the generated body: `return source();` converts `intptr_t` to the function's `void *` result type (`incompatible integer to pointer conversion`). The same mismatch occurs for an unconstrained parameter of a callable parameter, for example `int apply<T>(fn int(T) read, T value)`.

Known Impact:
Callable types over an unconstrained generic parameter cannot be used. Declaring the parameter `T: nint` (or another representation constraint) avoids the problem.
