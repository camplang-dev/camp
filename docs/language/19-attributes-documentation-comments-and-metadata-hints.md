# Attributes, Documentation Comments, And Metadata Hints

Attributes are Camp's way of attaching small pieces of source metadata to a
declaration, parameter, type position, or declaration child. You have already
seen the attributes that change how a feature behaves: `@symbol` for native
names, `@index` and `@range` for indexing and slicing, and `@awaitwith` and
`@noawait` for async bodies.

This chapter fills in the metadata side: documentation comments, direct
documentation attributes, target-availability hints such as `@notsupported`,
and the compact table you can use when you need to remember where each
attribute belongs.

## Attribute Syntax

An attribute begins with `@` and appears immediately before the thing it
describes, or inside the type/parameter position where the grammar permits it.

```camp
@symbol("native_add")
extern int add(int left, int right);
```

```camp
export nint find<T: any>(
	const T[] this,
	in T value,
	@range nuint start = 0,
	nuint count = ^0,
	sizeof(T));
```

Several attributes can attach to the same declaration:

```camp
@symbol("atomicExchangePtr")
@summary("Atomically stores a pointer and returns the previous value.")
export extern void* atomicExchange(overload void** dest, void* value);
```

Attribute arguments may include literal values, named arguments where the
attribute accepts them, arrays where the attribute accepts them, and
metadata-only symbol references through `symbolof(...)`.

## Documentation Comments

Line documentation comments use `///`. Block documentation comments use
`/** ... */`.

```camp
export newtype FileHandle: nint
{
	/// Opens a file.
	///
	/// - path: Zero-terminated path to open.
	/// - access: Requested access mode.
	/// - mode: Creation/opening behavior.
	/// @returns The opened handle.
	export static FileHandle open(
		string path,
		FileAccess access,
		FileMode mode,
		FileOptions options = default,
		thrown IoError error);
}
```

Plain text becomes the declaration summary. Blank documentation-comment lines
create paragraph breaks. The comment attaches to the next declaration or
declaration child; an ordinary token between the comment and the declaration
breaks the attachment.

The same documentation can be written directly as attributes:

```camp
export newtype FileHandle: nint
{
	@summary("Opens a file.")
	@returns("The opened handle.")
	export static FileHandle open(
		@summary("Zero-terminated path to open.") string path,
		@summary("Requested access mode.") FileAccess access,
		@summary("Creation/opening behavior.") FileMode mode,
		FileOptions options = default,
		thrown IoError error);
}
```

Direct attributes are useful for generated source and very small declarations.
For hand-written APIs, `///` is usually easier to read.

## Documentation Attributes

Recognized documentation attributes are:

| Attribute | Use |
|---|---|
| `@summary` | Main one- or two-sentence description |
| `@remarks` | Longer notes, contracts, ownership, or behavior |
| `@returns` | Return value description |
| `@example` | Example text, usually a fenced Camp block |
| `@see` | Related symbol or topic |
| `@deprecated` | Deprecation message |

Inside a doc comment, write them as doc commands:

````camp
/// Starts a repeating timer.
///
/// @remarks The callback runs until [stopTimer] is called.
/// - intervalMs: The number of milliseconds between ticks.
/// - callback: The callback invoked for each tick.
/// @returns A non-default handle when the timer starts successfully.
/// @example
/// ```camp
/// TimerHandle handle = startTimer(1000, tick);
/// finally stopTimer(handle);
/// ```
export TimerHandle startTimer(
	nuint intervalMs,
	escaped delegate void(TimerHandle handle) callback);
````

`@deprecated` is metadata. It does not remove the declaration from source
lookup or the ABI:

```camp
/// Writes one line.
/// @deprecated Use [Console.writeLine] for new code.
export void writeLineUtf8(const char[] text);
```

Tooling can warn at use sites and show the replacement.

## Child Targets

A documentation line beginning with `- name:` documents a child of the attached
declaration.

```camp
/// Finds a value.
///
/// - T: Element type.
/// - this: Values to search.
/// - value: Value to find.
/// - start: First index to inspect.
/// - count: Number of values to inspect.
/// @returns The matching index, or -1.
export nint find<T: any>(
	const T[] this,
	in T value,
	@range nuint start = 0,
	nuint count = ^0,
	sizeof(T));
```

Child targets may name type parameters, receiver parameters, ordinary
parameters, fields, methods, enum values, interface members, and similar
source-visible children. If the child does not exist, the compiler reports an
error.

A child target can also carry a specific documentation attribute:

```camp
/// Describes parser states.
/// - BODY: @remarks The parser has accepted the header and is reading content.
export enum ParserState
{
	START,
	BODY,
	DONE
}
```

## Links And `symbolof`

Inside doc-comment text, `[Symbol]` creates a documentation link.

```camp
/// Closes a handle returned by [FileHandle.open].
export void close(FileHandle this);
```

The compiler resolves the symbol using ordinary source visibility from the
documented declaration. If the symbol cannot be resolved, the compiler reports
an error.

When writing metadata attributes directly, use `symbolof(...)`:

```camp
@summary("Closes a handle returned by %s.", symbols: [symbolof(FileHandle.open)])
export void close(FileHandle this);
```

`symbolof(...)` is valid only inside metadata attribute arguments. It is not a
runtime reflection expression.

Use backticks when text should stay literal:

```camp
/// Writes the literal text `[FileHandle.open]`.
export void writeLiteral();
```

Inline code spans and fenced code blocks are literal regions. Links, child
targets, doc commands, and `%s` placeholders are not parsed inside them.

## Examples In Documentation

Use `@example` when a declaration benefits from a small call-site sketch.

````camp
/// Copies counted text into a new zero-terminated string.
///
/// @example
/// ```camp
/// string name = input.copyString(within allocator) finally delete;
/// ```
export escaped string copyString(const char[] this, within allocator);
````

Examples should name the domain they are demonstrating. They do not need to be
complete programs, but they should avoid suggesting that invented APIs are part
of the standard library unless that is actually true.

## Target Availability With `@notsupported`

`@notsupported` marks a function or method as unavailable for the active target
while preserving it in source/API metadata.

```camp
#if NO_TIMERS
@notsupported("The current target does not support timers.")
#endif
export extern TimerHandle startTimer(
	nuint intervalMs,
	escaped delegate void(TimerHandle handle) callback);
```

The optional argument is a string reason. Calls to a not-supported function are
diagnosed unless the caller is also marked not supported. This lets target
specific standard-library files keep one source shape while clearly explaining
why a declaration cannot be used on a particular target.

`@notsupported` applies only to functions and methods. It is not valid on
fields, parameters, constructors, or destructors.

## Metadata Attributes And Generated Output

Documentation comments lower to metadata attributes before API or metadata
output is written. After translation, the compiler no longer treats the comment
text as a separate semantic object.

For example:

```camp
/// Adds one to [Number].
/// - value: Input value.
/// @returns The incremented value.
export extern int add(int value);
```

The generated Camp API surface can preserve that information as attributes:

```camp
@summary("Adds one to %s.", symbols: [symbolof(Number)])
@returns("The incremented value.")
export extern int add(@summary("Input value.") int value);
```

Generated C headers currently describe the ABI, not API prose. Use Camp API
output or metadata JSON when tools need documentation, summaries, child docs,
symbol links, or deprecation messages.

## Attribute Summary

| Attribute | Applies to | Short explanation |
|---|---|---|
| `@symbol("Name")` | ABI-visible declarations where native spelling is meaningful; enum types and members; exported/static fields and inline constants; functions and methods | Overrides the emitted/imported native symbol without changing Camp source lookup |
| `@index` | Index-like parameters | Enables index-aware syntax and diagnostics, including from-end indexes where length is visible |
| `@range` | First parameter in an `index, count` pair | Enables range boundary syntax such as `start..end` for slice-like APIs |
| `@awaitwith` | One ordinary runtime parameter of a concrete async body | Selects the resumer used after `await` suspension |
| `@noawait` | Concrete async definitions with Camp bodies | Declares that the async body cannot suspend and may not contain `await` |
| `@notsupported("reason")` | Functions and methods | Marks the callable unavailable on the active target while preserving source/API metadata |
| `@summary("text")` | Declarations and declaration children | Main documentation summary; plain doc-comment text lowers to this |
| `@remarks("text")` | Declarations and declaration children | Longer documentation notes, contracts, ownership, or behavior |
| `@returns("text")` | Functions, methods, callable declarations, and relevant callable children | Describes the returned value |
| `@example("text")` | Declarations and declaration children | Documentation example text, commonly a fenced code block in doc comments |
| `@see("text")` | Declarations and declaration children | Related-symbol or related-topic documentation metadata |
| `@deprecated("message")` | Declarations and declaration children | Marks a source API as deprecated for tooling; does not remove it from lookup or ABI output |

One additional attribute name, `@createWithAllocator`, is recognized by current
compiler support code for specialized generated or expanded forms. It is not an
ordinary API-authoring tool; treat it as compiler/semantic-supplement territory
unless a future user-facing feature documents it directly.

## Where To Look Back

For the feature-specific attributes already introduced:

- `@symbol` belongs with native names and exported ABI design.
- `@index` and `@range` belong with arrays, slicing, and indexer-like APIs.
- `@awaitwith` and `@noawait` belong with async bodies and resumers.
- `@notsupported` belongs with target-conditioned APIs and standard-library
  portability.

For documentation, the useful rule is simple: write comments for the source API
you want callers to understand, and let metadata carry that source API to tools.
