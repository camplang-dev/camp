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

## Target Type Specifiers

Targets may define type specifiers such as address-space or memory-model
qualifiers. A type specifier applies to the carrier it annotates.

```camp
char* _near localData;
char* _far sharedData;
```

The selected target decides which specifiers exist and which conversions are
implicit, explicit, unsafe, or invalid.

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

## `unsafe` Casts

`(unsafe T)value` marks a cast that breaks a contract the type system normally
protects while preserving enough type relationship for the compiler to model
the cast.

```camp
const Widget* source;
Widget* mutableView = (unsafe Widget*)source;
```

Unsafe casts are not a general escape hatch for unrelated values. Raw fence
carriers are used when type information is intentionally erased.

## Raw Carriers

`void*` is a raw carrier for data pointers. `fn*` is a raw carrier for function
pointers. `nint` and `nuint` can carry pointer-sized integer values where the
target allows that policy. `untyped` erases raw scalar carrier identity.

```camp
PacketHeader* header = (PacketHeader*)(void*)bytes;

fn int(int) transform;
fn* rawTransform = (fn*)transform;
fn int(int) recovered = (fn int(int))rawTransform;
```

`fn*` is not callable until it is cast back to a concrete function type.

## Conversion Limits And Reconstruction

Some operations are not casts. Changing array element type, rewriting generic
arguments, or changing delegate signature usually requires constructing a new
value. The semantic supplement contains the full classifier and target-policy
tables.
