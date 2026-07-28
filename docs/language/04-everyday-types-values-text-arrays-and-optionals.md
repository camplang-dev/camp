+++
nav_title = "4. Everyday Types"
+++

# Everyday Types: Values, Text, Arrays, And Optionals

Most Camp code is not written in abstract type theory. It moves integers,
booleans, text, byte spans, arrays of values, optional results, and small
records. This chapter is about those everyday types: the values you reach for
before interfaces, async calls, iterators, or generic capability transport enter
the picture.

There is one Camp idea to keep in mind as you read: a type can describe a value
without saying who owns the storage behind it. `int` is just a scalar value.
`char[]` is a counted view. `string` is a zero-terminated string pointer.
`T?` is a value that may or may not contain a payload. Ownership is an API
contract layered over those types, not a secret property hidden inside the
spelling.

## A Small Program With Everyday Types

Here is a complete program that uses scalars, arrays, optionals, and console
output:

```camp
export int main()
{
	int[] scores = [10, 20, 30];
	int total = sum(scores);
	int? firstScore = first(scores);

	if (!firstScore.specified)
		return 1;

	Console.writeLine(total);
	Console.writeLine(firstScore.value);
	return 0;
}

int sum(const int[] values)
{
	int total = 0;
	for (nuint index = 0; index < values.length; index++)
		total += values[index];
	return total;
}

int? first(const int[] values)
{
	if (values.length == 0)
		return default;

	return values[0];
}
```

Several important ideas are already present:

- `int` is a scalar value.
- `int[]` is a counted array view.
- `const int[]` says the function will not mutate through the view.
- `nuint` is the natural unsigned type used for array lengths and indices.
- `int?` represents either an `int` payload or no value.
- `default` gives the empty optional when the destination type is `int?`.

The rest of the chapter turns those ideas into a reference you can return to.

## Values, Views, And Storage

Camp types often fall into three practical categories:

| Category | Examples | What to remember |
|---|---|---|
| Scalar values | `int`, `bool`, `double`, `char` | The value is the thing. |
| Views | `T*`, `T[]`, `string`, interface pointers | The value points at or describes other storage. |
| Aggregate values | structs, fixed arrays, optionals | The value has internal shape. |

This distinction is more useful than asking whether something "is a reference"
in the abstract. A `byte[]` array view can be copied cheaply, but copying the
view does not copy the bytes. A `string` can point at literal storage or owned
allocated storage. A `struct` can contain pointer-like fields whose storage
lives somewhere else.

When reading or designing an API, ask two questions:

1. What is the source type?
2. Who owns the storage, if any, behind that value?

The type answers the first question. Lifetimes, allocation, cleanup, and API
documentation answer the second.

## Primitive Scalars

Camp's scalar primitive types are built into the language. You do not import
them from `Std`.

| Family | Types |
|---|---|
| Signed integers | `sbyte`, `short`, `int`, `long`, `nint` |
| Unsigned integers | `byte`, `ushort`, `uint`, `ulong`, `nuint` |
| Floating point | `float`, `double` |
| Boolean | `bool` |
| Character/code values | `char`, `wchar`, `uchar`, `achar` |
| Raw boundary carrier | `untyped` |
| No value | `void` |

Use fixed-size integer types for stable formats and ABIs:

```camp
uint messageId = 0x000E;
ushort protocolVersion = 3;
```

Use `nint` and `nuint` for target-natural counts, indices, pointer-sized
results, and native integer carriers:

```camp
nuint count = values.length;
for (nuint index = 0; index < values.length; index++)
	process(values[index]);
```

`bool` is a real boolean type. Conditions should be boolean expressions, not
accidental integer or pointer tests:

```camp
if (count != 0)
	Console.writeLine("has values");

if (buffer != null)
	Console.writeLine("has buffer");
```

## Text: `string` And Counted Character Views

Camp has primitive string pointer types and counted character-array views.
They are related, but they are not interchangeable habits.

| Type | Model | Good for |
|---|---|---|
| `string` | Zero-terminated UTF-8 string pointer. | Compact string values and native string-like APIs. |
| `const char[]` | Counted UTF-8 byte view. | Camp APIs that need an explicit length. |
| `char[]` | Mutable counted UTF-8 byte view. | Writable buffers and in-place edits. |
| `const char*` | Pointer to character storage. | Native C-style boundaries. |

String literals are constant text:

```camp
string title = "Camp";
const char[] label = "ready";
const char* nativeText = "native";
```

The same pattern exists for wide and system-code-page text: `wstring` pairs
with `wchar` storage, and `astring` pairs with `achar` storage. Use those forms
when an API or target boundary is explicitly UTF-16-oriented or native
code-page-oriented.

A string literal cannot become mutable storage just because a mutable type is
convenient:

```camp
char[] mutableLabel = "ready"; // ERROR: literal storage is const
```

Use counted views for Camp APIs when the length matters:

```camp
nuint byteLength(const char[] text)
{
	return text.length;
}
```

Use `string` when the API is intentionally string-pointer-shaped:

```camp
string name = "Ada";
Console.writeLine(name);
```

Interpolated text uses `$"..."`:

```camp
int count = 3;
Console.writeLine($"Found {count} files");
```

When the inserted values are known at compile time, the result is ordinary
constant text. When runtime formatting is needed, Camp produces a formatter
value instead of allocating a new `string`. That formatter can be passed to APIs
such as `Console.writeLine` directly:

```camp
Console.writeLine("Found " + count + " files");
```

Ask for an owned string only when you really need one:

```camp
string message = ($"Found {count} files").copyString() finally delete;
```

The standard library supplies helpers for trimming, searching, comparing,
copying, and transcoding text. The language guide uses only a small amount of
that surface so it does not turn into an API catalog.

## Arrays Are Counted Views

`T[]` is a counted view over element storage. It has visible `.elements` and
`.length` components:

```camp
nuint countBytes(const byte[] data)
{
	return data.length;
}

byte* firstByte(byte[] data)
{
	return data.elements;
}
```

Copying an array value copies the view, not necessarily the elements:

```camp
int[] values = [1, 2, 3];
int[] sameStorage = values;

sameStorage[0] = 9;
Console.writeLine(values[0]); // prints 9
```

That behavior is not surprising once you read `T[]` as "pointer plus length."
It is a small source value describing storage elsewhere.

Array indexing uses `value[index]`:

```camp
int firstValue = values[0];
```

Slicing uses range syntax:

```camp
int[] middle = values[1..3];
int[] tail = values[1..];
```

Slices are also views. They do not allocate new storage unless a library helper
explicitly copies.

## Custom Indexing And Slicing

The array syntax you just saw is the built-in surface for counted views, but
Camp lets library types expose the same shape through ordinary methods. The
method remains the real API; the attributes only tell the compiler which
parameters behave like indexes or ranges.

Use `@index` for a parameter that is an index into the receiver. This enables
from-end syntax such as `^1` when the receiver has a visible length through a
`.length` field, a `.Length` property, or a `getLength()` method.

Use `@range` on the first parameter of an `index, count` pair. That lets
callers use either direct `index, count` form or boundary range form.

Here is an excerpt from a custom row-like type:

```camp
class PixelRow
{
	byte[] pixels;

	nuint getLength()
	{
		return this.pixels.length;
	}

	byte get(@index nuint index)
	{
		return this.pixels[index];
	}

	byte[] slice(@range nuint index = 0, nuint count = ^0)
	{
		return this.pixels[index..(index + count)];
	}
}
```

Callers get a familiar surface:

```camp
byte first = row.[0];
byte last = row.[^1];
byte[] middle = row.slice(2, 4);
byte[] trimmed = row.slice(2..^1);
```

The comma form and the range form do different things. `row.slice(2, 4)`
passes index `2` and count `4`. `row.slice(2..^1)` treats the written values
as boundaries, clamps them to the receiver length, and computes the count from
the final start and end.

This keeps user-defined sequence APIs close to built-in arrays without adding
a separate slice object model. If the method is exported, the ABI is still the
ordinary `index, count` method shape.

## Fixed-Size Arrays Store Inline

`T[N]` is fixed-size storage. Use it when the elements are part of the storage
of the local, field, or containing value.

```camp
fixed byte[4] magic = [0x43, 0x41, 0x4D, 0x50];
```

Inside a struct, fixed arrays are useful for native records and packet-like
data:

```camp
struct PacketHeader
{
	fixed byte[4] magic;
	uint length;
}
```

Use `T[]` when you want a view over storage. Use `T[N]` when you want the
storage itself to be inline. The difference matters for layout, copying,
initialization, and native interop.

## Allocated And Initialized Arrays

Array literals are useful for small values:

```camp
int[] values = [1, 2, 3];
const byte[] bytes = [(byte)'a', (byte)'b', (byte)'c'];
```

`new T[count]` allocates element storage and returns an array view. `init
T[count]` creates initialized storage in contexts where inline temporary
storage is appropriate.

```camp
char[] heapBuffer = new char[128] finally delete;
char[] localBuffer = init char[128];
```

Allocation and cleanup rules get their own chapter. The everyday rule is this:
an array view can describe allocated storage, literal storage, fixed storage,
or borrowed storage. The API around the array says which one you have.

## Optional Values

`T?` is an optional value: either a payload of type `T`, or no payload.

```camp
int? maybeCount = default;
```

The default optional value is empty. You can check whether it has a payload
with `.specified`, and read the payload through `.value` after the check:

```camp
if (maybeCount.specified)
	Console.writeLine(maybeCount.value);
```

A payload value can be formed with an initializer:

```camp
int? someCount = { 42, true };
```

Use optionals when absence is an ordinary result:

```camp
int? findFirstEven(const int[] values)
{
	for (nuint index = 0; index < values.length; index++)
		if ((values[index] % 2) == 0)
			return values[index];

	return default;
}
```

Use `thrown` when failure should participate in explicit error flow. An absent
value and an error are different stories.

## `default`, `null`, And Literals

`default` is destination-typed. It means "the default value for the type the
compiler already expects."

```camp
int count = default;          // 0
bool ready = default;         // false
int? maybeCount = default;    // empty optional
Document* document = default; // null-like pointer value
```

`null` is the null pointer literal:

```camp
Document* document = null;
void* context = null;
```

Do not use `null` as a universal empty value. Use `default` when you want the
default of a known destination type, and use optionals when "no payload" is part
of the value's meaning.

Literals are target-typed where the destination is known:

```camp
byte small = 12;
long offset = 123456789;
double ratio = 0.5;
char marker = 'R';
```

When a literal belongs to a protocol, file format, or ABI, give it an explicit
type or a named declaration. That keeps portability decisions near the boundary
that needs them.

## Choosing The Right Everyday Type

Use this table as a first-pass guide:

| Need | Prefer |
|---|---|
| Ordinary signed arithmetic | `int` or a fixed-size signed type |
| Counts, lengths, indices | `nuint` |
| Stable protocol or ABI integer | Explicit fixed-size integer |
| True/false state | `bool` |
| Counted UTF-8 text | `const char[]` |
| Zero-terminated UTF-8 string pointer | `string` |
| Writable text or byte buffer | `char[]` or `byte[]` with clear ownership |
| Borrowed sequence of values | `const T[]` or `T[]` |
| Inline fixed storage | `fixed T[N]` |
| Ordinary absence | `T?` |
| Native raw data pointer | `void*` |

The deeper chapters will add pointers, lifetimes, allocation, errors, generics,
and conversions. This chapter gives you the everyday baseline: values are
clear, views are explicit, and ownership is a contract you should be able to
find in the surrounding API.
