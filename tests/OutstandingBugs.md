# Outstanding Bugs

Next bug number: BUG-088.

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
