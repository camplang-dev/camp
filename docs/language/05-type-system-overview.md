# Type System Overview

## Type Categories

Camp has primitive scalar types, pointer types, callable types, arrays,
fixed-size arrays, optional values, structs, classes, interfaces, enums,
newtypes, generic constructed types, iterator types, and special expanded forms
such as thrown and grouped parameter carriers.

Primitive examples include `int`, `uint`, `long`, `ulong`, `nint`, `nuint`,
`float`, `double`, `bool`, `char`, `achar`, `wchar`, and `uchar`.

## Type Spelling

Type spelling is compositional:

```camp
const char[] text;
int* values;
delegate void(const char[] message) callback;
Option<int>* result;
```

Qualifiers such as `const`, `volatile`, `escaped`, `scoped`, `unscoped`, and
`constof(anchor)` apply to type forms. Target-specific type specifiers may also
apply where the selected target defines them.

## Value, Reference, And Expanded Forms

Some source types are represented by multiple lowered components. Arrays carry
an element pointer and a length. Delegates carry a target and context.
Iterators, thrown slots, and async callable forms also have expanded shapes.

Users normally write the source type. Compiler writers should use the semantic
supplements for exact component order and ABI details.

## Type Qualifiers

`const` prevents mutation through the qualified view. `volatile` marks values
whose access semantics are target-sensitive. `constof(anchor)` ties constness to
another parameter or receiver. Lifetime qualifiers describe whether a value may
escape a scope or is anchored to another value.

## Target Specifiers And Call Specifiers

A target type specifier describes a target-defined representation domain, such
as an address-space or memory-model qualifier. A call specifier describes how a
concrete callable is invoked. Targets define the valid specifier names.

```camp
extern fn _cdecl int(int value) transform;
char* _far buffer;
```

The compiler supplement documents target files. Conversion details live in
[Pointers, Qualifiers, And Conversions](07-pointers-qualifiers-and-conversions.md).

## When Types Are Nominal

Structs, classes, interfaces, enums, and newtypes introduce nominal boundaries.
Two types with similar storage are not interchangeable unless the language
defines a conversion or the programmer uses an explicit construction or cast
that is valid for that boundary.
