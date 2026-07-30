# Outstanding Bugs

Next bug number: BUG-062.

## BUG-059: Stack-initialized virtual class values fail inherited member receiver compatibility

### Summary

Calling an inherited instance method on a virtual class value produced by
`init Type()` fails receiver compatibility. The inherited member is found on
the derived type, but the receiver checker rejects the stack-initialized derived
value.

### General Repro

1. Declare a `public virtual class Base` with an ordinary instance method.
2. Declare a `public virtual class Derived: Base`.
3. In `main`, create a value with `auto value = init Derived();`.
4. Call a method inherited from `Base` through `value`.

### Current Behavior

Reproduced on Windows/MSVC and macOS with this minimal shape:

```camp
export int main()
{
	auto value = init Derived();
	value.touch();
	return 0;
}

public virtual class Base
{
	uint count;

	public void touch()
	{
		this.count++;
	}
}

public virtual class Derived: Base
{
}
```

The compiler reports:

```text
error: Member 'touch' exists on type 'Derived', but its this parameter is not compatible with that receiver.
```

The relevant semantic path builds the inherited method's effective receiver from
the base method owner. For a stack-value derived receiver, that produces an
incompatible receiver shape instead of a deliberate rule for whether
stack-initialized virtual class values can call inherited class methods.

### Expected Behavior

Either:

- the inherited method call should bind and lower correctly for the
  stack-initialized virtual class value; or
- if stack initialization of that virtual class shape is not legal, the compiler
  should reject the `init Type()` construction directly with a clear diagnostic.

The compiler should not allow the construction and then fail inherited member
receiver binding later.
