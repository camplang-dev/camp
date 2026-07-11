# Camp LLM Coding Guide

## Purpose And Assumptions

This guide is for LLM agents writing Camp application or library code. Pair it
with standard-library metadata when available. Prefer exact metadata signatures
over guessing library APIs.

## High-Priority Rules

- Write meaningful names in examples and generated code.
- Prefer source-level Camp types over lowered shapes.
- Use explicit lifetimes and `within` when ownership matters.
- Do not assume arrays own their elements.
- Do not invent standard-library APIs.
- Use `thrown` and `catch` rather than ad hoc error channels when an API uses
  thrown slots.
- Keep target-specific type specs and call specs close to native boundaries.

## Minimal Syntax

```camp
export int main()
{
	return 0;
}
```

Declarations end with semicolons when they have no body. Blocks use braces.
Function bodies use ordinary statements.

## Namespaces And Visibility

Use `export as` for module namespace, `using` for imports, and `export` for API
surface. Use `public` for visible source surface that is not exported as an API
boundary.

```camp
export as Samples::Text;
using Std;
```

## Types Cheat Sheet

- `T*`: pointer to `T`.
- `T[]`: counted array view.
- `T[N]`: fixed-size storage.
- `T?`: optional value.
- `fn R(P)`: direct callable.
- `delegate R(P)`: callable with context.
- `once R(P)`: callable invoked exactly once.
- `const T`: read-only view.
- `escaped T`, `scoped T`, `unscoped(anchor) T`: lifetime-qualified types.

## Function And Callable Patterns

```camp
export int add(int left, int right)
{
	return left + right;
}

export void visitLines(const char[][] lines, delegate void(const char[] line) visitor)
{
	foreach (const char[] line in lines)
		visitor(line);
}
```

Use callable newtypes for public callback shapes.

## Struct/Class Patterns

Use structs for value aggregates and classes for reference-oriented objects.
Use receiver parameters for methods.

```camp
export struct Position
{
	int row;
	int column;
}

export int distanceFromOrigin(const Position this)
{
	return this.row + this.column;
}
```

## Arrays, Strings, And Optionals

Use `const char[]` for read-only text. Use `T[]` for counted views and `T[N]`
for inline storage.

```camp
export bool hasText(const char[] text)
{
	return text.length > 0;
}
```

## Lifetimes And Allocation

Use `within` for allocation contexts and explicit lifetime annotations when a
value is retained.

```camp
export Buffer* createBuffer(nuint capacity, within allocator)
{
	return new Buffer(capacity);
}
```

Do not store scoped values in escaped fields.

## Error Handling

Use `thrown` parameters for APIs that report errors.

```camp
export void loadBytes(const char[] path, out byte[] data, thrown IoError);
```

Handle errors with `catch` arguments or `try`/`catch` blocks.

## Generics

Request only the capabilities the generic body needs.

```camp
export T choose<T: copyable>(T left, T right, bool useLeft)
{
	return useLeft ? left : right;
}
```

Use `sizeof(T)`, `typenameof(T)`, and `vtableof(T: Interface)` parameters when
the body needs those capabilities.

## Iterators

Use `yield` in generator functions and `foreach` for consumption.

```camp
export iter int countUp(int limit)
{
	for (int value = 0; value < limit; value++)
		yield value;
}
```

## Async

Async APIs are callback-shaped and may be awaited when their completion
callback has an awaitable shape.

```camp
const char[] text = await loadText(path);
```

Use a result struct for multiple success values.

## Interop

Use `extern`, `@symbol`, call specs, type specs, and explicit pointer types at
native boundaries.

```camp
@symbol("puts")
extern int cPuts(const char* text);
```

## Standard Library Conventions

Use metadata or source for exact standard-library signatures. The prose docs
intentionally include only a small API surface.

## Common Pitfalls

- Treating `T[]` as owned storage.
- Forgetting that conversions do not rewrite generic arguments or array element
  types.
- Omitting `within` when allocation context is part of the API.
- Capturing scoped values in escaped delegates.
- Assuming `T: any` permits copying or construction.
- Returning multiple async success values instead of a result struct.
- Using raw carriers when a normal typed conversion or reconstruction is the
  right operation.

## Canonical Idioms

Simple function:

```camp
export int clampToZero(int value)
{
	return value < 0 ? 0 : value;
}
```

Receiver method:

```camp
export bool isEmpty(const Buffer* this)
{
	return this.length == 0;
}
```

Thrown API:

```camp
export void parseCount(const char[] text, out int value, thrown ParseError);
```

## Self-Check Before Returning Code

- Are all referenced APIs real or supplied by metadata?
- Are allocations paired with `within` and cleanup where needed?
- Are lifetimes explicit when values escape?
- Are generic capabilities declared?
- Are `thrown` slots handled or forwarded?
- Are target-specific details limited to interop boundaries?
