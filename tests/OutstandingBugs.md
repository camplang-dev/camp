# Outstanding Bugs

Next bug number: BUG-092.

## BUG-091: Implicit `within` is emitted before explicit arguments for an array-returning call

Status: Open

When an array-returning callable has an ordinary parameter followed by a
`within` parameter, forwarding its result from another allocator-aware callable
lowers the implicit allocator before the ordinary argument. The generated call
does not match the generated declaration.

Generalized repro:

```camp
requires (TEST_MODULE);

using Std;

char[] copyOne(int value, within allocator)
{
	char[] buffer = new char[1];
	buffer[0] = (char)value;
	return buffer;
}

char[] forwardCopy(int value, within allocator)
{
	return copyOne(value);
}

@test void implicitWithinFollowsExplicitArgumentForArrayResult(
	within Allocator* allocator,
	thrown Assertion*)
{
	char[] result = forwardCopy(65) finally delete;
	assert(result.length == 1);
	assert(result[0] == 'A');
}
```

Expected: generated C calls `copyOne(value, allocator, result_length)`.

Actual: generated C calls `copyOne(allocator, value, result_length)`. Native
compilation fails because the first two arguments have incompatible types.

Known impact: canonical implicit allocation propagation cannot be used when a
pointer-bearing array result is forwarded with explicit arguments. This blocks
the Waystone WIRS disassembler and similar allocator-aware forwarding paths.

## ~~BUG-088: API emission suppresses valid source-authored `destroy` methods~~

Status: Fixed

The API header emitter suppresses methods named `destroy` as generated lifecycle
implementation details even when the method was explicitly declared by source
code.

The compiler already rejects declaring a `destroy()` method on a type with an
explicit destructor. The remaining bug is API emission only: if a source-authored
`destroy()` method is valid in the declaring module, it should also be emitted
when it is part of the public API surface.

Generalized repro:

```camp
// library.camp
namespace Repro;

public escaped class Resource
{
	public void destroy()
	{
	}
}
```

Build the source as an API/static/shared package and inspect the generated API
header.

Expected: the API header contains `public extern void destroy();` because the
method is source-authored and was accepted by declaration analysis.

Actual: the generated API header omits the method, so consuming modules cannot
call it.

Known impact: packages cannot expose a valid public method named `destroy`
because API emission currently treats the name itself as sufficient evidence
that the method is a generated lifecycle helper.

## ~~BUG-087: Override signature lookup does not recognize same-namespace types from another source file~~

Status: Fixed

An override method declared in one source file can fail to resolve an unqualified
type declared in another source file, even when both files are in the same
namespace. The diagnostic incorrectly says the type is declared in that namespace
but is not imported by the file.

This appears to be specific to override signature analysis. A top-level function
or ordinary member method in the same namespace can resolve the same type
correctly.

Generalized repro:

```camp
// a.camp
namespace A::B;

export virtual class Base
{
	export virtual void f(Thing* value)
	{
	}
}

export struct Thing
{
}
```

```camp
// b.camp
namespace A::B;

export sealed class Derived: Base
{
	export override void f(Thing* value)
	{
	}
}
```

Expected: `Thing` is visible because `b.camp` is in namespace `A::B`, the same
namespace where `Thing` is declared.

Actual: the compiler reports that `Thing` is declared in namespace `A::B` but is
not imported by the file.

Known impact: projects that split a virtual base type and derived overrides
across source files can require unnecessary self-imports or fail with misleading
namespace diagnostics.
