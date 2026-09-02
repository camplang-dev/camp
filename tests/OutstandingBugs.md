# Outstanding Bugs

Next bug number: BUG-091.

## BUG-090: An ordinary `null` argument is consumed as the implicit `within` argument

Status: Open

When a call supplies `null` for an ordinary pointer parameter and the selected
callable also has a later `within` parameter, call lowering consumes the `null`
as the hidden within argument. The ordinary pointer argument disappears and
the remaining arguments shift left.

Generalized repro:

```camp
requires (TEST_MODULE);

using Std;

void consumeOptional(int* optional, int value, within allocator)
{
}

@test void nullOrdinaryArgumentPrecedesImplicitWithin(
	within Allocator* allocator)
{
	consumeOptional(null, 7);
}
```

Expected: generated C calls `consumeOptional(NULL, 7, allocator)`.

Actual: generated C calls `consumeOptional(7, NULL)`. Native compilation then
fails because the declaration requires three arguments. More complex signature
shapes may instead produce type confusion or runtime corruption.

Known impact: allocator-aware callables cannot safely receive literal `null`
for an ordinary pointer parameter. This blocks canonical implicit within
propagation in APIs with optional pointer arguments.

## BUG-089: Implicit `within` forwarding is emitted before an explicit `out` argument

Status: Open

When a callable declares an ordinary `out` parameter before a `within`
parameter, an implicit within context is bound to the correct source call but
lowered into the wrong ABI position. The generated call passes the allocator
where the `out` pointer belongs and passes the `out` pointer where the allocator
belongs.

Generalized repro:

```camp
requires (TEST_MODULE);

using Std;

int produceValue(out int output, within allocator)
{
	output = 7;
	return 9;
}

@test void withinAfterOutUsesDeclaredAbiOrder(
	within Allocator* allocator,
	thrown Assertion*)
{
	int output;
	int value = produceValue(out output);
	assert(value == 9);
	assert(output == 7);
}
```

Expected: generated C calls `produceValue(&output, allocator)`, matching the
generated declaration and the source parameter order.

Actual: generated C calls `produceValue(allocator, (StdAllocator ***)&output)`.
The first assertion can pass because the return value is unaffected, but the
`out` value is not written correctly. With nontrivial allocator use this can
corrupt memory or crash.

Known impact: allocator-aware helpers that combine `out` results with implicit
within forwarding cannot be called safely. This affects canonical allocator
propagation in library and test code; callers must not rely on the alpha
compiler's generated output for this signature shape.

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
