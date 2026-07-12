# Generics And Type Capabilities

## Generic Type And Function Declarations

Generic declarations use type parameters in angle brackets.

```camp
export struct Pair<T: copyable>
{
	T first;
	T second;
}

export struct BorrowedSlot<T: any>
{
	T* value;
}

export T choose<T: copyable>(T left, T right, bool useLeft)
{
	return useLeft ? left : right;
}
```

Generic parameters are scoped to the declaration that introduces them. A
generic type can use its type parameters in fields, methods, constructors,
static members, implemented interfaces, and nested signatures. The constraint
must still match the operation: a field of type `T` needs a value-copying
contract such as `T: copyable`, while a pointer field `T*` can work under
`T: any`. A generic function can use its parameters in its return type,
parameter types, local types, constraints, and capability parameters.

Camp generics are explicit about the capabilities a body uses. A type parameter
is not automatically assumed to be copyable, constructible, sized, printable,
or interface-dispatchable. This keeps erased and ABI-visible generic code
predictable.

## Constraints

A generic parameter may be constrained by `any`, `copyable`, or an interface.
Constraints determine what operations are valid on values of that type
parameter.

| Constraint | Meaning |
|---|---|
| `T: any` | `T` can be named, but few operations are assumed. |
| `T: copyable` | Values of `T` can be copied by value. |
| `T: implements Interface` | `T` satisfies an interface contract. |

Constraint checking happens both when the generic declaration is analyzed and
when a constructed use supplies type arguments. Invalid constraints are errors
at the declaration. Invalid type arguments are errors at the use.

Interface constraints are capability constraints, not representation promises.
A `struct` implementation and a `class` implementation remain distinct even
when both satisfy the same interface.

## `T: any`

`T: any` is the broadest constraint and the weakest inside the body. It accepts
any type, but it does not automatically permit:

- copying;
- default filling;
- construction or destruction;
- `sizeof(T)`;
- array stride or element layout;
- interface dispatch;
- runtime type names.

This function is therefore too weak if returning `value` requires copying an
erased value:

```camp
export T identity<T: any>(T value)
{
	return value; // invalid when the body needs copy capability
}
```

Use `T: copyable` when copying is part of the generic contract. Use explicit
capability parameters for size, vtable, and type-name values.

The `in` modifier transports erased values without turning them into ordinary
pointers. It is a way to pass a value through a generic API under controlled
rules; it does not imply that the body can copy, store, or take arbitrary
addresses of the value.

## `T: copyable`

`T: copyable` permits ordinary copying of values of type `T`.

```camp
export T duplicate<T: copyable>(T value)
{
	return value;
}
```

`copyable` means copyable and no more. It does not imply a default constructor,
destructor, allocator behavior, interface vtable, or runtime type name. Add only
the additional capabilities that the body actually uses.

Copyable constraints are useful for containers that store values by copy,
functions that choose among input values, and algorithms that rearrange values
without owning special cleanup.

## Interface Constraints

Interface constraints allow generic code to require an interface contract.

```camp
export void writeOne<T: implements Writer>(
	T* writer,
	const char[] line,
	vtableof(T: Writer))
{
	Writer* view = writer;
	view.write(line);
}
```

`implements Writer` says `T` can satisfy the `Writer` contract. It does not say
that `T*` is stored like a class interface field. If `T` is a struct, conversion
to `Writer*` may use scoped adapter storage. If `T` is a class, conversion uses
the class's stored interface slot. Generic APIs that retain interface pointers
must account for that difference.

## `sizeof(T)`, `typenameof(T)`, And `vtableof(T: Interface)`

Generic code can request explicit capability parameters:

```camp
export nuint itemSize<T: any>(sizeof(T))
{
	return sizeof(T);
}

export const char[] typeName<T: any>(typenameof(T))
{
	return typenameof(T);
}

export void draw<T: implements Drawable>(T* value, vtableof(T: Drawable))
{
	Drawable* drawable = value;
	drawable.draw();
}
```

These values are explicit because erased generic code cannot recover them on
its own. They are part of the ABI of the generic function. The caller supplies
the value that the body needs.

`sizeof(T)` is needed for allocation, array stride, erased storage, and
layout-sensitive code. `typenameof(T)` is needed when the runtime type name is
part of behavior. `vtableof(T: Interface)` is needed for erased interface
dispatch.

A `typenameof(T)` capability is exact for the requested type form. It does not
automatically provide names for related forms such as `T[]` or `T?`.

```camp
export void writeArrayType<T: any>(typenameof(T[]))
{
	Console.writeLine(typenameof(T[]));
}
```

Capability parameters can be forwarded to other generic functions. If they are
stored for later use, their lifetime and representation must be valid for the
storage that retains them. A generic class constructor that requests
`sizeof(T)`, `typenameof(T)`, or `vtableof(T: Interface)` may retain that
capability for later instance methods according to the type's lowering model.

## Generic Construction And Destruction

Generic construction and destruction require a declared source of construction
and cleanup behavior. Camp does not synthesize hidden constructors merely
because a generic body writes `new T(...)` or `init T(...)`.

Construction can be provided through an interface constructor contract, a
factory function parameter, a type-specific helper, or another explicit API.

```camp
export interface ConstructibleBuffer
{
	ConstructibleBuffer(nuint size, within allocator);
	~ConstructibleBuffer(within allocator);
}

export T* createStorage<T: implements ConstructibleBuffer>(
	nuint size,
	within allocator,
	vtableof(T: ConstructibleBuffer));
```

Generic destruction remains explicit. If the generic function owns a value that
may require cleanup, the constraint must expose the destructor or cleanup path
that the body will use.

Arrays of `T` need size or stride information. In erased generic code, request
`sizeof(T)` or call an API that already owns the storage layout.

## Generic Static Members

Generic types may have static members. Their visibility and specialization
follow the generic type declaration and exported API rules.

A static member of `Container<int>` and a static member of `Container<byte>` are
conceptually tied to different constructed generic contexts even when lowering
can share code. Exported static members must keep stable names and metadata
that preserve the source generic relationship.

## Generic Interfaces And Generic Methods

Interfaces can be generic, and interface methods can introduce their own
generic parameters.

```camp
export interface Comparer<T: any>
{
	int compare(in T left, in T right);
}

export interface Transformer
{
	TResult transform<TSource: any, TResult: any>(in TSource value);
}
```

Generic substitution affects declared parameter and result types. It does not
change the basic interface-instance representation: interface dispatch still
uses the interface slot pointer, and generic method capability parameters still
need to be supplied according to the method contract.

## Arrays, Optionals, Delegates, And Strings In Generic Code

Expanded forms remain expanded when `T` is generic. `T[]` requires element
stride and lifetime-safe element access. `T?` requires payload handling.
Delegates carry target and context. Strings are counted arrays over character
storage. Do not assume a generic type argument can be flattened into a scalar or
copied component-by-component unless the constraints say so.

```camp
export nuint countItems<T: any>(T[] values, sizeof(T))
{
	return values.length;
}
```

Even code that only reads an array length still uses a source type whose
representation depends on the generic element type.

## Lifetimes And Async In Generic Code

Lifetimes on generic values are checked structurally. If `T` may contain
pointer-bearing state, storing or returning `T` can require the same lifetime
proofs as storing or returning a concrete pointer-bearing struct.

Async generic functions must also account for erased values retained across
suspension. A value of `T` used after `await` may need to be stored in the async
frame, which can require copy, lifetime, and cleanup capabilities.

## Common Generic Design Patterns

When designing a generic API:

- start with the weakest constraint that lets the body compile;
- add `copyable` only when the body copies values;
- add `sizeof(T)` when layout, allocation, or array stride is needed;
- add `typenameof(T)` only when runtime names are part of behavior;
- add `vtableof(T: Interface)` when erased interface dispatch is needed;
- use interface constructor/destructor contracts for generic construction and
  cleanup;
- remember that struct and class interface implementations have different
  storage models.
