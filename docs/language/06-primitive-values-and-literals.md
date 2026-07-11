# Primitive Values And Literals

## Integer Types

Camp provides signed and unsigned integer types:

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

Exact native spelling is target-defined, but Camp source uses the primitive
names consistently.

## Natural Integer Types

`nint` and `nuint` are natural integer types. They are useful for sizes,
indices, pointer-sized arithmetic, and target-specific pointer carrier rules.
Their width comes from the selected target.

## Floating-Point Types

`float` and `double` represent floating-point values. Their native mapping is
defined by the target.

## Boolean Values

`bool` has the values `true` and `false`.

```camp
bool hasItems = count > 0;
```

## Character Types

`char`, `achar`, `wchar`, and `uchar` represent character-oriented primitive
values used by text and interop surfaces. Standard string helpers use counted
`char[]` values for UTF-8-oriented text.

## `void`, `default`, `null`, And `untyped`

`void` is the absence of a value. `default` creates the default value for a
target-typed expression. `null` is the null pointer value. `untyped` is a raw
scalar carrier used at explicit fence boundaries.

```camp
int zero = default;
byte* data = null;
```

## Literal Conversion Rules

Literals are target-typed where a surrounding declaration, parameter, return, or
assignment supplies an expected type. Numeric literals may convert to compatible
primitive or newtype targets when the value is valid for that target. Casts are
required when the conversion is not implicit.
