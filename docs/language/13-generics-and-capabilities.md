+++
nav_title = "13. Generics"
+++

# Generics And Capabilities

Camp generics are built around type erasure and explicit capabilities. If
Java's erased generics are familiar, the phrase should ring a bell: a generic
body is checked once under the constraint it declares, and the body cannot
quietly assume every operation will exist after substitution. If C++ templates
are the comparison point, the important contrast is that Camp generics are not
template macros that get to discover their errors only when a particular
instantiation is compiled.

Camp adds one low-level twist to that familiar story. Some generics are
representation generics, where the type parameter stands for a known scalar
carrier such as `uint` or `nint`. Others are erased value generics, where the
type parameter may stand for values with very different storage shapes. Erased
generic code asks for the runtime support it needs: `sizeof(T)` for storage
size, `typenameof(T)` for a type-name value, and `vtableof(T: Interface)` for
interface dispatch.

The goal is not to make generic code ceremonial. It is to keep the source API
honest about what the compiled ABI must carry.

## The Shape Of A Generic Declaration

A generic declaration introduces type parameters in angle brackets:

```camp
T choose<T: copyable>(T left, T right, bool useLeft, sizeof(T))
{
	return useLeft ? left : right;
}
```

At a call site, Camp can often infer the type argument:

```camp
int selected = choose(10, 20, preferFirst);
```

When inference does not have enough information, or when spelling the type
helps the reader, write the type argument explicitly:

```camp
int selected = choose<int>(10, 20, preferFirst);
```

The constraint after the colon is not decoration. `choose` returns a `T` by
value, so it needs `T: copyable`; because the erased copy needs a runtime
storage size, the signature also asks for `sizeof(T)`. A generic body is checked
against the constraint it declares, not against the first friendly type argument
that happens to work.

Generic type declarations use the same form:

```camp
class BorrowedSlot<T: any>
{
	T* value;

	void set(T* value)
	{
		this.value = value;
	}

	T* getValue()
	{
		return this.value;
	}
}
```

This class can use `T: any` because it stores a pointer to `T` storage, not a
`T` value. That is the first Camp habit to learn: choose the constraint for the
operations the body actually performs.

## Checked Once, Not Per Instantiation

Camp validates generic definitions eagerly. A generic function must be valid
under its own constraint even if every current caller passes a type that would
make the body work.

This is invalid:

```camp
T first<T: any>(T[] values, sizeof(T))
{
	return values[0]; // ERROR: copies T under T: any
}
```

`T: any` permits very broad erased values, including values that cannot be
copied by ordinary value operations. The fix is to say that the API copies:

```camp
T first<T: copyable>(T[] values, sizeof(T))
{
	return values[0];
}
```

That one rule explains a lot of Camp's generic surface. The constraint is the
promise. The capabilities are the extra ABI facts the erased body needs in
order to keep that promise.

## Constraints At A Glance

A type parameter has one constraint. If no constraint is written, the default
constraint is `nint`. That default is useful for small machine-word-style
helpers, but it is usually clearer to write the constraint explicitly.

`untyped` is a raw carrier type used for explicit low-level erasure and casts.
It is not the default generic constraint.

So this:

```camp
struct WordSlot<T>
{
	T value;
}
```

is shorthand for a representation generic with the default `T: nint`
constraint, not for an erased `T: any` generic.

| Form | Generic model | What the body may assume |
|---|---|---|
| `T: uint` | representation generic | `T` has a compatible integer-like representation. |
| `T: nint` | representation generic | `T` has a natural-integer or pointer-shaped representation. |
| `T: any` | erased value generic | `T` can be named, addressed, transported, and observed with explicit support. |
| `T: copyable` | erased value generic | `T` values can be copied, assigned, used in locals, returned, and yielded by value. Direct `T` fields are still not allowed. |
| `T: implements Reader` | erased value generic plus interface contract | `T` explicitly implements `Reader`; dispatch still needs a vtable capability. |

Representation constraints are for scalar carrier families. Erased constraints
are for general Camp values.

## Representation Generics

Representation generics are the small, C-like side of Camp generics. They are
for code that genuinely wants a known integer or pointer-sized carrier.

```camp
struct CounterMap<T: uint>
{
	T key;
	uint count;

	void add(T key, uint amount = 1)
	{
		if (this.key == key)
			this.count += amount;
	}
}
```

Valid integer representation constraints are:

- `byte`, `ushort`, `short`, `uint`, `int`;
- `ulong`, `long`;
- `nuint`, `nint`.

For most integer constraints, valid type arguments are integer types of equal
or smaller width, enums with compatible underlying types, and value newtypes
with compatible underlying types:

```camp
enum SmallCode: byte
{
	READY,
	BUSY
}

newtype LocalIndex: ushort;

struct Slot<T: uint>
{
	T value;
}

Slot<byte> byteSlot;
Slot<SmallCode> codeSlot;
Slot<LocalIndex> indexSlot;
// Slot<ulong> wideSlot; // ERROR: wider than uint
```

`nint` and `nuint` are special because they also accept pointer types:

```camp
struct MachineValue<T: nint>
{
	T value;
}

MachineValue<int> count;
MachineValue<void*> address;
MachineValue<byte*> bytes;
```

Use representation constraints when the body is really about the carrier. Do
not use them as a substitute for `T: any` or `T: copyable` in ordinary erased
generic code.

## Type Erasure With `T: any`

`T: any` is the broadest erased value constraint. It can stand for scalars,
pointers, copyable structs, fixed structs, classes, fixed-size arrays, and
expanded Camp forms through materialized storage.

That breadth is exactly why the body gets fewer assumptions. Under `T: any`,
the body may not copy, assign, return, or store a `T` value by value.

Use pointers when the API is really about storage:

```camp
class PointerBox<T: any>
{
	T* value;

	void set(T* value)
	{
		this.value = value;
	}
}
```

This is invalid because it tries to store the erased value itself:

```camp
class BadBox<T: any>
{
	T value; // ERROR: generic values constrained to any must be passed by reference
}
```

The error is not that `T: any` is useless. It is that `T: any` deliberately
does not promise a copyable, one-size-fits-all value representation.

## `in T` As Erased Value Transport

When an erased generic API wants to observe a value rather than store it, use
`in T`:

```camp
void inspect<T: any>(in T value, sizeof(T))
{
	logSize(sizeof(T));
}
```

At the source level, an `in T` parameter feels like a value parameter: callers
pass a value, not an explicit pointer. At the ABI level, it gives the compiler
room to transport the value through storage appropriate for the substituted
type.

That makes `in T` a good shape for comparers, hashers, visitors, and other
APIs that look at a value without taking ownership of it:

```camp
newtype fn bool Equals<T: any>(in T left, in T right);
```

`in T` does not grant permission to copy, return, or retain the value. If the
body needs those operations, the signature needs a stronger constraint or a
more explicit ownership contract.

## `T*` Means Storage Form

Inside erased generic code, `T*` is a pointer to the storage form of `T`.

For a scalar substitution such as `T = int`, that means `int*`. For a
fixed-size array substitution such as `T = byte[32]`, it means `byte[32]*`.
For an expanded form such as `T = char[]` or `T = int?`, it means a pointer to
the materialized storage form, conceptually `struct(T)*`.

That rule lets generic code talk about storage without pretending every source
value is physically shaped the same way:

```camp
void swapPointers<T: any>(T** left, T** right)
{
	T* temporary = *left;
	*left = *right;
	*right = temporary;
}
```

This swaps pointers to `T` storage. It does not copy the `T` values.

## Copyable Erased Values

Use `T: copyable` when a generic function copies, assigns, returns, yields, or
uses local `T` storage.

```camp
void copyTo<T: copyable>(const T[] source, T[] destination, sizeof(T))
{
	nuint count = source.length;
	if (destination.length < count)
		count = destination.length;

	for (nuint index = 0; index < count; index++)
		destination[index] = source[index];
}
```

`copyable` means copyable and no more. It does not imply construction,
destruction, a type name, an interface vtable, or an allocator.

One important boundary is easy to miss: `T: copyable` still does not allow a
generic type to store a direct field of type `T`.

```camp
class BadValueBox<T: copyable>
{
	T value; // ERROR: erased generic fields cannot store T directly
}
```

An erased generic type has one physical layout. A direct `T` field would require
the class or struct layout to change with every substituted `T`, which is not
Camp's model. Store a pointer, an array view, or explicit erased storage
instead:

```camp
class RefBox<T: copyable>
{
	T* value;
}

class BufferView<T: copyable>
{
	T[] values;
}
```

Local variables are different. A generic function can use local `T` storage
when its signature carries the capabilities needed by the operation:

```camp
T choose<T: copyable>(T left, T right, bool useLeft, sizeof(T))
{
	T selected = useLeft ? left : right;
	return selected;
}
```

Direct class types, fixed structs, and fixed-size arrays do not satisfy
`copyable`. Pointers to them do, because the pointer value itself is copyable:

```camp
class Widget
{
}

fixed struct ParserState
{
	nuint position;
}

struct InlineBuffer
{
	fixed byte[4] bytes;
}

class RefHolder<T: copyable>
{
	T* value;
}

RefHolder<int>* countHolder;
RefHolder<Widget*> widgetPointerHolder;
RefHolder<ParserState*> statePointerHolder;
RefHolder<InlineBuffer>* inlineBufferHolder;
// RefHolder<Widget>* widgetHolder;       // ERROR
// RefHolder<ParserState>* stateHolder;   // ERROR
// RefHolder<byte[4]>* fixedArrayHolder;  // ERROR
```

Some copy operations in erased code also need `sizeof(T)`. The constraint says
copying is allowed; the size capability tells lowered code how much storage is
involved when size is not otherwise known.

## `sizeof(T)`

`sizeof(T)` is an explicit capability parameter. It supplies the size of the
concrete substituted type's storage form:

```camp
nuint itemSize<T: any>(sizeof(T))
{
	return sizeof(T);
}
```

Callers normally do not write an ordinary argument for it:

```camp
nuint size = itemSize<int>();
```

Across a C-facing boundary, the idea is ordinary. This Camp function:

```camp
void clear<T: any>(T[] values, sizeof(T))
{
	for (nuint index = 0; index < values.length; index++)
		values[index] = default;
}
```

has the same kind of ABI ingredients you would write by hand in C:

```c
void clear(void *values, size_t values_length, size_t sizeof_T);
```

The exact emitted spelling belongs to the target, but the model is stable:
`sizeof(T)` is an ABI-visible fact requested by the generic signature.

Reach for `sizeof(T)` when the body needs element stride, erased storage size,
generic array indexing, allocation, default filling, or erased copy lowering.
It does not make `T: any` copyable:

```camp
void badCopy<T: any>(T* destination, T* source, sizeof(T))
{
	*destination = *source; // ERROR: size is not copy permission
}
```

## `typenameof(T)`

`typenameof(T)` supplies a type-name capability:

```camp
string nameOf<T: any>(typenameof(T))
{
	return typenameof(T);
}
```

It is a type-form operation, not reflection over a runtime value:

```camp
string intName = typenameof(int);
string arrayName = typenameof(byte[]);
// string bad = typenameof(value); // ERROR: value is not a type
```

A capability for `typenameof(T)` is exact. It does not also provide
`typenameof(T[])`:

```camp
string arrayTypeName<T: any>(typenameof(T[]))
{
	return typenameof(T[]);
}
```

A type-name capability carries a name. It does not carry size, copyability,
constructors, destructors, or interface vtables.

## Interface Constraints And `vtableof`

An interface constraint is nominal:

```camp
interface Retainable
{
	void retain();
	void release();
}

void retainTwice<T: implements Retainable>(
	T* value,
	vtableof(T: Retainable))
{
	value.retain();
	value.retain();
}
```

`T: implements Retainable` says the concrete type explicitly implements the
interface. `vtableof(T: Retainable)` supplies the interface vtable needed for
erased dispatch.

A simplified C-shaped view looks like this:

```c
void retainTwice(void *value, const RetainableVTable *vtable)
{
	vtable->retain(value);
	vtable->retain(value);
}
```

Real emitted code may adjust the receiver context differently for class and
struct implementations. The useful source rule is that the generic value is
still a concrete `T`, and the vtable capability supplies the dynamic call path.

That distinction matters when a generic API wants to keep something for later.
A call-only algorithm can often accept `T*` plus `vtableof(T: Interface)`.
A storage API may need to store the concrete pointer and vtable together, or
choose a class/interface-pointer surface instead.

## Generic Interfaces And Generic Methods

Interfaces can be generic:

```camp
interface Comparer<T: any>
{
	int compare(in T left, in T right);
}
```

Interface methods can introduce their own generic parameters:

```camp
interface Transformer
{
	TResult transform<TSource: any, TResult: any>(in TSource value);
}
```

Generic substitution affects the parameter and result types in the interface
contract. It does not change the basic interface representation: interface
calls still go through interface slots and vtables.

## Arrays And Other Expanded Forms

`T[]` is still an array view in generic code: element pointer plus length. The
moment the body indexes, slices, allocates, or walks element storage for an
erased `T`, it needs `sizeof(T)`.

For erased element types, a storage-aware routine can walk the array and pass
each element's address to a callback:

```camp
void visitEach<T: any>(T[] values, fn void(T* value) visit, sizeof(T))
{
	for (nuint index = 0; index < values.length; index++)
		visit(&values[index]);
}
```

If the loop body copies, returns, yields, or keeps an element value in a local,
use `T: copyable`:

```camp
struct iter T iterate<T: copyable>(T[] values, sizeof(T))
{
	foreach (auto value in values)
		yield value;
}
```

Optionals, delegates, `once` values, arrays, and counted text remain built-in
expanded forms. When erased storage needs one address for such a value, Camp
uses the materialized storage form. Do not assume a generic `T` is scalar just
because a particular call uses a scalar type argument.

## Generic Construction And Cleanup

Camp does not invent constructors for erased `T`. If generic code needs to
create or destroy values, make that path part of the API.

One common pattern is an explicit policy:

```camp
struct ObjectPolicy<T: any>
{
	fn T*() create;
	fn void(T* value) destroy;
}

T* createWith<T: any>(ObjectPolicy<T> policy)
{
	return policy.create();
}

void destroyWith<T: any>(T* value, ObjectPolicy<T> policy)
{
	policy.destroy(value);
}
```

If allocation context matters, put `within allocator` on the factory or
cleanup callable just as you would on an ordinary function. Generic allocation
is still ordinary Camp allocation.

Concrete generic types can use ordinary constructors when the constructed type
is known:

```camp
class Box<T: any>
{
	Box()
	{
	}
}

Box<int>* box = new Box<int>();
```

That constructs `Box<int>`. It does not mean erased code can freely write
`new T()` without a construction contract.

## Static Members

Generic types may have static members:

```camp
class Cache<T: any>
{
	inline int DEFAULT_LIMIT = 16;

	static int defaultLimit()
	{
		return Cache.DEFAULT_LIMIT;
	}
}
```

Static member access uses the generic type name, not a constructed type
argument list:

```camp
int limit = Cache.DEFAULT_LIMIT;
int again = Cache.defaultLimit();
```

If a static member needs its own generic type, declare that parameter on the
static member:

```camp
class SizeTools<T: any>
{
	static nuint sizeOf<U: any>(sizeof(U))
	{
		return sizeof(U);
	}
}
```

Do not rely on the containing type's generic parameters from an unrelated
static member. Static generic APIs are clearest when they declare exactly the
capabilities they use.

## Designing Generic APIs

A Camp generic signature should read like a short list of the operations the
body will perform:

- Use a representation constraint such as `T: uint` or `T: nint` when the body
  relies on a known scalar carrier.
- Use `T: any` when the body only observes values, points at storage, or walks
  erased storage with explicit capabilities.
- Use `in T` for erased value inputs that should feel value-like to callers
  without granting copy or ownership rights.
- Add `sizeof(T)` when the body needs size, stride, allocation, default-fill,
  generic array indexing, or erased copy lowering.
- Use `T: copyable` when the body copies, assigns, uses local storage, returns,
  or yields `T` by value. Use `T*`, `T[]`, or explicit erased storage when a
  generic type needs to keep values in fields.
- Add `typenameof(T)` only when the type name is part of behavior.
- Use `T: implements Interface` plus `vtableof(T: Interface)` when erased code
  needs interface dispatch.
- Pass construction and cleanup explicitly when generic code owns values.

That is the rhythm of Camp generics: erase the type, then ask for only the
facts the erased body needs. The result is a generic API that remains readable
at the source level and unsurprising at the ABI boundary.
