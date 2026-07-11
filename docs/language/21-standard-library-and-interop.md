# Standard Library And Interop

## Standard Library Availability

The standard library is included by default for normal compiler builds. Use
compiler options when a build must omit it or use package/project references
explicitly.

This language reference mentions only the minimum standard-library surface
needed for examples and source semantics. Use metadata or source when exact API
coverage matters.

## Arrays And Strings

Standard helpers operate on counted arrays and text values. Common string
surfaces use `const char[]` for read-only UTF-8 text.

```camp
const char[] label = "ready";
```

## Console And Streams

Console and stream helpers are ordinary exported library APIs. Prefer metadata
or source for exact signatures because standard-library APIs can change without
changing the language.

## Files

File APIs live in the standard library and are not part of the core language.
They are useful examples of `thrown` slots, arrays, allocation, and native
helper integration.

## Time And Math

Time and math helpers are standard-library APIs. The language defines the
primitive types and call semantics they use, while the library defines the exact
functions.

## Native Interop Declarations

`extern` declares native functions and types.

```camp
@symbol("puts")
extern int cPuts(const char* text);
```

Interop APIs should use explicit pointer, constness, call specifier, and target
specifier rules when the native boundary requires them.

## Call Specs And Type Specs

Targets define valid call specs and type specs. Call specs apply to concrete
callables. Type specs apply to target-capable carrier types such as data
pointers, function pointer carriers, and natural integers.

```camp
extern fn _cdecl int(int value) transform;
void* _far memory;
```

## Target-Conditioned Code

Use preprocessor symbols and target-defined capabilities for target-conditioned
source. Keep the target-specific boundary narrow so most code remains ordinary
Camp.
