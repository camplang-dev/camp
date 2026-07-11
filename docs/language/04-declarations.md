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

## Type Declarations

Type declarations introduce nominal names:

```camp
export struct Span
{
	const byte* data;
	nuint length;
}

export class Stream;

export interface Reader
{
	nuint read(byte[] buffer);
}
```

`struct` and `class` bodies contain fields and members. `interface` bodies
contain contract entries. `enum` bodies contain values. `newtype` declarations
wrap an underlying value, callable, or expanded shape in a nominal type.

Type declarations can have generic parameters, base types, implemented
interfaces, visibility, and target-specific metadata. A semicolon form declares
the surface without a body where the language permits it, such as extern or
forward-style declarations.

## Member Declarations

Members can be fields, methods, constructors, destructors, static members,
inline constants, and implementation methods for interfaces. A member's
containing type contributes to name lookup and receiver binding.

```camp
export class Buffer
{
	byte* data;
	nuint length;

	Buffer(nuint capacity);
	~Buffer();
	nuint getLength(const this);
}
```

Constructors use the type name. Destructors use `~TypeName`. Methods can be
ordinary, static, virtual, abstract, override, async, or interface-marked.

## Modifiers

Modifiers are not cosmetic; they change binding and emitted surface.

| Modifier | Typical meaning |
|---|---|
| `export` | Part of exported API/ABI surface. |
| `public` | Public source visibility. |
| `extern` | Implemented outside Camp. |
| `static` | Associated with type, not instance. |
| `virtual` | Dispatch can be overridden. |
| `override` | Implements a virtual base member. |
| `sealed` | Closes inheritance or overriding. |
| `abstract` | Declares required derived implementation. |
| `async` | Callback-shaped async callable. |
| `fixed` | Fixed storage or fixed type surface. |
| `inline` | Inline constant/value surface. |

Invalid modifier combinations are compiler errors. For example, a bodyless
ordinary method and an abstract method mean different things; an extern
declaration's body rules differ from a Camp-defined declaration.

## Declaration Children

Many declarations have children that can be named by metadata, doc comments, or
diagnostics: type parameters, parameters, receiver parameters, fields, enum
values, and methods. Child names are part of the authoring surface, especially
when documentation comments use `- name:` targets or when `constof(anchor)` and
lifetime anchors name parameters.

## Declaration Order And Generated Declarations

Source declaration order affects layout, overload grouping, metadata order, and
some generated helper names. The compiler may generate declarations for
constructors, destructors, interface accessors, vtables, thunks, async frames,
and iterator state. Those generated declarations support the source surface;
they are not usually declarations users write directly.

## File-Level Declarations

File-level declarations include imports, namespace export declarations, aliases,
types, functions, variables, and inline constants.

```camp
using Std;

export as Example::Images;

export inline uint DefaultWidth = 640;

export struct Size
{
	uint width;
	uint height;
}

export Size makeDefaultSize()
{
	return { .width = DefaultWidth, .height = 480 };
}
```

File-level declarations form the top of source lookup for that file and
contribute to module output according to visibility.

## Variables And Constants

Camp distinguishes storage from inline constants.

```camp
export int GlobalCount = 0;
export inline uint MaxPlayers = 9;
```

A variable has storage, address, and assignment rules according to its type and
modifiers. An `inline` constant has a compile-time value and no ordinary
storage. `const` affects mutability of a storage view; it does not by itself
make a declaration an inline constant.

Inline constants are described in
[Enums, Newtypes, And Inline Constants](13-enums-newtypes-and-inline-constants.md).

## Function-Like Declarations

Functions, methods, constructors, destructors, generators, async functions, and
interface methods all share a function-like signature model:

```camp
export int add(int left, int right);
export async int loadCount(const char[] path, thrown IoError error);
struct iter int range(int first, int last);
```

A function-like declaration can have:

- type parameters;
- ordinary parameters;
- receiver parameters;
- `out`, `thrown`, `within`, and capability parameters;
- default arguments;
- call specs;
- callable newtype ascription;
- body or semicolon according to declaration kind and modifiers.

The callable shape is part of overload resolution, metadata, ABI emission, and
compatibility checks.

## Bodies And Semicolons

A body provides a Camp implementation:

```camp
export int add(int left, int right)
{
	return left + right;
}
```

A semicolon declares a surface without a Camp body where that is valid:

```camp
export extern int nativeAdd(int left, int right);

export interface Reader
{
	nuint read(byte[] buffer);
}
```

Whether a semicolon is valid depends on the declaration kind. An `extern`
function has no Camp body. An interface method declares a contract entry. An
abstract method declares a required override. An ordinary non-extern function
normally needs a body unless it is part of a valid bodyless surface.

## Generic Declarations

Types and functions can declare generic parameters:

```camp
export class List<T: copyable>
{
}

export void copyTo<T: copyable>(const T[] source, T[] destination, sizeof(T));
```

Generic constraints are declaration-time contracts. They determine which
operations are available inside the generic body and which arguments can be
supplied by callers.

Special capability parameters such as `sizeof(T)`, `typenameof(T)`, and
`vtableof(T: Interface)` are declarations too. They make runtime support for
erased generic operations explicit.

## Receiver Declarations

Instance methods have a receiver. The receiver can be implicit or explicit:

```camp
struct Counter
{
	int value;

	void increment()
	{
		this.value += 1;
	}

	int getValue(const this)
	{
		return this.value;
	}
}
```

An explicit receiver can state constness, lifetime, or a receiver type needed
for an extension-style declaration. Receiver qualifiers are part of the method
contract and participate in method references and callable compatibility.

## Interface Implementation Markers

A method can explicitly implement an interface slot:

```camp
class ConsoleWriter: Writer
{
	void write(overload const char[] value): Writer
	{
		Console.writeLine(value);
	}
}
```

The marker is a declaration-level contract. Structural name/signature matching
alone does not fill an interface slot. Selector forms such as
`: Writer.writeString` can bind a differently named method to a specific slot.

Interface implementation details are in
[Interfaces And Dispatch](12-interfaces-and-dispatch.md).

## Declaration Names And Symbols

The declaration name is the Camp source name. The emitted symbol is the native
or metadata-facing spelling derived from that source name, unless `@symbol`
overrides it.

```camp
@symbol("SetWindowTextW")
extern bool setWindowText(WindowHandle handle, wstring text);
```

Callers use `setWindowText` in Camp source. The generated native reference uses
the symbol override.

## Invalid Combinations

The compiler rejects declaration combinations that would describe two different
contracts at once.

Examples include:

- a value newtype with fields or a destructor;
- an extern function with a Camp body;
- a constructor declared on an interface value as though the interface itself
  were directly instantiable;
- a virtual static method;
- an iterator generator with `out` parameters;
- an inline constant without an initializer;
- a fixed-size array used as a normal return type.

Most diagnostics are tied to the declaration because the declaration is where
the API contract is written.

## Generated Surface Is Not Extra Source Authority

Camp generates helper surfaces for lifecycle, interfaces, async, iterators,
properties, expanded values, and ABI output. Those helpers support the source
declaration; they do not replace it as the canonical API.

For example, an interface class accessor may be generated from:

```camp
export class Counter: IValue
{
	int value(): IValue
	{
		return 1;
	}
}
```

Users should document and reason from the source declaration. Compiler writers
can use semantic supplements for exact helper naming and pass-level lowering.
