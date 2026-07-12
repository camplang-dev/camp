# Primitive Values And Literals

Primitive types are the indivisible built-in types of Camp source. They appear
in signatures, storage declarations, generic constraints, inline constants,
metadata, and native interop boundaries.

## Primitive Type Table

| Type | Meaning |
|---|---|
| `sbyte` | 8-bit signed integer. |
| `byte` | 8-bit unsigned integer. |
| `short` | 16-bit signed integer. |
| `ushort` | 16-bit unsigned integer. |
| `int` | 32-bit signed integer. |
| `uint` | 32-bit unsigned integer. |
| `long` | 64-bit signed integer. |
| `ulong` | 64-bit unsigned integer. |
| `nint` | Signed natural integer, pointer-sized but not smaller than 32 bits. |
| `nuint` | Unsigned natural integer, pointer-sized but not smaller than 32 bits. |
| `float` | 32-bit floating-point value. |
| `double` | 64-bit floating-point value. |
| `bool` | Boolean truth value. |
| `char` | UTF-8 code unit. |
| `wchar` | UTF-16 code unit. |
| `uchar` | Unicode code point. |
| `achar` | ASCII or system-code-page character. |
| `string` | Zero-terminated UTF-8 string pointer to const data. |
| `wstring` | Zero-terminated UTF-16 string pointer to const data. |
| `astring` | Zero-terminated ASCII/system-code-page string pointer to const data. |
| `untyped` | Raw carrier used at explicit representation boundaries. |
| `void` | No value. |

`void` participates in signatures and callable types even though it is not a
normal storable scalar value.

## Integer Types

Camp provides fixed-size signed and unsigned integer types:

```camp
sbyte signedByte;
byte unsignedByte;
short smallSigned;
ushort smallUnsigned;
int signedValue;
uint unsignedValue;
long largeSigned;
ulong largeUnsigned;
```

Use signed types when negative values are part of the domain. Use unsigned
types for bit masks, sizes, counts, and values whose domain is naturally
non-negative.

```camp
int delta = -4;
uint flags = 0x12;
nuint length = buffer.length;
```

Fixed-size integer names have stable source meanings. Their C spelling is
target-specific, but the Camp type contract is not.

## Natural Integer Types

`nint` and `nuint` are natural integer types. They follow the selected target's
pointer width, but are not smaller than 32 bits.

Use them for:

- array lengths and indices;
- pointer-sized native results;
- handles represented as native integers;
- target-defined pointer/integer carrier rules.

```camp
nuint count = values.length;
nint nativeResult = callNative();
```

Do not use `nint` and `nuint` merely because the exact size feels unimportant.
For serialized data, file formats, wire protocols, and stable ABIs, choose the
fixed-size type that the protocol requires.

## Floating-Point Types

`float` and `double` are scalar floating-point types.

```camp
float ratio = 0.5;
double distance = 12.75;
```

Use `double` for ordinary high-precision numeric work and `float` where the API
or storage format requires a 32-bit floating-point value.

## Boolean Values

`bool` has the values `true` and `false`.

```camp
bool hasItems = count > 0;
bool finished = false;
```

Boolean operators and statement conditions require `bool`. Integers, pointers,
enums, and newtypes do not implicitly become truth values.

```camp
if (count != 0)
	process();

if (buffer != null)
	use(buffer);
```

## Character Types

Camp distinguishes code units and code points:

```camp
char utf8Unit = 'A';
wchar utf16Unit = 'A';
uchar codePoint = 0x41;
achar nativeChar = 'A';
```

`char` is a UTF-8 code unit. `wchar` is a UTF-16 code unit. `uchar` is a
Unicode scalar/code-point value. `achar` is for ASCII or system-code-page
surfaces.

The intended widening chain is:

```text
achar -> char -> wchar -> uchar
```

Narrowing in the opposite direction is explicit. These primitive conversions
are representation-level conversions, not full text transcoding policies.
Text transcoding belongs in standard-library helpers.

## Primitive String Types

`string`, `wstring`, and `astring` are primitive zero-terminated string pointer
types:

| Type | Element family | Counted view |
|---|---|---|
| `string` | `char` / UTF-8 | `const char[]` |
| `wstring` | `wchar` / UTF-16 | `const wchar[]` |
| `astring` | `achar` / ASCII or system code page | `const achar[]` |

Primitive string types describe representation, not ownership. A borrowed
string pointer and an allocated string pointer can have the same primitive
type; the API contract and lifetime annotations describe ownership and
validity.

Use counted arrays when a length must be carried explicitly:

```camp
const char[] text = "ready";
```

Use primitive string types for zero-terminated interop or compact string
pointers:

```camp
string path = "readme.txt";
```

## Numeric Literals

Numeric literals are target-typed where a destination type exists:

```camp
byte small = 12;
long offset = 123456789;
double ratio = 0.5;
```

Without a stronger target type, integer literals are inferred as `int` and
floating-point literals as `double`.

Hexadecimal integer literals are useful for masks and native constants:

```camp
uint messageId = 0x000E;
```

The compiler checks that a literal value fits the target type when a target is
known. Numeric literal conversion to a value newtype is explicit unless a
specific rule permits the target context.

```camp
newtype UserId: uint;

UserId id = (UserId)42;
```

## String Literals

String literals are constant data.

```camp
auto inferred = "ready";       // string
string terminated = "ready";
const char[] counted = "ready";
const char* raw = "ready";
```

A string literal cannot implicitly target mutable string storage:

```camp
char[] mutableView = "ready"; // ERROR
char* mutableRaw = "ready";   // ERROR
```

For `wstring`, `astring`, fixed character arrays, or counted character arrays,
the destination type controls the representation and validation.

## Character Literals

Character literals are single-character literal values:

```camp
char newline = '\n';
uchar smile = 0x1F600;
```

When the literal has a destination type, the compiler checks compatibility with
that character type. Use explicit casts or standard helpers when crossing
encoding boundaries would otherwise hide a narrowing operation.

## `void`

`void` means no value.

```camp
void log(const char[] message)
{
	Console.writeLine(message);
}
```

`void` appears in function return types, callable types, delegates, async
completion shapes, and generic API boundaries where a callable produces no
success value. It is not a normal field or local value type.

## `default`

`default` is destination-typed. It means the default value of the expected type.

```camp
int count = default;
Position position = default;
int? maybeCount = default;
delegate void() callback = default;
```

For numeric and boolean primitives, the default is the all-zero value. For
pointer-like values, it is the null-like value. For optionals, it is the empty
optional. For expanded forms, it is the all-default component shape.

When no destination type is available, `default` is invalid because the
compiler cannot know which default value to form.

## `null`

`null` is the null pointer literal.

```camp
Document* document = null;
void* context = null;
```

It is not a universal zero value. Use `default` for destination-typed default
values and enum success values for status/error conventions.

Optional absence should normally be represented by the optional's default empty
value, not by overloading a non-pointer payload with `null`.

## `untyped`

`untyped` is a raw carrier for explicit representation boundaries. It is useful
when code intentionally erases the distinction among low-level carriers before
reconstructing a checked type.

Prefer more specific carriers when they describe the operation:

- `void*` for raw data pointers;
- `fn*` for raw function pointers;
- `nint`/`nuint` for native integer carriers.

`untyped` is not a replacement for generic `T`, variants, optionals, or
interfaces. Use it only where the operation is truly a raw ABI escape.

## Primitive Defaults And Status Values

Many later features depend on default values. The `thrown` convention uses the
default value of the error type as success. Status enums therefore usually make
the success value explicit:

```camp
enum IoError
{
	OK = 0,
	NOT_FOUND
}
```

This is not a special enum rule. It is ordinary default-value discipline.

## Literal Portability

Keep target assumptions near the boundary that needs them. A literal used for a
native flag, file format, protocol value, or ABI constant should have an
explicit destination type:

```camp
export inline uint WindowGetTextLength = 0x000E;
```

For ordinary local arithmetic, let target typing do the simple work:

```camp
int retries = 3;
double scale = 1.5;
```

When in doubt, prefer a named inline constant or enum value over repeating a
magic literal in several places.
