# Structs, Classes, And Lifecycle

## Structs

`struct` declarations define value-oriented aggregate types.

```camp
export struct Position
{
	int row;
	int column;
}
```

Struct fields are laid out in declaration order for source semantics. Methods
with receiver parameters can be called using member syntax.

Structs are the right default for plain value aggregates, fixed native layout
shapes, small protocol records, and values whose storage is owned by the caller
or by a containing value. A struct value is not implicitly heap allocated and it
does not carry hidden object identity unless a specific feature adds generated
storage for a particular use. Interface implementation is one of the places
where this distinction matters: a struct that implements an interface does not
gain hidden interface fields. Instead, conversion from a struct pointer to an
interface pointer uses scoped adapter storage, described in
[Interfaces And Dispatch](12-interfaces-and-dispatch.md).

Struct methods are ordinary functions with a receiver. A receiver written as
`this` in the parameter list determines whether the method sees a copy, pointer,
const view, or lifetime-qualified view of the value. Mutating methods normally
use a pointer receiver.

```camp
export struct Cursor
{
	nuint offset;
}

export void advance(Cursor* this, nuint amount)
{
	this.offset += amount;
}
```

Because structs are values, assignment copies the struct value according to the
rules for its fields and any generic constraints involved. Pointer-bearing
fields still participate in lifetime analysis: copying a struct that contains a
pointer copies the pointer value, not the pointed-to storage.

## Classes

`class` declarations define reference-oriented object types. Class values are
usually manipulated through pointers.

```camp
export class Buffer
{
	byte* data;
	nuint length;
}
```

Classes may participate in inheritance, virtual dispatch, interfaces, and
lifecycle rules.

Classes are the right choice when values have identity, participate in
inheritance, need virtual dispatch, own resources through constructors and
destructors, or must be stored behind interface pointers that can escape. Class
objects can have generated hidden fields for virtual dispatch and directly
declared interface implementations. Those hidden fields are part of the private
layout of the object; exported opaque class APIs do not expose them as public
layout.

Class declarations may be `abstract`, `virtual`, `sealed`, `extern`, or
ordinary concrete classes. `abstract` classes cannot be directly constructed.
`sealed` classes cannot be subclassed. `extern class` declarations describe
native objects whose layout or lifecycle is provided outside Camp.

## Fields And Static Fields

Fields store data in a struct, class, enum, or newtype scope. `static` fields
belong to the type rather than an instance.

```camp
export class Counter
{
	static int created;
	int value;
}
```

`fixed` storage is used for fixed-size storage surfaces described with arrays.

Instance fields are initialized by constructors or by default initialization.
Static fields are associated with the type and are accessed through the type
scope rather than an instance. A field declared in an exported type is part of
that type's source surface only when its visibility allows it.

Fields that can retain pointer-bearing values must satisfy the lifetime rules
for storage. A field in escaped storage cannot receive a scoped pointer unless
the assignment is proven safe by the signature or by an explicit lifetime cast.

Fixed-size array fields store their elements inline:

```camp
export struct Digest
{
	fixed byte[32] bytes;
}
```

Use fixed storage when the field's storage is part of the containing value's
layout. Use `T[]` when the field is a counted view over storage located
elsewhere.

## Methods And Receivers

Methods are functions associated with a type. A receiver parameter named `this`
determines the instance the method operates on.

```camp
export int value(const Counter* this)
{
	return this.value;
}
```

The receiver type controls mutation, dispatch, and member access.

Camp does not use a separate arrow operator for pointer receivers. Member access
uses `.` for values, pointers, interface pointers, static members, and expanded
components. The receiver type determines how the compiler binds the call.

Receiver qualifiers are part of the method contract. A method with `const this`
cannot mutate through the receiver. A method returning `constof(this) T` ties
the result's constness to the receiver's constness. This is common for accessors
that expose internal storage without losing const-correctness.

```camp
export constof(this) byte* data(const Buffer* this)
{
	return this.data;
}
```

Methods can be used as callable references. When a method reference binds a
receiver, the generated callable value must retain any context and lifetime
facts required by that receiver.

## Constructors

Constructors initialize a value. They use the type name as the callable name and
may initialize fields, base classes, or required state.

```camp
export Buffer(nuint capacity)
{
	this.length = capacity;
}
```

Default constructor behavior is generated where the language permits it.

Constructors are responsible for establishing the invariants that methods and
destructors rely on. A constructor can take ordinary parameters, `within`
parameters, generic capabilities, and thrown slots according to the same rules
as functions.

Construction has two important source forms:

- `init T(...)` constructs into source storage.
- `new T(...)` allocates storage and then constructs into it.

```camp
Buffer value = init Buffer(1024);
Buffer* owned = new Buffer(1024);
```

Constructor result lifetimes follow the storage form. `init` produces a value
whose lifetime is tied to the destination storage. `new` produces a pointer
whose lifetime and deallocation are tied to the allocation context.

When a class implements interfaces, typed construction initializes the hidden
interface vtable-pointer fields before the user constructor body runs. This
means a constructor body can rely on the object having its direct interface
dispatch fields set, but should still avoid exposing partially initialized
state.

Derived class construction must initialize base state according to the class
hierarchy. If the base is native (`extern`), Camp cannot assume ownership of the
native base lifecycle.

## Destructors

Destructors use `~TypeName` and release resources owned by an instance.

```camp
~Buffer()
{
	delete this.data;
}
```

Destructors do not return values.

A destructor should release only resources owned by the value. It should not
free borrowed storage merely because the value holds a pointer to it. Ownership
should be clear from the constructor, fields, lifetimes, and allocation context.

Destructors can participate in generated cleanup paths, including `finally`
cleanup expressions and `delete`. They should be written so they are safe when
called by the lifetime and allocation pattern the type exposes.

Interface destructors are part of an interface vtable contract, not ordinary
instance methods. See
[Interface Constructors And Destructors](12-interfaces-and-dispatch.md#interface-constructors-and-destructors).

## `init`, `new`, And `delete`

`init` constructs in existing or declaration-scope storage. `new` allocates and
constructs through the active allocation context. `delete` destroys and frees a
value when the target form owns allocated storage.

```camp
Position value = init Position(row: 1, column: 2);
Buffer* buffer = new Buffer(4096);
delete buffer;
```

Allocation context and lifetime rules are covered in
[Lifetimes, Allocation, And `within`](16-lifetimes-allocation-and-within.md).

`init` is for construction into already available storage. It does not imply
heap allocation. It is appropriate for locals, fields, inline storage, and
generic construction where the destination is supplied.

`new` is for allocating and constructing. It uses the active allocator selected
by a `within` expression, `within` statement, `within` parameter, or compiler
allocation policy.

`delete` has value and pointer forms. Deleting a pointer destroys the pointed-to
object and frees storage according to the matching allocation contract. Deleting
a value runs value cleanup without freeing separately allocated outer storage.

```camp
within (allocator)
{
	Buffer* buffer = new Buffer(4096);
	...
	delete buffer;
}
```

`finally delete` is useful when a value must be cleaned up at the end of an
expression or scope-like use:

```camp
Buffer* scratch = new Buffer(4096) finally delete;
```

The compiler checks lifetime and allocation rules around these forms. If a
value may be used after an async suspension or iterator yield, stack-like
storage and scoped values cannot be retained in generated state.

## Abstract, Virtual, Override, Sealed, And Extern Types

`virtual` and `abstract` declare dispatch points. `override` implements a
virtual member from a base type. `sealed` prevents further overriding or
inheritance where the declaration form supports it.

`extern` types describe native or externally implemented boundaries. Camp does
not allow source lifecycle rules that would make ownership of an external base
class ambiguous.

`virtual` introduces dispatch that can be customized by derived classes.
`override` provides a replacement implementation for a virtual base member. A
sealed override closes the override chain for that member. Virtual calls and
interface calls are related but not identical: virtual dispatch chooses an
implementation along the class hierarchy, while interface dispatch calls through
an interface vtable slot.

When a base class implements an interface and the behavior should be
customizable, make the implementation methods virtual and override them in
derived classes. Do not redeclare the same interface implementation in the
derived class.

`extern` functions and types are declarations for native or separately compiled
symbols. An `extern class` can participate in pointer types, method declarations,
and interop contracts, but Camp does not synthesize ownership of the native
base. If a native object needs construction or destruction, expose that through
explicit extern functions or a carefully designed wrapper type.

## Object Layout And ABI Surface

For structs, field order is the source layout order used by the compiler's
emitted representation. For classes, layout includes base-object fields and
possible hidden fields for virtual and interface dispatch. Public API docs
should distinguish source-visible fields from private generated fields.

Exported classes can be opaque to consumers even when their implementation has
hidden fields. Exported structs and interfaces expose more ABI shape: a struct's
fields and an interface's vtable contract are part of their public meaning.

When designing ABI-stable APIs:

- prefer structs for plain data records whose layout is intentionally visible;
- prefer classes for opaque identity-bearing objects;
- expose explicit constructor/destructor functions at interop boundaries;
- use interfaces when callers need a stable vtable contract;
- avoid leaking private class layout through helper functions unless that is the
  explicit ABI design.

## Common Lifecycle Patterns

Owned heap object:

```camp
export Buffer* createBuffer(nuint capacity, within allocator)
{
	return new Buffer(capacity);
}

export void destroyBuffer(Buffer* buffer)
{
	delete buffer;
}
```

Inline value:

```camp
Buffer scratch = init Buffer(4096) finally delete;
```

Borrowed view:

```camp
export struct ByteView
{
	const byte* data;
	nuint length;
}
```

The borrowed view does not own `data`; its API must not delete it.
