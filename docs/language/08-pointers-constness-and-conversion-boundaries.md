# Pointers, Constness, And Conversion Boundaries

Camp pointers are deliberately plain. A pointer is an address; it is not an
ownership model, not a lifetime proof, and not a promise that the target can be
mutated. You have already learned how lifetimes describe where pointer-bearing
values may go. This chapter looks at the other half of the story: what kind of
address you have, what view of the pointee it gives you, and what a conversion
is allowed to change.

The practical rule is: if a conversion changes meaning, make the change visible
in source. Camp accepts ordinary conversions where the relationship is clear,
requires casts where the programmer needs to acknowledge the change, and uses
`unsafe` or raw carriers when the program crosses a boundary the type system
cannot protect.

## Pointer Values

`T*` is a pointer to `T`.

```camp
int value = 5;
int* address = &value;
int copy = *address;
```

Copying a pointer copies the address. It does not copy the pointee, transfer
ownership, or make the storage live longer. A pointer can be null:

```camp
byte* data = null;
byte* defaultData = default;
```

As you saw in the object chapter, Camp uses `.` for member access through both
values and pointers:

```camp
Buffer* buffer = new Buffer(4096);
buffer.clear();
nuint length = buffer.getLength();
```

There is no separate `->` operator. The receiver type and the member
declaration decide what call or access is valid.

## Pointer Depth

Pointer depth is physical shape. `Document*` and `Document**` are different
shapes, not just different spellings.

```camp
Document* document;
Document** documentSlot;
```

Changing depth means changing how many times a program must dereference to find
the object. That is a serious reinterpretation, so Camp does not treat it as an
ordinary conversion.

```camp
byte* oneLevel = default;
byte** twoLevels = (unsafe byte**)oneLevel;
```

That cast is marked `unsafe` because the source and target are still pointer
types, but the promise being made is outside normal type safety. Most code
should avoid casts that change pointer depth. If a native API genuinely works
with pointer-to-pointer storage, model that storage directly in the API.

## `const` And `volatile`

`const T` gives a read-only view of `T`.

```camp
byte[] writable;
const byte[] readonly = writable;
```

Adding constness is safe because it removes mutation rights through that view.
Removing constness is not safe:

```camp
const byte* readonlyBytes;
byte* writableBytes = (unsafe byte*)readonlyBytes;
```

`const` is a property of the view, not proof that the underlying storage is
physically immutable. A caller may have a mutable view elsewhere. The point is
that this view cannot be used to mutate.

`volatile` is for target-sensitive access where reads and writes must remain
observable to the target environment. Like `const`, it is part of the type's
view of the storage. Removing it is an unsafe operation because it can discard
an access guarantee the target may require.

## `constof(anchor)`

Sometimes an API should preserve the caller's constness. If the caller passes a
mutable buffer, the result should be mutable. If the caller passes a read-only
buffer, the result should be read-only. `constof(anchor)` expresses that in one
signature.

```camp
constof(source) int* firstInt(const int[] source)
{
	return source.elements;
}

int[] values = [1, 2, 3];
const int[] readonlyValues = values;

int* mutableFirst = firstInt(values);
const int* readonlyFirst = firstInt(readonlyValues);
```

The parameter is written as `const int[]` because the function body can only
read through it. The return type is `constof(source) int*` because the caller's
view decides the result view.

Receivers can be anchors too:

```camp
constof(this) char* firstChar(const char[] this)
{
	return this.elements;
}
```

When called as `mutableText.firstChar()`, the result is mutable. When called as
`readonlyText.firstChar()`, the result is const.

`constof` is about constness, not lifetime. A `constof(source)` result still
needs a valid lifetime relationship. If the result points into `source`, the
signature should also say how long that result is valid when the lifetime is
not otherwise obvious.

## Dependent Constness In Parameters

`constof` can also relate parameters:

```camp
void compareBytes(const byte[] source, constof(source) byte[] other)
{
}
```

The second parameter follows the first parameter's constness. If `source` is
mutable, `other` must be mutable too. If `source` is const, `other` must be
const too. The function body sees both through the const views declared in the
signature, but the call still preserves the relationship between the two
arguments.

```camp
byte[] mutableLeft;
byte[] mutableRight;
const byte[] readonlyLeft;
const byte[] readonlyRight;

compareBytes(mutableLeft, mutableRight);    // OK
compareBytes(readonlyLeft, readonlyRight);  // OK
compareBytes(readonlyLeft, mutableRight);   // ERROR
compareBytes(mutableLeft, readonlyRight);   // ERROR
```

This is the point where `constof` earns its keep. It lets you write APIs that
preserve a caller's mutability without splitting every operation into separate
mutable and read-only overloads.

## Target Type Specifiers

Some targets have more than one pointer or integer domain. A retro target might
distinguish near and far pointers. An embedded target might distinguish normal
RAM from a special address space. Camp lets targets define type specifiers for
those domains.

```camp
byte* _near localBytes;
byte* _far sharedBytes;
fn* _near localFunction;
nint _huge largeIndex;
```

The selected target decides which specifiers exist and which conversions are
implicit, explicit, unsafe, or forbidden. On a target with no such domains,
these names may not exist at all.

A type specifier applies to the carrier it annotates:

```camp
(byte* _near)[] nearPointers;
byte[] _near nearArray;
```

The first declaration is an ordinary array whose elements are near pointers.
The second is a near-domain array carrier. The distinction matters at ABI
boundaries because the lowered components are not the same promise.

Call specifiers are separate. They describe how a concrete callable is invoked:

```camp
fn _near _pascal nint() callback;
fn* _near rawCallback;
```

The concrete `fn` has a call specifier. The raw `fn*` has only a function
pointer carrier domain; it has no callable signature yet.

## Conversion Levels

Camp classifies conversions by the source action they require:

| Level | Source Form | Meaning |
|---|---|---|
| implicit | `Target value = source;` | The movement is accepted without cast syntax. |
| explicit | `(Target)source` | The conversion is known, but source must acknowledge it. |
| unsafe | `(unsafe Target)source` | The conversion breaks a protected guarantee. |
| raw fence | cast through `void*`, `fn*`, `nint`, `nuint`, or `untyped` | Type information is deliberately erased before recovery. |
| reconstruct | build a new value | The target is a different shape, not a cast. |
| forbidden | none | The conversion is not valid for this source and target. |

The categories are about source promises. An explicit cast says "I know this
conversion changes the view." An unsafe cast says "I know this removes a
guarantee." A raw fence says "I am crossing an ABI boundary where the precise
source type is intentionally erased."

## Ordinary Explicit Casts

Use an ordinary cast when the conversion is known and related, but should be
visible:

```camp
long wide = 42;
int narrow = (int)wide;

PacketHeader* packet = default;
OtherHeader* other = (OtherHeader*)packet;
```

That second example is still something to treat carefully: it changes the
pointee type. Camp accepts same-family explicit pointer casts where the
relationship is modelled, but the cast should be a sign to the reader that the
program is interpreting the address differently.

Ordinary casts should not be used as a design shortcut. If the target value is
really a different aggregate, callback shape, array element type, or generic
constructed type, build a new value instead.

## `unsafe` Casts

Use `(unsafe T)` when the source and target are directly related enough for the
compiler to model the operation, but the operation removes a guarantee.

```camp
const byte* readonlyBytes;
byte* writableBytes = (unsafe byte*)readonlyBytes;

Base* base;
Derived* derived = (unsafe Derived*)base;
```

Common unsafe casts include:

- removing `const` or `volatile`;
- changing pointer depth;
- narrowing target-defined pointer or integer domains;
- downcasting class pointers where the hierarchy relationship exists;
- changing callable lifetime or signature guarantees without using a raw
  function fence.

`unsafe` is not a universal escape hatch. If the source and target are
unrelated, use a raw carrier fence only when that is genuinely the ABI operation
you are modelling.

## Raw Data Carriers

`void*` is the raw carrier for data pointers. It erases the pointee type while
remaining a data pointer carrier.

```camp
byte* bytes;
void* rawData = (void*)bytes;
PacketHeader* header = (PacketHeader*)rawData;
```

`void*` preserves pointer depth. If you start with a two-level pointer, use a
matching two-level raw carrier:

```camp
Document** documents;
void** rawDocuments = (void**)documents;
ArchivedDocument** archived = (ArchivedDocument**)rawDocuments;
```

Do not use a one-level `void*` as a casual bucket for every pointer-shaped
thing. It is specifically a data-pointer fence.

`nint` and `nuint` are natural integer carriers:

```camp
byte* bytes;
nuint bits = (nuint)bytes;
byte* again = (byte*)bits;
```

Pointer-to-integer conversions are native interop operations. They discard
pointer type information, qualifiers, and lifetime facts. Use them when the
native API really speaks in pointer-sized integers, not as a general-purpose
cast style.

## Raw Function Carriers

`fn*` is the raw carrier for function pointers:

```camp
fn int(int) transform;
fn* rawTransform = (fn*)transform;
fn int(int) recovered = (fn int(int))rawTransform;
```

You already saw that `fn*` is not callable. Now you can see why: it has erased
the return type, parameter types, callable newtype identity, call specifier, and
callable lifetime annotations. The recovered concrete type is a new source
promise that the raw function pointer really has that ABI shape.

Delegates, `once` values, async callables, and iterators are not just function
pointers. They can carry context and protocol state. Reconstruct or adapt them
instead of pretending a raw function pointer captures the whole value.

## `untyped`

`untyped` is the broadest raw scalar carrier. It can erase scalar carrier
identity for data pointers, function pointers, natural integers, and other
target-supported scalar values.

```camp
byte* bytes;
untyped erased = (untyped)bytes;
PacketHeader* restored = (PacketHeader*)erased;
```

Use `untyped` sparingly. It tells the reader that the program is leaving the
ordinary typed world for a moment. Prefer `void*` for data-pointer erasure and
`fn*` for function-pointer erasure when those describe the boundary precisely.

`untyped*` is not the same thing. It is a pointer to storage whose element type
is `untyped`; it is not the universal raw fence.

## Reconstruct Instead Of Cast

Conversions happen at value boundaries. They do not secretly rewrite every
component inside a larger shape.

```camp
(byte* _near)[] localPointers;
// (byte* _far)[] sharedPointers = ((byte* _far)[])localPointers; // wrong shape
```

Changing the element type of an array means building a new array with converted
elements. Changing a delegate signature means writing an adapter. Changing a
generic argument means constructing a new generic value whose type really has
that argument.

```camp
delegate void(byte* _far data) adapt(delegate void(byte* _near data) source)
{
	return new delegate (byte* _far data) =>
	{
		byte* _near local = (unsafe byte* _near)data;
		source(local);
	};
}
```

That adapter is more verbose than a cast, but it tells the truth: calls through
the new delegate perform a conversion before forwarding.

## Class, Interface, And Newtype Boundaries

Class pointer conversions follow the declared class hierarchy:

```camp
Button* button;
Control* control = button;
Button* again = (unsafe Button*)control;
```

The upcast is ordinary. The downcast is unsafe because the static type
`Control*` does not prove the object is actually a `Button`.

Interfaces have their own representation and dispatch rules, which the
interface chapter will cover in detail. For now, remember that an interface
pointer is not merely a class pointer with a different name. Let declared
interface conversions do their work instead of raw-casting object pointers into
interface pointers.

Newtypes are nominal boundaries. A value newtype may share representation with
its underlying type, and a callable newtype may share a callable shape, but the
name is part of the contract. Use construction, ascription, or an explicit cast
where the language permits crossing that boundary.

## Examples Worth Remembering

These small patterns cover most pointer-conversion decisions:

```camp
const byte* readOnly;
byte* mutable = (unsafe byte*)readOnly; // removing const
```

```camp
byte* bytes;
PacketHeader* header = (PacketHeader*)(void*)bytes; // raw data fence
```

```camp
fn int(int) callback;
fn* raw = (fn*)callback; // erase callable signature
```

```camp
byte* _near local;
byte* _far shared = local; // target-defined widening, if the target allows it
```

```camp
(byte* _near)[] localItems;
// Rebuild, do not cast, if each element needs conversion.
```

When in doubt, ask what is changing. If only the value view is changing, a cast
may be right. If a safety guarantee is being removed, say `unsafe`. If the
native ABI erases type information, use the narrowest raw carrier that describes
that erasure. If the shape changes, reconstruct the value.
