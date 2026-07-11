# Enums, Newtypes, And Inline Constants

## Enums

`enum` declarations introduce named values.

```camp
export enum Direction
{
	North,
	South,
	East,
	West
}
```

Enum values can be used in expressions and switches.

## Fixed-Representation Enums

An enum can specify an underlying representation when a stable native shape is
part of the API.

```camp
export enum Status: int
{
	Ok = 0,
	Failed = 1
}
```

Fixed representation is useful for interop and serialized protocols.

## Enum Values And Symbols

Enum values have source names. `@symbol` can override emitted native names when
the ABI requires a specific spelling.

## `newtype`

`newtype` creates a nominal wrapper around an underlying type or shape.

```camp
export newtype int UserId;
```

Newtypes prevent accidental interchange with plain values unless a conversion or
construction explicitly crosses the boundary.

## Callable Newtypes

Callable newtypes give names to callback shapes.

```camp
export newtype delegate void ProgressHandler(nuint completed, nuint total);
```

Use callable newtypes when a callback is part of a public API or appears in many
places.

## Inline Constants

`inline` constants are compile-time values emitted at use sites or in metadata
as needed.

```camp
export inline int MaxSmallBuffer = 256;
```

Inline initializers must be valid for the declared inline type.

## Type-Scope Constants

Types may contain inline constants in their scope.

```camp
export struct Limits
{
	inline int DefaultCapacity = 64;
}
```

Type-scope constants are addressed through the type's namespace.
