# Generics And Type Capabilities

## Generic Type And Function Declarations

Generic declarations use type parameters in angle brackets.

```camp
export struct Pair<T: any>
{
	T first;
	T second;
}

export T choose<T: any>(T left, T right, bool useLeft)
{
	return useLeft ? left : right;
}
```

## Constraints

A generic parameter may be constrained by `any`, `copyable`, or an interface.
Constraints determine what operations are valid on values of that type
parameter.

## `T: any`

`T: any` accepts any type, but it does not automatically permit copying,
default filling, construction, or layout-sensitive operations. Generic code
must request the capabilities it uses.

## `T: copyable`

`T: copyable` permits ordinary copying of values of type `T`.

```camp
export T duplicate<T: copyable>(T value)
{
	return value;
}
```

## Interface Constraints

Interface constraints allow generic code to call interface methods or request
vtable capabilities.

```camp
export void writeAll<T: implements Writer>(T* writer, const char[][] lines);
```

## `sizeof(T)`, `typenameof(T)`, And `vtableof(T: Interface)`

Generic code can request explicit capabilities:

```camp
export nuint itemSize<T: any>(sizeof(T))
{
	return sizeof(T);
}

export const char[] typeName<T: any>(typenameof(T))
{
	return typenameof(T);
}
```

`vtableof(T: Interface)` supplies interface dispatch capability for a generic
type parameter.

## Generic Construction And Destruction

Generic construction and destruction require the relevant type information and
capabilities. Code that allocates `T[]`, constructs `T`, or destroys `T` must
declare what it needs.

## Generic Static Members

Generic types may have static members. Their visibility and specialization
follow the generic type declaration and exported API rules.
