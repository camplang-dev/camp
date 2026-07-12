# Pointers, Qualifiers, And Conversions

## Pointer Types

`T*` is a pointer to `T`.

```camp
int value = 5;
int* address = &value;
int copy = *address;
```

Pointer values may be `null`. Target type specifiers can describe pointer
representation domains when the selected target defines them.

Pointers are ordinary values. Copying a pointer copies the address, not the
storage it points at. Ownership is not inferred from pointer type alone. A
pointer may be borrowed, retained, allocated, native-owned, or an interface
slot pointer depending on the API contract.

Camp uses `.` for member access even when the receiver is a pointer:

```camp
Buffer* buffer = new Buffer(1024);
nuint count = buffer.length;
buffer.clear();
```

The compiler chooses the matching receiver form from the available members.
There is no separate `->` operator.

Pointer depth is significant. `Document*` and `Document**` are different physical
indirection shapes. A cast that changes physical depth is not an ordinary
conversion; it requires at least an unsafe operation, and often a raw carrier is
the clearer signal.

## `const`, `volatile`, And `constof`

`const T` prevents mutation through that view. `volatile T` marks access as
target-sensitive. `constof(anchor) T` makes constness depend on the constness of
another parameter or receiver.

```camp
const char[] readOnlyText;

export constof(this) char* data(const Buffer* this);
```

`constof` is most useful when APIs preserve a caller's constness across returns
or output positions.

`const` is a property of the view. It does not necessarily say the underlying
storage is globally immutable; it says this expression cannot mutate through
that view. Removing `const` is unsafe because it lets code write through a view
that promised not to write.

`constof(anchor)` is dependent constness. It is not lifetime-derived constness.
The anchor must be visible in the signature, and call-site substitution decides
whether the result or output is const.

```camp
export constof(this) byte* data(constof(this) Buffer* this);

Buffer* mutableBuffer = ...;
byte* mutableData = mutableBuffer.data();

const Buffer* readOnlyBuffer = ...;
const byte* readOnlyData = readOnlyBuffer.data();
```

This lets one declaration express both mutable and read-only access without
duplicating APIs.

A `constof` anchor must be a receiver or a non-output parameter with exactly
one ordinary `const` slot. It cannot be an `out`, `thrown`, or `within`
parameter, and it cannot itself be dependent on another `constof(...)`.

```camp
constof(source) byte* first(const byte[] source);          // OK
constof(source) byte* mutableAnchor(byte[] source);        // ERROR
constof(result) byte* outputAnchor(out const byte[] result); // ERROR
constof(error) byte* thrownAnchor(thrown const byte[] error); // ERROR
constof(source) byte* dependentAnchor(
	const byte[] other,
	constof(other) byte[] source);                         // ERROR
```

Inside the callee, a `constof(...)` slot is checked as an ordinary `const` view.
It does not grant mutation rights. Its purpose is caller-visible: it lets the
caller's mutable-or-const choice flow to returns and output positions.

```camp
constof(source) byte* first(const byte[] source)
{
	return (constof(source) byte*)source.elements;
}

byte[] mutableBytes = ...;
const byte[] readOnlyBytes = ...;

byte* mutableFirst = first(mutableBytes);
const byte* readOnlyFirst = first(readOnlyBytes);
```

When `constof(anchor)` appears in an input position, the call must preserve the
same constness relation after substituting the anchor:

```camp
void compareBytes(const byte[] source, constof(source) byte[] other);

byte[] mutableLeft = ...;
byte[] mutableRight = ...;
const byte[] readOnlyLeft = ...;
const byte[] readOnlyRight = ...;

compareBytes(mutableLeft, mutableRight);     // OK
compareBytes(readOnlyLeft, readOnlyRight);   // OK
compareBytes(readOnlyLeft, mutableRight);    // OK: mutable can become const
compareBytes(mutableLeft, readOnlyRight);    // ERROR
```

Storage whose type contains `constof(anchor)` carries that anchor promise. A
value assigned to it must be derived from the same anchor, already have the
same dependent-const relationship, or cross an explicit `constof(anchor)` cast
where the program's proof lives. An unrelated `const` pointer is not enough to
reconstruct the dependency.

Callable compatibility treats dependent constness by position. Outputs are
covariant: an implementation may return a more precise `constof(anchor)` result
where the target only promises ordinary `const`. Inputs are contravariant: an
implementation may accept ordinary `const` where the target requires
`constof(anchor)`. Virtual overrides are stricter and must match exactly.

```camp
newtype fn const byte* ConstGetter(const byte[] source);

constof(source) byte* getInterior(const byte[] source): ConstGetter
{
	return (constof(source) byte*)source.elements;
}
```

Lambdas follow the same rule. In an explicit lambda signature, `constof`
anchors name lambda parameters, not variables from the surrounding scope.
Target-typed lambdas use the target callable's parameter mapping.

## Target Type Specifiers

Targets may define type specifiers such as address-space or memory-model
qualifiers. A type specifier applies to the carrier it annotates.

```camp
char* _near localData;
char* _far sharedData;
```

The selected target decides which specifiers exist and which conversions are
implicit, explicit, unsafe, or invalid.

A target specifier applies to a carrier, not to every type nested inside it.
This is particularly important for arrays:

```camp
(int* _near)[] nearPointers;
int[] _near nearArrayCarrier;
```

The first type is a default-domain array whose elements are near pointers. The
second type is a near-domain array carrier whose `.elements` and `.length`
components are in the target-defined near domain. Do not rely on prefix-style
readability to infer the wrong carrier.

Call specifiers are different from type specifiers. A call specifier describes
how a concrete callable is invoked. A data pointer, `void*`, `nint`, `nuint`,
or `untyped` cannot carry a call specifier.

## Implicit And Explicit Conversions

An implicit conversion needs no cast. An explicit conversion uses `(T)value`.
The compiler accepts explicit casts only where the source and target remain
related enough for a known conversion.

```camp
long widened = 42;
int narrowed = (int)widened;
```

Conversions happen at value boundaries. They do not automatically rewrite array
element types, generic arguments, delegate signatures, or function signatures.
When a shape changes, construct a new value.

The useful conversion categories are:

| Category | Surface form | Meaning |
|---|---|---|
| Implicit | `T target = value;` | The conversion is accepted without cast syntax. |
| Explicit | `(T)value` | The conversion is known, but source must acknowledge it. |
| Unsafe | `(unsafe T)value` | The conversion breaks a protected type-system contract. |
| Fence | `(T)(void*)value` or similar | Source deliberately erases type information before recovery. |
| Reconstruct | Build a new value | The source and target shapes are different values, not casts. |

Implicit conversions are for value-preserving or target-defined safe movements.
Explicit conversions are for known conversions that should be visible in
source. Unsafe conversions are for direct related casts that remove guarantees,
such as removing constness or changing a protected pointer relationship.

The compiler treats conversions as value-boundary operations. This rule avoids
surprising hidden rewrites:

```camp
byte* _near local;
byte* _far shared = local;        // OK if target defines near-to-far implicit.

byte* _near[] localPointers;
// byte* _far[] sharedPointers = localPointers; // Not an element rewrite.
```

To change an array element type, map or rebuild the array. To change a delegate
or function signature, write an adapter. To change a generic argument, build a
new constructed value or use an explicit raw boundary when that is truly the
native operation.

## `unsafe` Casts

`(unsafe T)value` marks a cast that breaks a contract the type system normally
protects while preserving enough type relationship for the compiler to model
the cast.

```camp
const Document* source;
Document* mutableView = (unsafe Document*)source;
```

Unsafe casts are not a general escape hatch for unrelated values. Raw fence
carriers are used when type information is intentionally erased.

Use `unsafe` when the source and target are still directly related enough for
the compiler to model the operation, but the cast violates a safety contract.
Common examples are:

- removing `const` or `volatile`;
- narrowing a target-defined pointer domain where the target marks it unsafe;
- changing class/interface relationships outside normal safe conversion;
- changing physical indirection depth.

`unsafe` keeps the relationship visible to the compiler. A raw fence says the
relationship is intentionally erased first.

## Raw Carriers

`void*` is a raw carrier for data pointers. `fn*` is a raw carrier for function
pointers. `nint` and `nuint` can carry pointer-sized integer values where the
target allows that policy. `untyped` erases raw scalar carrier identity.

```camp
byte* packetBytes = ...;
PacketHeader* header = (PacketHeader*)(void*)packetBytes;

fn int(int) transform;
fn* rawTransform = (fn*)transform;
fn int(int) recovered = (fn int(int))rawTransform;
```

`fn*` is not callable until it is cast back to a concrete function type.

A `void*` fence is for data pointers. It erases pointee type and data-pointer
family while remaining a data-pointer carrier. It preserves physical depth:

```camp
Document** documents;
void** rawDocuments = (void**)documents;
ArchivedDocument** recovered = (ArchivedDocument**)rawDocuments;
```

Using `void*` for a two-level pointer loses depth information and is not the
same thing as a matching-depth fence.

`fn*` is for function pointers. It erases return type, parameter types,
callable newtype identity, call spec, and callable lifetime annotations.
Recovering from `fn*` to a concrete function type is explicit and should be
used only when the native ABI contract really guarantees the target signature.

`nint` and `nuint` are natural integer carriers. Targets may define when pointer
values can move through them. Treat pointer-integer conversions as native
interop operations, not as ordinary application-level casts.

`untyped` is the strongest raw scalar fence. Once a value passes through
`untyped`, the compiler treats its carrier identity as erased. Prefer narrower
raw carriers when the native operation is specifically a data pointer or
function pointer operation.

## Conversion Limits And Reconstruction

Some operations are not casts. Changing array element type, rewriting generic
arguments, or changing delegate signature usually requires constructing a new
value. The semantic supplement contains the full classifier and target-policy
tables.

Examples that require reconstruction:

```camp
// Rebuild with converted elements.
export byte* _far[] widenPointers(byte* _near[] source);

// Write an adapter rather than casting one callback signature to another.
export delegate void(byte* _far data) adapt(delegate void(byte* _near data) source);

// Construct a new container rather than rewriting its generic argument.
export Box<byte* _far> widenBox(Box<byte* _near> source);
```

## Class, Interface, And Newtype Boundaries

Class pointer upcasts and interface conversions follow the declared inheritance
and implementation graph. A derived class pointer can convert to a base class
pointer where the class hierarchy permits it. A class pointer can convert to an
implemented interface pointer through the generated interface accessor. A
struct pointer can convert to an interface pointer only through scoped adapter
rules.

Newtypes are nominal. A value newtype does not become its underlying primitive
automatically merely because the storage is similar. Use an explicit
construction or cast that the language permits. Callable newtypes likewise keep
their nominal boundary around a callable shape.

## Arrays, Optionals, And Expanded Forms

Arrays, delegates, iterators, async callables, and thrown slots are expanded
forms. A cast applies to the source value as a whole; it does not silently cast
the individual lowered components. This is why signature rewrites and array
element rewrites usually require reconstruction.

Optionals preserve their payload contract. A conversion between optional values
is valid only when the optional shape and payload conversion rules allow it.
Do not use `untyped` to hide an optional payload mismatch unless the operation
is truly a raw ABI boundary and the recovery path is deliberately unchecked.

## Diagnostics To Expect

Conversion diagnostics should tell you which level is required:

- add an explicit cast;
- write `(unsafe T)` because the direct conversion removes a guarantee;
- pass through a raw carrier because the source and target are not directly
  related;
- reconstruct because the operation changes a multivalue shape;
- change the API because the conversion is forbidden.

When a target type specifier is involved, the diagnostic is target-dependent.
The same source may be valid for one selected target and invalid for another if
their pointer domains or call specs differ.
