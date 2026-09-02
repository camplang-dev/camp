# Outstanding Bugs

Next bug number: BUG-095.

## BUG-094: Nested expanded-result calls omit the inner result length

Status: Open

When an array-returning call is used directly as the array argument of another
call, lowering does not create or forward storage for the inner call's expanded
result length. The outer call therefore receives no length corresponding to its
array input, and the inner call is itself missing its required result-length
argument.

Generalized repro:

```camp
scoped const char[] inner(const char[] text)
{
	return text;
}

scoped const char[] outer(const char[] text)
{
	return text;
}

void assign(const char[] text)
{
	const char[] value = outer(inner(text));
}

export int main()
{
	return 0;
}
```

Expected: lowering materializes the pointer and length returned by `inner`,
then passes both as the input pointer and length of `outer`.

Actual: generated C contains
`outer(inner(text, text_length), &value_length)`. The declaration for `inner`
requires a third result-length argument, and `outer` receives the outer result
length where its input length belongs. Native compilation fails.

Known impact: nested calls over borrowed array views cannot compile. Waystone's
WIRS parser uses helpers such as `[stripComment(...).trim()]` and is prevented
from compiling.

## BUG-093: Cross-file array fields omit their length when passed as arguments

Status: Open

When an array-valued struct field declared in one source file is passed to a
call in another source file, lowering emits the array pointer but omits its
expanded length argument. The same pattern lowers correctly when the struct and
caller are in one file.

Generalized repro using one build with two source files:

```camp
// holder.camp
using Std;

public struct Holder
{
	byte[] bytes;
}

public byte[] copyBytes(const byte[] input, int mode, within allocator)
{
	return default;
}
```

```camp
// caller.camp
using Std;

byte[] assignCopy(Holder holder, within allocator)
{
	byte[] output = copyBytes(holder.bytes, 1);
	return output;
}

export int main()
{
	return 0;
}
```

Expected: the generated call includes
`copyBytes(holder.bytes, holder.bytes_length, 1, allocator, &output_length)`.

Actual: the generated call omits `holder.bytes_length`, leaving four arguments
for a five-parameter generated declaration. Native compilation fails.

Known impact: array fields of public cross-file result structures cannot be
passed directly to array-accepting APIs. Waystone test modules commonly pass
`WirEmitResult.bytes` this way, preventing binder tests and potentially other
downstream suites from compiling.

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
