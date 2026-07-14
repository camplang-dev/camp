# Declarations And Program Shape

Once a Camp file grows past `Hello, Camp.`, it becomes a set of declarations.
That is the first real shift from "I can read a function body" to "I can read a
Camp program."

Declarations are the named pieces of a program: functions, structs, classes,
interfaces, enums, newtypes, aliases, variables, constants, and members inside
types. They are also where Camp puts most of the information that matters at a
native boundary: visibility, exported API shape, allocation and error slots,
receiver rules, generic capabilities, and interop markers.

This chapter is the map. Later chapters go deep on each feature; this one helps
you look at a source file and understand what kind of thing each declaration is
and why it belongs there.

## A File Is Not A Script

Camp source files contain declarations. They can also contain file-level setup
such as imports, build prelude directives, and `export as`, but ordinary program
logic lives inside function and method bodies.

Here is a small complete file:

```camp
using Std;

export int main()
{
	Size size = { .width = DefaultWidth, .height = 3 };
	Console.writeLine(area(size));
	return 0;
}

int area(Size size)
{
	return size.width * size.height;
}

struct Size
{
	int width;
	int height;
}

inline int DefaultWidth = 4;
```

Top-level declaration order generally does not matter for visibility within the
same source scope. Camp does not need the C habit of putting callees or types
first just to satisfy forward declarations, so examples in this guide prefer
call order: show the entry point, then show what it calls or uses.

Member order often matters more. Field order affects layout. Enum value order
affects default numbering. Interface member order affects the contract's shape.
Overload and metadata order should be stable for readers and tools. When order
is part of a declaration's meaning, keep it deliberate.

## Top-Level Declarations

Top-level declarations live directly in a source file or module. They introduce
the names other declarations can use.

Common top-level forms:

| Form | Introduces | Use it when |
|---|---|---|
| function | A callable operation. | You want named executable behavior. |
| `struct` | A value/layout type. | Storage shape and field layout matter. |
| `class` | An identity/lifecycle type. | Instances have identity, allocation, or virtual behavior. |
| `interface` | A dynamic contract. | Callers need a vtable-shaped API. |
| `enum` | A named integer choice. | A raw integer would hide meaning. |
| `newtype` | A distinct nominal wrapper. | Existing representation needs a new meaning. |
| `alias` | Another source name. | You want readability without a new nominal type. |
| variable | Storage. | The program needs a named storage location. |
| inline constant | A compile-time value. | The value belongs in the API without storage. |

The exact syntax varies by declaration kind, but the reading habit is the same:
start with the modifiers, identify the kind of declaration, then read the name
and body.

```camp
alias ByteCount = nuint;

enum FileKind
{
	REGULAR,
	DIRECTORY,
	DEVICE
}

inline ByteCount DefaultBufferSize = 4096;
```

`ByteCount` is only another name for `nuint`. `FileKind` is a nominal enum.
`DefaultBufferSize` is a compile-time value that exported callers can see.

## Functions

A function declaration has a name, a return type, parameters, and either a body
or a valid bodyless surface.

```camp
int clampExitCode(int value)
{
	if (value < 0)
		return 1;
	if (value > 255)
		return 255;
	return value;
}
```

Read a function signature from left to right:

1. Optional visibility says whether it crosses a broader source or ABI boundary.
2. `int` is the result type.
3. `clampExitCode` is the source name.
4. `(int value)` is the parameter list.
5. The body is the Camp implementation.

Parameters are part of the callable contract. Later chapters add more parameter
forms, such as `out`, `thrown`, `within`, receivers, defaults, and generic
capabilities. For now, treat the signature as the place where a function says
what it needs from callers.

```camp
int parsePort(const char[] text, thrown ParseError error);
Buffer* createBuffer(nuint capacity, within allocator);
```

These are not just "an extra parameter or two." A `thrown` slot changes error
flow. A `within` parameter changes allocation policy. Camp puts those contracts
in the declaration so a caller can see them at the call site.

## Bodies And Semicolons

A body provides Camp implementation:

```camp
int doubleValue(int value)
{
	return value * 2;
}
```

A semicolon declares a surface without a Camp body only where that makes sense.
Common examples include extern functions, interface entries, and abstract
members:

```camp
@symbol("strlen")
extern nuint nativeStringLength(const char* text);

interface Reader
{
	nuint read(byte[] buffer);
}
```

Do not read every semicolon as a C-style prototype. In Camp, a bodyless
declaration is still a real source contract. The kind of declaration tells you
who is expected to provide the implementation: native code, an implementing
class, a derived type, or another valid surface.

## Type Declarations

Type declarations introduce names for values and objects.

```camp
struct Position
{
	int row;
	int column;
}

class Window
{
}

interface Writer
{
	void write(const char[] text);
}
```

The main type forms have different jobs:

| Type form | Think of it as | Typical use |
|---|---|---|
| `struct` | Direct value storage. | Coordinates, headers, spans, small records. |
| `class` | Allocated identity with lifecycle. | Handles, services, mutable objects. |
| `interface` | Dynamic dispatch contract. | Plug-in points and abstraction boundaries. |
| `enum` | Nominal integer choice. | Modes, states, status values, error kinds. |
| `newtype` | Nominal wrapper around a representation. | IDs, handles, typed callbacks. |

This guide will return to all of them. The important early rule is that these
forms are not interchangeable just because a target could represent them with
similar bits. Camp uses the declaration kind as part of the source contract.

## Members Inside Types

Types can contain fields and members. A member belongs to its containing type
and participates in lookup, receiver binding, visibility, and generated API
surface.

```camp
class Counter
{
	int value;

	void increment()
	{
		this.value += 1;
	}

	int getValue()
	{
		return this.value;
	}

	int computeTotal(const this)
	{
		return this.value + 10;
	}
}
```

Common member forms:

- fields store data inside structs and classes;
- methods operate on a receiver;
- constructors initialize a new instance;
- destructors clean up an instance;
- static members belong to the type rather than one instance;
- interface-marked methods fill a specific interface slot.

The receiver is the value or object a method is called on. In an instance
method, `this` is the receiver. Get-style methods have an implicit const
receiver, so `getValue` does not need to write `const this`. Other methods can
write an explicit receiver when they want to state constness, lifetime, or
extension-style behavior.

```camp
int computeTotal(const this)
{
	return this.value + 10;
}
```

That `const this` matters. It says the method can read through the receiver but
cannot mutate it through that receiver view.

## Visibility And Exported Shape

Visibility modifiers are part of the declaration's contract.

| Modifier | Meaning in ordinary code |
|---|---|
| `export` | Put this declaration on the public API/ABI boundary. |
| `public` | Make this declaration visible to other Camp source without necessarily exporting ABI. |
| no visibility keyword | Keep it private to the relevant source scope. |
| `extern` | The implementation or definition is provided outside Camp. |

Use `export` deliberately. It is not just "public, but louder." An exported
declaration can affect generated headers, metadata, native symbols, layout
commitments, and downstream compatibility.

```camp
export struct PacketHeader
{
	uint magic;
	ushort version;
}

int checksum(PacketHeader header)
{
	return (int)(header.magic + header.version);
}
```

Here `PacketHeader` is public API. `checksum` is an internal helper unless it
is later marked `public` or `export`.

## Static Members And Inline Constants

Use `static` for behavior or storage associated with a type rather than an
instance. Use `inline` for a compile-time value with no ordinary storage.

```camp
export int main()
{
	return ExitCodes.OK;
}

class ExitCodes
{
	inline int OK = 0;
	inline int USAGE = 2;
}
```

Inline constants are useful when a value belongs to an API contract:

```camp
export inline uint ProtocolVersion = 3;
```

Do not use `const` when you mean `inline`. `const` describes what can be
mutated through a view. `inline` says the declaration is a compile-time value
rather than storage.

## Generic Declarations At A Glance

Functions and types can declare generic parameters. A generic parameter is not
a blank check; its constraints say what operations the declaration can use.

```camp
export int main()
{
	int[] values = [1, 2, 3];
	return sumItems(values, valueOfInt) == 6 ? 0 : 1;
}

int sumItems<T: any>(T[] values, delegate int(in T item) valueOf, sizeof(T))
{
	int total = 0;
	foreach (auto item in values)
		total += valueOf(item);
	return total;
}

int valueOfInt(in int item) => item;
```

`sumItems(values, valueOfInt)` does not spell `int` at the call site. The
compiler can often infer generic arguments from the values you pass. `T: any`
allows the function to receive values of many element types, but it does not
promise arithmetic, copying, construction, comparison, or formatting. The
caller supplies `valueOfInt` to say how an item contributes to the sum.
`sizeof(T)` is an explicit capability parameter that lets erased generic code
know the element size.

Generics are a major feature, so this chapter only sets the expectation:
generic declarations state the capabilities they need instead of assuming a
large runtime can recover them later.

## Choosing The Right Declaration Form

When you are deciding how to introduce a name, start with the promise you want
the name to make.

| Need | Prefer |
|---|---|
| Named behavior | Function or method |
| Small record with visible layout | `struct` |
| Object identity, allocation, or virtual behavior | `class` |
| Dynamic contract | `interface` |
| Named set of integer values | `enum` |
| Strong name for an existing representation | `newtype` |
| Readable synonym only | `alias` |
| Stored mutable state | variable or field |
| Compile-time API value | `inline` constant |
| Native function or type from elsewhere | `extern` declaration |

The details will become sharper as the guide continues. For now, the key habit
is simple: choose the declaration form that says what the name means, not just
the one that happens to compile.
