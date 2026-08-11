# Outstanding Bugs

Next bug number: BUG-071.

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
