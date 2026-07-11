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

## Destructors

Destructors use `~TypeName` and release resources owned by an instance.

```camp
~Buffer()
{
	delete this.data;
}
```

Destructors do not return values.

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

## Abstract, Virtual, Override, Sealed, And Extern Types

`virtual` and `abstract` declare dispatch points. `override` implements a
virtual member from a base type. `sealed` prevents further overriding or
inheritance where the declaration form supports it.

`extern` types describe native or externally implemented boundaries. Camp does
not allow source lifecycle rules that would make ownership of an external base
class ambiguous.
