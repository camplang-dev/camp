# Outstanding Bugs

Next bug number: BUG-071.

## BUG-068: Virtual methods with array parameters can produce duplicate hidden-length arguments

Status: open

Observed while implementing a terminal base class with an internal virtual method
that accepted `const char[]`. Source-only package builds failed during lowering
with a no-line diagnostic similar to:

```text
Argument 'text_length' was already supplied.
```

General repro shape:

```camp
abstract class Base
{
	virtual void writeRaw(const char[] text) { }
}

sealed class Derived: Base
{
	override void writeRaw(const char[] text) { }
}

void use(Base* target, char[] buffer, nuint length)
{
	target.writeRaw(buffer[..length]);
}
```

Expected behavior: array hidden ABI components should be supplied exactly once
for virtual dispatch, including calls with slices.

Workaround used: make the internal virtual method accept explicit pointer and
length parameters instead of an array parameter.

## BUG-069: `catch _` with enum thrown slots can leave unresolved argument types

Status: open

Observed while testing methods declared with an enum thrown slot. The package
compiled, but test execution aborted during C emission with:

```text
C emission aborted because ArgumentExpression ... has unresolved type '#ERROR'.
```

General repro shape:

```camp
enum ErrorCode: byte
{
	OK,
	FAILED,
}

bool canFail(thrown ErrorCode error)
{
	return true;
}

void caller()
{
	_ = canFail(catch _);
}
```

Expected behavior: `catch _` should create typed discard storage for the thrown
slot, as it does for other catch/out discard uses, or produce a normal
diagnostic if the form is invalid.

Workaround used: declare a typed local error value and write `catch error`.

## BUG-070: Virtual methods with `escaped this` receivers emit inconsistent C receiver shapes

Status: open

Observed while testing an escaped abstract base class with a virtual method whose
receiver was explicitly declared `escaped this`. Camp analysis accepted the
program, but the generated C failed to compile because the virtual table slot was
emitted with a by-value receiver while dispatch and method implementations still
used pointer-shaped receivers.

General repro shape:

```camp
escaped abstract class Base
{
	public virtual void destroy(escaped this)
	{
	}
}

escaped sealed class Derived: Base
{
	override void destroy(escaped this)
	{
	}
}

export int main(string[] args)
{
	Derived* value = new Derived();
	value.destroy();
	return 0;
}
```

Smoke-tested repro result:

```text
error: incompatible function pointer types initializing 'void (*)(Base)' with an expression of type 'void (Base *)'
error: passing 'Base *' to parameter of incompatible type 'Base'
error: redefinition of 'this'
error: use of undeclared identifier 'ctx'
```

Expected behavior: if `escaped this` is legal on virtual methods, the virtual
table slot type, virtual dispatch thunk, concrete implementation signature, and
override receiver rewrite should all use the same lowered receiver shape. If this
receiver form is not legal for virtual methods, the compiler should reject it
before C emission.
