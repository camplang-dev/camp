# Arrays, Slices, Optionals, And Strings

## Array Values

`T[]` is a counted array view. Source code treats it as one value, while the
compiler lowers it to carrier components.

```camp
export nuint countBytes(const byte[] data)
{
	return data.length;
}
```

Array values carry an element pointer and length. Do not assume an array value
owns its elements unless the API says so.

## Fixed-Size Arrays

`T[N]` is fixed-size storage.

```camp
byte[16] digest;
```

Fixed-size arrays are useful for inline storage in structs, buffers, and native
interop shapes.

## Array Literals

Array literals use brackets and are target-typed by context.

```camp
int[] values = [1, 2, 3];
```

Nested array and fixed-array initializers follow the target type's shape.

## Slices And Ranges

Slice syntax uses range expressions.

```camp
const char[] prefix = text[0..4];
const char[] suffix = text[^4..];
```

The from-end operator `^` counts from the end where the target expression
supports it.

## Optional Values

`T?` is an optional value. It represents either a `T` value or no value.

```camp
int? maybeCount = default;
```

Use explicit checks or APIs that understand optionals before consuming the
payload.

## String Literals

String literals are counted text values and are commonly used as `const char[]`.

```camp
const char[] greeting = "hello";
```

The compiler manages literal storage. Mutation through string literal views is
not allowed.

## Counted Text

Camp's core string surface is based on counted arrays rather than sentinel-only
C strings. Native interop may still use `char*` where required by external APIs.

## Standard Array And String Helpers

The standard library includes common helpers for arrays and strings. The
language docs mention only the small surface needed for examples; consult
metadata or source for exact library APIs.
