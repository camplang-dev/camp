# Declarations

## Declaration Forms

A Camp compilation unit may contain preprocessor directives, imports, namespace
exports, and declarations.

Common declaration forms include:

```camp
export alias ByteCount = nuint;

export struct Position
{
	int row;
	int column;
}

export int add(int left, int right)
{
	return left + right;
}
```

Type declarations introduce `struct`, `class`, `interface`, `enum`, and
`newtype` names. Member declarations introduce fields, methods, constructors,
destructors, properties after rewriting, constants, and functions. Alias
declarations introduce another source name for an existing type or declaration.

Declarations may use visibility and behavior modifiers such as `export`,
`public`, `extern`, `static`, `virtual`, `override`, `sealed`, `abstract`,
`async`, `fixed`, and `inline`.

Attributes and documentation comments attach metadata to declarations and are
covered in [Attributes And Doc Comments](20-attributes-and-doc-comments.md).

## Declaration Grammar Summary

At a high level, declarations follow this shape:

```text
attributes modifiers declaration-kind name optional-type-parameters body-or-semicolon
```

Functions and methods use parameter lists. Types use type bodies or semicolons.
Enums use value lists. Aliases use `alias Name = Target;`.

The detailed grammar is intentionally split across the language reference:
types are introduced in [Type System Overview](05-type-system-overview.md),
functions in [Functions And Callables](08-functions-and-callables.md), object
types in [Structs, Classes, And Lifecycle](11-structs-classes-and-lifecycle.md),
interfaces in [Interfaces And Dispatch](12-interfaces-and-dispatch.md), and
generics in [Generics And Type Capabilities](17-generics-and-type-capabilities.md).
