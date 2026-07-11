# Arrays, Slices, Optionals, And Strings

## Array Values

`T[]` is a counted array view. Source code treats it as one value, while the
compiler represents it as an expanded carrier with an element pointer and a
length.

```camp
export nuint countBytes(const byte[] data)
{
	return data.length;
}
```

An array value does not imply ownership of its elements. It is a view over
storage located elsewhere unless the API explicitly says the array owns or must
free that storage.

```camp
export struct ByteView
{
	const byte[] data;
}
```

`ByteView` stores a counted view. It does not automatically own `data.elements`.
Its lifetime contract must explain how long the pointed-to storage remains
valid.

Array components are visible through ordinary member access:

```camp
nuint count = data.length;
byte* start = data.elements;
```

Those components are part of the source model, but API authors should avoid
depending on lowered ABI component order unless they are deliberately writing a
native boundary.

## Array Carrier Versus Element Type

Target type specifiers can apply to the array carrier or to its element type.
These are different:

```camp
(byte* _near)[] pointersInDefaultArray;
byte[] _near nearArrayCarrier;
```

The first is a default-domain array carrier whose elements are near pointers.
The second is a near-domain array carrier whose `.elements` and `.length`
components are in the target-defined near domain. This distinction matters for
conversions, ABI, and native interop.

Element conversions do not tunnel through arrays. If you need an array whose
elements have a different type, reconstruct the array.

## Fixed-Size Arrays

`T[N]` is fixed-size storage.

```camp
byte[16] digest;
```

Fixed-size arrays store elements inline. They are useful for native records,
hashes, buffers, and fields whose storage is part of the containing value.

```camp
export struct PacketHeader
{
	fixed byte[4] magic;
	uint length;
}
```

Use `fixed` fields when the storage is embedded. Use `T[]` when the value is a
view over separately located storage.

Fixed-size arrays can appear in pointer, callable, and aggregate contexts, but
copying and assignment must respect the element type and storage shape.

## Array Literals

Array literals use brackets and are target-typed by context.

```camp
int[] values = [1, 2, 3];
```

The expected type determines element type and carrier shape. For fixed-size
storage, the element count must satisfy the destination. Nested initializers
follow the nested target type.

```camp
int[3] scores = { 10, 20, 30 };
```

Array literals are not general untyped bags of values. If the destination type
is not known, supply one with a declaration or cast-like construction form that
the language permits.

## Indexing

Indexing uses `value[index]`. Arrays and strings support index-like operations
through the standard array/string protocol. A user type can provide indexer
surface through methods or properties accepted by the language.

```camp
byte first = data[0];
```

Index values are numeric. Bounds behavior belongs to the API or generated
helper used for the target surface. When writing public APIs, state whether an
indexing operation clamps, reports errors, or requires callers to validate
bounds.

## Slices And Ranges

Slice syntax uses range expressions.

```camp
const char[] prefix = text[0..4];
const char[] suffix = text[^4..];
```

Range boundaries can be omitted. The from-end operator `^` counts from the end
where the target expression supports it. Boundary computation happens before
the final count is computed.

Slice syntax is method/property based. Arrays and strings use both indexing and
slicing helper surfaces. Other types can provide range-aware methods or
property indexers when the source shape matches the slice rules.

There are two important slice styles:

- direct comma form, where the call-like surface receives start and count;
- boundary form, where a range expression describes start and end boundaries.

Use the form that matches the API's semantics. Do not assume every range-aware
method clamps boundaries; the method's contract decides.

## Optional Values

`T?` is an optional value. It represents either a `T` payload or no payload.

```camp
int? maybeCount = default;
```

Optionals are useful when absence is an ordinary result rather than an error.
Use `thrown` when the absence is an error path that should participate in error
propagation.

Optional conversions depend on the optional shape and payload compatibility.
`T?` does not permit arbitrary reinterpretation of `T`, and casts do not tunnel
through optional payloads unless the language defines that conversion.

## String Literals

String literals are counted text values and are commonly used as `const char[]`.

```camp
const char[] greeting = "hello";
```

The compiler manages literal storage. The resulting view is read-only. Do not
attempt to mutate string literal storage by casting away constness.

String literals can participate in target typing. A fixed character array
destination may receive a string literal when the destination has a compatible
shape.

## Counted Text

Camp's core string surface is counted text, not sentinel-only C strings. A
`const char[]` carries a pointer and a byte length. This is different from a
native `const char*` string where length is discovered by a terminator.

Use counted text for Camp APIs. Use `char*` or `const char*` for native APIs
that require C string conventions.

```camp
@symbol("puts")
extern int cPuts(const char* text);
```

Conversion between counted text and native C strings is a library or interop
operation, not a hidden language conversion.

## Strings, UTF-8, And Character Helpers

The standard library provides helpers for UTF-8-oriented counted text, such as
length, search, comparison, and case-related operations. The language reference
does not list every helper so ordinary library API churn does not force a
language-doc update. Use generated metadata or `lib/std/src` when exact
signatures matter.

`char[]` length is a byte count unless the helper explicitly computes logical
Unicode code point count. Be explicit about which count an API expects.

## Arrays In Generic Code

Generic arrays need element layout. A generic API that accepts `T[]` may need
`sizeof(T)` if it indexes, allocates, copies, or computes element addresses.

```camp
export nuint countItems<T: any>(T[] values, sizeof(T))
{
	return values.length;
}
```

Even when a function only reads `.length`, the compiler may require capability
transport so the expanded array type has a valid representation in erased
generic code.

## Arrays, Lifetimes, And Ownership

An array view's lifetime is tied to the lifetime of its element storage.
Returning or storing an array view must preserve that relationship.

```camp
export const byte[] viewData(unscoped(this) const Buffer* this)
{
	return this.data;
}
```

If an API returns newly allocated array storage, it should say how the caller
owns and deletes that storage. If it returns a borrowed view, it should anchor
the lifetime to a parameter, receiver, or owning object.

## Standard Array And String Helpers

The standard library includes common helpers for arrays and strings. The
language docs mention only the small surface needed for examples; consult
metadata or source for exact library APIs.

Useful categories include:

- counted text search and comparison;
- UTF-8 byte/code point helpers;
- array span/view helpers;
- string builders and owned string values;
- stream/text adapters.

Use the standard library when possible rather than hand-rolling pointer
arithmetic in application code.
