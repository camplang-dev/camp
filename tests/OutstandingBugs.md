# Outstanding Bugs

Next bug number: BUG-089.

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
