# Attributes And Doc Comments

Attributes attach source-level metadata to declarations, declaration children,
and a few type positions. Documentation comments are a convenient source form
that lowers to metadata attributes during binding.

This chapter covers the authoring rules users need when documenting Camp APIs
or controlling source/ABI metadata. The compiler supplement describes metadata
JSON serialization in more detail.

## Attribute Syntax

Attributes use `@name` with optional arguments.

```camp
@symbol("native_add")
extern int add(int left, int right);
```

Attributes normally appear immediately before the declaration or child they
describe. Type-position attributes appear inside a type or parameter position
where the grammar permits them:

```camp
export nint find<T: any>(
	const T[] this,
	in T value,
	@range nuint start = 0,
	nuint count = ^0,
	sizeof(T));
```

Attribute arguments may include strings, values, named arguments, arrays where
the attribute accepts them, and metadata-only symbol expressions such as
`symbolof(Name)`.

## Attribute Attachment

An attribute attaches to the immediately following declaration, declaration
child, or type position accepted by the grammar.

```camp
@symbol("DIFFICULTY_EASY")
EASY
```

```camp
export struct Packet
{
	@symbol("packet_size")
	nuint size;
}
```

When several attributes appear together, all attach to the same target:

```camp
@summary("Writes one line.")
@deprecated("Use writeLineUtf8.")
export void writeLine(const char[] value);
```

Attributes do not float across unrelated tokens. If an attribute or doc comment
is separated from the intended target by another declaration or statement, it
attaches to that intervening target or becomes invalid.

## Source And ABI Attributes

Some attributes affect emitted symbols or lowering-relevant source semantics.
The most common user-facing ABI attribute is `@symbol`.

```camp
@symbol("puts")
extern int cPuts(const char* text);
```

`@symbol` overrides the native symbol spelling for declarations such as extern
functions, exported variables, inline constants, enum types, enum members, and
other ABI-visible declarations where a native spelling is meaningful.

Use `@symbol` only at native boundaries or compatibility points. Ordinary Camp
names should remain meaningful source names and let the compiler derive the
native symbol.

Other attributes, such as `@index`, `@range`, `@awaitwith`, and `@noawait`,
participate in specific language features and are described in the chapters for
indexing/slicing or async.

## Metadata Attributes

Metadata attributes describe declarations for tools and generated API
metadata. Documentation attributes include:

| Attribute | Purpose |
|---|---|
| `@summary` | Main one- or two-sentence description. |
| `@remarks` | Longer discussion or behavioral notes. |
| `@returns` | Return value description. |
| `@example` | Example text or fenced code. |
| `@see` | Related symbol or topic. |
| `@deprecated` | Deprecation message. |

These can be written directly:

```camp
@summary("Writes one line.")
@returns("True when the value was written.")
export bool tryWriteLine(CharWriter writer, const char[] value);
```

Direct metadata attributes are useful for generated source, terse declarations,
or cases where a normal doc comment would be less readable.

## Documentation Comment Forms

Line documentation comments use `///`. Block documentation comments use
`/** ... */`.

```camp
/// Adds two values.
///
/// - left: First value.
/// - right: Second value.
/// @returns The sum.
export int add(int left, int right);
```

```camp
/** Adds two values. */
export int add(int left, int right);
```

Contiguous doc-comment lines form one block. Blank doc-comment lines are part
of the same block and usually become paragraph breaks. Any non-doc-comment
token between the block and the declaration breaks attachment.

Prefer `///` for ordinary API documentation because it keeps each line clearly
attached to the declaration.

## Lowering To Metadata Attributes

Doc comments lower to ordinary metadata attributes during binding. After
translation, the comments themselves are not a separate semantic object.

Plain text lowers to `@summary`:

```camp
/// Represents a reusable buffer.
export struct Buffer
{
}
```

For metadata purposes, this is equivalent to:

```camp
@summary("Represents a reusable buffer.")
export struct Buffer
{
}
```

Explicit doc attributes lower to attributes with the same names:

```camp
/// Searches the list.
/// @remarks The list should usually be sorted first.
/// @returns The matching index, or -1.
export nint find(...);
```

Unknown documentation attributes in doc comments are compiler errors. This
keeps metadata output predictable for tools.

## Child Documentation Targets

A doc-comment line beginning with `- name:` targets a child of the attached
declaration. The child target defaults to `@summary`.

```camp
/// Finds a value.
///
/// - T: Element type.
/// - this: Values to search.
/// - value: Value to find.
/// - start: First index to inspect.
/// @returns The matching index, or -1.
export nint find<T: any>(
	const T[] this,
	in T value,
	@range nuint start = 0,
	nuint count = ^0,
	sizeof(T));
```

The compiler attaches those child summaries to the type parameter `T`, the
receiver parameter `this`, and ordinary parameters.

Child targets are matched against the attached declaration's children. Examples
include:

- type parameters;
- function parameters;
- receiver parameters;
- fields;
- methods;
- enum values;
- interface members.

If the named child does not exist, the compiler reports an error.

Child targets can include an explicit metadata attribute:

```camp
/// Represents a state.
/// - Started: @remarks First active state.
export enum State
{
	Started
}
```

## Documenting Members And Enum Values

Members can be documented directly inside the type body:

```camp
/// A snapshot of parser counters.
export struct ParserCounters
{
	/// Stored count.
	int count;

	/// Gets the count.
	int getCount() => this.count;
}
```

Enum values can be documented either through child targets on the enum comment
or with comments on individual values:

```camp
/// Example mode values.
/// - First: First mode.
/// - Second: Second mode.
export enum Mode
{
	First = 4,
	Second,

	/// Combined mode.
	Combined = 12
}
```

Use child targets when a compact list is clearer. Use per-value comments when
some enum values need longer explanation.

## Symbol Links

Inside ordinary doc-comment text, `[Symbol]` creates a documentation link.

```camp
/// Converts a [UserId] to text.
export const char[] format(UserId id);
```

The compiler resolves the symbol and emits a metadata symbol reference.
Resolution uses the source visibility and lookup rules available to the
declaration being documented.

If the symbol cannot be resolved, the compiler reports an error. Use code spans
when text should remain literal:

```camp
/// Writes the literal text `[UserId]`.
export void writeLiteral();
```

## `symbolof`

`symbolof(Name)` is valid only inside metadata attribute arguments.

```camp
@summary("Converts %s to text.", symbols: [symbolof(UserId)])
export const char[] format(UserId id);
```

It is not a runtime expression:

```camp
auto value = symbolof(format); // ERROR
```

Metadata consumers receive opaque symbol references and should not infer
language semantics by parsing those ids.

## Literal Regions

Inline code spans and fenced code blocks are literal regions. Symbol links,
child targets, doc attributes, and `%s` placeholders are not parsed inside
literal regions.

````camp
/// Uses `[NotALink]`, `@summary`, `- value:`, and `%s`.
/// @example
/// ```camp
/// Console.writeLine("ready");
/// ```
export void showExample();
````

Use backticks whenever documentation needs to show syntax that would otherwise
look like a doc-comment command.

## Examples

`@example` is ordinary metadata content. The most readable form is a fenced
Camp block:

````camp
/// Parses a port number.
/// @example
/// ```camp
/// int port = parsePort("443", catch error);
/// ```
export int parsePort(const char[] text, thrown ParseError error);
````

Examples should be short and should use meaningful names from the API domain.
Avoid placeholder-heavy examples that hide what the declaration is for.

## Deprecation

Use `@deprecated` with a replacement or reason:

```camp
/// Writes one line.
/// @deprecated Use [writeLineUtf8] for new code.
export void writeLine(const char[] value);
```

Deprecation is metadata. It does not remove the declaration from overload
resolution or ABI output. Tooling can surface the message to users.

## Attribute Arguments And Source Text

Default values and many attribute arguments are preserved as source-level text
in metadata. They are not a substitute for executable code.

```camp
export void resize(nuint capacity = 64);
```

Metadata consumers should treat source strings as source contracts. They should
not assume a particular lowered C spelling unless the metadata provides an
explicit `symbol`.

## Metadata JSON Relationship

Doc comments and metadata attributes are serialized into metadata JSON when the
compiler is asked to emit metadata. The compiler supplement documents the JSON
shape.

Language users mainly need these rules:

- documentation attaches to source declarations and children;
- plain text becomes `summary`;
- recognized doc attributes become metadata attributes;
- symbol links resolve during binding;
- literal regions are left alone;
- metadata describes source-level declarations, not generated helper internals.

## Authoring Conventions

Write summaries as direct declarative sentences:

```camp
/// Represents a platform file handle.
```

Document ownership, allocation, lifetime, and thrown behavior when they affect
callers:

```camp
/// Opens a file.
///
/// @remarks The returned handle must be closed with [FileHandle.close].
/// @returns The opened handle.
export static FileHandle open(
	string path,
	FileAccess access,
	FileMode mode,
	FileOptions options = default,
	thrown IoError error);
```

Document parameters with child targets when the names alone are not enough:

```camp
/// Copies values.
///
/// - this: Source values.
/// - destination: Destination storage.
export void copyTo<T: copyable>(const T[] this, T[] destination, sizeof(T));
```

Prefer cross-references to repeating another chapter's full semantics. For
example, a function that takes `within allocator` can link or mention allocator
ownership, but it does not need to re-explain the whole lifetime model.
