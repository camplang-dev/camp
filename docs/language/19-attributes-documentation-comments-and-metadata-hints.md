+++
nav_title = "19. Attributes And Metadata"
+++

# Attributes, Documentation Comments, And Metadata Hints

Attributes are Camp's way of attaching small pieces of source metadata to a
declaration, parameter, type position, or declaration child. You have already
seen the attributes that change how a feature behaves: `@symbol` for native
names, `@index` and `@range` for indexing and slicing, and `@awaitwith` and
`@noawait` for async bodies.

This chapter fills in the metadata side: documentation comments, direct
documentation attributes, availability requirements such as `@require`, and the
compact table you can use when you need to remember where each attribute
belongs.

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
| `@overload` | Summary for an overload family in generated docs |
| `@category` | Category name for grouping a top-level declaration in generated docs |

If most declarations in a file belong to the same documentation category, write
`@category("Name");` as a standalone file metadata attribute near the top of the
file:

```camp
namespace Std;

@category("I/O");

public class File
{
}

public class Directory
{
}
```

The semicolon matters: it makes the category a file default instead of attaching
the attribute to the next declaration. A declaration can still use its own
`@category(...)` when it belongs somewhere else.

## Availability Requirements

Some declarations exist only when a selected target or build configuration
supports the feature they need. Use `@require(...)` to state that requirement:

```camp
@require(SUPPORTS_FILES)
public static void FileSystem.deleteFile(string path, thrown IoError error);
```

The expression inside `@require` uses configuration flags such as
`OS_WIN32`, `SUBSYSTEM_POSIX`, and `SUPPORTS_FILES`. These names are not
ordinary variables. In executable code, query them with `configured(...)`:

```camp
void logPlatform()
{
	if (configured(OS_WIN32))
		Console.writeLine("Windows");
	else if (configured(SUBSYSTEM_POSIX))
		Console.writeLine("POSIX");
}
```

When most declarations in a file share the same requirement, write it as a file
metadata attribute:

```camp
namespace Std;

@require(SUPPORTS_FILES);

public enum FileAccess
{
	READ,
	WRITE
}
```

The semicolon again matters. `@require(SUPPORTS_FILES);` is a file-level default
for top-level declarations in that file. A top-level declaration with its own
`@require` uses that declaration requirement instead of the file default.

For everyday code, the practical rule is simple: put `@require(...)` on APIs
that need a platform or capability, and use `if (configured(...))` when a
function body needs to choose between supported implementations.

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
/// Prepares counted text for display.
///
/// @example
/// ```camp
/// auto name = input.toString();
/// ```
export prep char[] toDisplayText(const char[] this);
````

Examples should name the domain they are demonstrating. They do not need to be
complete programs, but they should avoid suggesting that invented APIs are part
of the standard library unless that is actually true.

## Target Availability With `@require`

`@require` marks a declaration as available only when the selected target or
build configuration satisfies a condition.

```camp
@require(SUPPORTS_TIMERS)
export extern TimerHandle startTimer(
	nuint intervalMs,
	escaped delegate void(TimerHandle handle) callback);
```

The same requirement is preserved in API headers and metadata so tools can show
which declarations are target- or capability-specific.

## Testing Attributes

Camp tests are ordinary top-level functions marked with `@test`. A small test
usually reads like the code it is checking: set up a value, call the operation,
and use `assert(...)` for the condition that should hold.

```camp
int add(int left, int right)
{
	return left + right;
}

@test
void addReturnsSum(thrown Assertion*)
{
	assert(add(2, 3) == 5);
}
```

The `thrown Assertion*` parameter is the runner's failure channel. You do not
write to it directly in ordinary tests; `assert(...)` and `fail(...)` use it
when a check fails. `assert(...)` also captures the source expression, file, and
line for the failure report, so the test can stay focused on the behavior:

```camp
@test
void divideRejectsZero(thrown Assertion*)
{
	if (canDivide(10, 0))
		fail("division by zero should be rejected");
}
```

The thrown slot may have an explicit parameter name when a function needs to
refer to it directly. Most tests do not need one.

Tests that allocate in an explicit-`within` context, which is common when
testing libraries, may ask the built-in runner for the default allocator:

```camp
@test
void bufferCreatesStorage(within allocator, thrown Assertion*)
{
	Buffer* buffer = new Buffer(32);
	delete buffer;
}
```

The allocator slot must appear before the thrown slot. It may use the implicit
form `within allocator`, or the explicit form `within Allocator* name` when the
project provides an accessible `Allocator` type. The runner supplies the same
default allocator representation used by `within(default)`.

For the standard interface-shaped `Allocator*`, the runner-supplied allocator
also tracks allocations made through that parameter. If a tracked allocation is
still live when the test returns, the test fails with a memory-leak result.
`campc cover` can report the coverage checkpoint captured at allocation time;
plain `campc test` reports the owning test location. Use `within(default)` for
intentional process-lifetime allocations that should not be tracked by the
built-in test leak detector. `campc test --ignore-leaks` and `campc cover
--ignore-leaks` still report leaks but do not make leak-only tests fail.

Run the tests in a project with:

```sh
campc test app.campbuild
```

When a test needs a helper that is not part of the real program, mark the helper
with `@testonly`. Test-only helpers are useful for fixtures, sample values,
small adapters, and helper types that make tests clearer without becoming part
of the production module.

```camp
@testonly
internal int expectedSum()
{
	return 5;
}

@test
void addUsesExpectedValue(thrown Assertion*)
{
	assert(add(2, 3) == expectedSum());
}
```

Use `internal` when a test-only helper should be shared across test files in the
same project. If a whole helper type is marked `@testonly`, its body travels
with it, so its fields and methods are available to tests and absent from
ordinary builds.

Sometimes a test should remain visible even though it is not ready to run. Add
`@skip("reason")` above the test, and the runner will report it as skipped:

```camp
@skip("waiting on parser fix")
@test
void futureParserCase(thrown Assertion*)
{
	fail("not ready");
}
```

Many projects keep tests beside the code they check. Running `campc test` on
that module builds a test version of the module, so those tests can exercise the
implementation directly. Larger projects can also use a separate test module
that references the production module as a shared library; that style tests the
same exported API a real consumer would use.

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
| `@symbol("Name")` | ABI-visible declarations where native spelling is meaningful; class, struct, interface, enum, and newtype declarations; enum values; exported globals; static fields and inline constants; functions and methods | Overrides the emitted/imported native symbol without changing Camp source lookup |
| `@index` | Index-like parameters | Enables index-aware syntax and diagnostics, including from-end indexes where length is visible |
| `@range` | First parameter in an `index, count` pair | Enables range boundary syntax such as `start..end` for slice-like APIs |
| `@awaitwith` | One ordinary runtime parameter of a concrete async body | Selects the resumer used after `await` suspension |
| `@noawait` | Concrete async definitions with Camp bodies | Declares that the async body cannot suspend and may not contain `await` |
| `@require(CONDITION)` | Declarations and fields where availability is meaningful | Makes the declaration available only when the configuration condition is satisfied |
| `@getshadow` | Shadow-capable base methods | Marks the getter hook that returns attached shadow data |
| `@setshadow` | Shadow-capable base methods | Marks the setter hook that stores attached shadow data |
| `@summary("text")` | Declarations and declaration children | Main documentation summary; plain doc-comment text lowers to this |
| `@remarks("text")` | Declarations and declaration children | Longer documentation notes, contracts, ownership, or behavior |
| `@returns("text")` | Functions, methods, callable declarations, and relevant callable children | Describes the returned value |
| `@example("text")` | Declarations and declaration children | Documentation example text, commonly a fenced code block in doc comments |
| `@see("text")` | Declarations and declaration children | Related-symbol or related-topic documentation metadata |
| `@deprecated("message")` | Declarations and declaration children | Marks a source API as deprecated for tooling; does not remove it from lookup or ABI output |
| `@overload("text")` | One function or method in an overload family | Summary for the whole overload group in generated documentation |
| `@category("name")` | Top-level declarations, or standalone near the top of a file as `@category("name");` | Category label for documentation generators |
| `@test` | Top-level functions with no visibility modifier | Marks a function as a discovered test; the built-in runner invokes `void name(thrown Assertion*)` and `void name(within Allocator* allocator, thrown Assertion*)` tests |
| `@testonly` | Private or `internal` top-level declarations | Includes a helper only in test and coverage builds; top-level types make their whole body test-only |
| `@skip("reason")` | Declarations also marked `@test` | Discovers the test but reports it as skipped without invoking it |

## Where To Look Back

For the feature-specific attributes already introduced:

- `@symbol` belongs with native names and exported ABI design.
- `@index` and `@range` belong with arrays, slicing, and indexer-like APIs.
- `@awaitwith` and `@noawait` belong with async bodies and resumers.
- `@require` belongs with target-conditioned APIs and standard-library
  portability.
- `@test`, `@testonly`, and `@skip` belong with first-class test runs.
- `@getshadow` and `@setshadow` belong with shadow classes and native extension
  surfaces.

For documentation, the useful rule is simple: write comments for the source API
you want callers to understand, and let metadata carry that source API to tools.
