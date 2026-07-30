# Outstanding Bugs

Next bug number: BUG-062.

## BUG-059: Stack-initialized virtual class values fail inherited member receiver compatibility

### Summary

Calling an inherited instance method on a virtual class value produced by
`init Type()` can fail receiver compatibility, even though calling methods and
properties on the same value otherwise binds.

### General Repro

1. Declare a `public virtual class Base` with an ordinary instance method.
2. Declare a `public virtual class Derived: Base`.
3. In `main`, create a value with `auto value = init Derived();`.
4. Call a method inherited from `Base` through `value`.

### Current Behavior

The compiler reports that the inherited member exists on the derived type, but
that the member's `this` parameter is not compatible with the receiver. In the
observed case, the diagnostic named a different inherited member than the one
actually being called, which also suggests poor candidate/diagnostic selection.

### Expected Behavior

Either:

- the inherited method call should bind and lower correctly for the
  stack-initialized virtual class value; or
- if stack initialization of that virtual class shape is not legal, the compiler
  should reject the `init Type()` construction directly with a clear diagnostic.

The compiler should not allow the construction and then fail inherited member
receiver binding later.

## BUG-060: Derived virtual class allocation can emit a reference to an undeclared concrete vtable symbol

### Summary

Allocating a derived virtual class can generate C that assigns the object's
hidden virtual table field from a concrete derived vtable symbol that was not
declared or emitted.

### General Repro

1. Declare a virtual base class with at least one virtual lifecycle/member
   surface.
2. Declare a derived virtual class.
3. Allocate the derived class with `new Derived()`.
4. Build to C/native.

### Current Behavior

The C emitter can produce an assignment shaped like:

```c
instance->_vt = &_Derived__vt.Base;
```

but `_Derived__vt` is not declared in the generated C/private header, causing
the native compiler to fail with an undeclared identifier error.

### Expected Behavior

When lowering allocation or construction of a derived virtual class, the
compiler must ensure that the concrete derived virtual table storage is
generated, declared, and initialized before any emitted C references it. If the
class shape is invalid, the Camp compiler should report a source diagnostic
instead of emitting invalid C.

## BUG-061: `delete this` is allowed for non-escaped class receivers

### Summary

The compiler currently allows a class instance method with a non-escaped
receiver to execute `delete this`. This is unsafe because the receiver may refer
to storage that was not allocated with `new`, such as a stack-initialized class
value.

### General Repro

1. Declare a non-escaped class.
2. Add an instance method with the ordinary implicit receiver, or an explicit
   non-escaped `this` receiver.
3. Put `delete this;` in that method body.
4. Compile the program.

### Current Behavior

The compiler accepts the method body.

### Expected Behavior

The compiler should reject `delete this` unless the receiver is known to be
escaped:

- the method declares an explicit `escaped this` receiver;
- the containing class is declared `escaped`; or
- the containing class is implicitly escaped, such as a `shadow` class.

The diagnostic should explain that deleting `this` requires an escaped receiver
because a non-escaped receiver may refer to stack or otherwise non-owned
storage. This restriction does not prove that the allocator is correct, but it
blocks the obvious invalid cases.
