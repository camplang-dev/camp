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

## BUG-060: Derived virtual class delete can call an `op_delete` helper that is declared but not emitted

### Summary

Allocating and deleting a derived virtual class with a destructor can generate
C that calls the derived concrete `op_delete` helper even though that helper is
declared but not defined.

### General Repro

1. Declare a virtual base class with at least one virtual lifecycle/member
   surface.
2. Declare a derived virtual class.
3. Allocate the derived class with `new Derived()`.
4. Build to C/native.

### Current Behavior

Reproduced on Windows/MSVC with this minimal shape:

```camp
export int main()
{
	auto value = new Derived();
	delete value;
	return 0;
}

public virtual class Base
{
	public virtual ~Base()
	{
	}
}

public virtual class Derived: Base
{
	override ~Derived()
	{
	}
}
```

MSVC reports:

```text
error C2129: static function 'void Derived_op_delete(Derived *)' declared but not defined
```

The fuller `Component`/`Control` property sample reproduces the same issue as:

```text
error C2129: static function 'void Control_op_delete(Control *)' declared but not defined
```

The previously suspected `_Control__vt` undeclared-identifier symptom did not
reproduce on the current compiler. The generated C does declare and define the
concrete vtable storage:

```c
static _Control _Control__vt;
static _Control _Control__vt = { .Component = { .op_delete = Control__op_delete } };
ctl->_vt = &_Control__vt.Component;
```

The current confirmed failure is that the virtual thunk is emitted, but the
direct concrete helper is not:

```c
static void Control_op_delete(Control *this);
void Control__op_delete(Component *ctx);

(Control_op_delete(ctl), free((void *)(ctl)));

void Control__op_delete(Component *ctx)
{
	Control *this = (Control *)(ctx);
	(void)this;
	free((void *)(this->text));
}
```

The lifecycle expansion creates the destructor delete helper with an
override-flavored modifier so it participates in virtual dispatch expansion.
Virtual dispatch lowering emits the erased-receiver thunk (`Control__op_delete`)
and clears the override body, but ordinary delete lowering can still target the
concrete helper symbol (`Control_op_delete`). That leaves a concrete helper
prototype and call-site without a matching C definition.

### Expected Behavior

When lowering allocation or construction of a derived virtual class, the
compiler must ensure that any concrete `op_delete` helper referenced by delete
call-sites is emitted with a matching C definition, or delete lowering must
target an emitted helper with the correct receiver adaptation. If that lifecycle
shape is invalid, the Camp compiler should report a source diagnostic instead
of emitting invalid C.
