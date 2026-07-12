# Standard Library And Interop

Camp's core language is small, but ordinary programs use the standard library
for allocation, arrays, strings, formatting, console I/O, files, collections,
time, math helpers, and platform integration. Native interop is part of the
language surface: Camp can declare external functions, choose call specs and
type specs, override symbols, and expose predictable C-facing ABI shapes.

This chapter gives the user-facing model. It intentionally avoids an exhaustive
standard-library API catalog so the language reference stays stable as the
library grows.

## Standard Library Availability

Normal compiler builds include the standard library unless the build is
configured otherwise. Standard library declarations live in exported namespaces
such as `Std`, `Std::Math`, and `Std::Time`.

```camp
using Std;
using Std::Math;
using Std::Time;
```

Use source or emitted metadata for exact API signatures. This reference names
representative APIs only where they clarify language behavior.

## What Is Language Versus Library

The language defines:

- primitive types;
- pointers, arrays, optionals, delegates, iterators, and async callable forms;
- `thrown`, `within`, lifetimes, generics, and interface dispatch;
- `extern`, `@symbol`, call specs, and target specs.

The standard library defines:

- allocator implementations;
- array and text helper methods;
- stream and file abstractions;
- console helpers;
- collections;
- math, formatting, parsing, timing, and date/time APIs.

When in doubt, a keyword or type form is language; an ordinary class, newtype,
function, or helper method in `Std` is library.

## Allocation Library Surface

The standard library exposes C-style allocation functions and an allocator
interface:

```camp
export extern void* malloc(nuint size);
export extern void* realloc(void* ptr, nuint newSize);
export extern void free(void* ptr);

export abstract class Allocator
{
	export abstract void* alloc(nuint size);
	export abstract void* realloc(void* ptr, nuint newSize);
	export abstract void free(void* ptr);
}

export virtual class HeapAllocator: Allocator
{
	...
}
```

Language allocation forms such as `new`, `delete`, and `within` use the
allocator model described in
[Lifetimes, Allocation, And `within`](16-lifetimes-allocation-and-within.md).
The library supplies common allocator implementations and native allocation
bindings.

## Arrays

Arrays are a language-expanded span shape: element pointer plus length. The
standard library layers helper methods over that shape.

Representative helper categories:

- slicing and address helpers;
- searching and containment;
- copying into allocator-owned storage;
- filling or mutating copyable element storage;
- collection adapters such as lists.

Generic array helpers use `T: any` when they only inspect, address, or walk
storage. They use `T: copyable` when they store, move, copy, compact, or return
`T` values.

```camp
nint index = values.indexOf(match);
auto copy = values.copyArray(within allocator);
```

See
[Arrays, Slices, Optionals, And Strings](14-arrays-slices-optionals-and-strings.md)
for the array type model.

## Strings And Counted Text

Camp distinguishes zero-terminated string pointer types from counted character
arrays.

```camp
string terminated = "ready";
const char[] counted = "ready";
```

The standard library provides conversions and text helpers across UTF-8,
UTF-16, and ASCII/system-code-page families.

Representative conversion helpers:

```camp
escaped string copyString(const char[] this, within allocator);
escaped wstring copyWString(const char[] this, within allocator);
escaped astring copyAString(const char[] this, achar unrepresentable = '?', within allocator);
```

Counting, slicing, trimming, parsing, formatting, and case conversion are
library operations over the source text model. Counted text indexing and
slicing are code-unit based unless a specific helper decodes Unicode scalar
values.

## Formatting

Formatting commonly uses callable newtypes. A formatter can be called once to
obtain a required size, then called again with a destination buffer.

```camp
export newtype delegate nuint CharFormatter(const this, char[] buffer = default);
```

Library helpers can turn a formatter into an allocated string:

```camp
string text = value.format.copyString() finally delete;
```

This pattern is useful because it keeps formatting allocation explicit. The
formatter itself is a callable value; `copyString` performs allocation through
the selected `within` context.

## Console

`Console` is a standard-library class with static helpers for standard input,
standard output, and standard error.

Representative surface:

```camp
export class Console
{
	export static CharReader getReader();
	export static CharWriter getWriter();
	export static CharWriter getError();

	export static void write(overload const char[] value);
	export static void write(overload string value);
	export static void writeLine(overload const char[] value);
	export static void writeLine(overload string value);
	export static char readChar();
	export static escaped string readLine(within allocator);
}
```

Console helpers are ordinary exported library APIs. They demonstrate overload
selector parameters, properties over getter methods, stream newtypes, and
allocator-returned strings.

```camp
Console.writeLine("ready");
string line = Console.readLine(within allocator) finally delete;
```

## Streams

The standard stream model uses iterator newtypes. Readers fill caller-provided
buffers; writers consume caller-provided buffers. Each iteration step yields
the number of elements transferred.

Representative core stream families:

```camp
export newtype iter nuint ByteReader(byte[] buffer);
export newtype iter nuint ByteWriter(byte[] buffer);

export newtype iter nuint CharReader(char[] buffer);
export newtype iter nuint CharWriter(char[] buffer);

export newtype iter nuint WCharReader(wchar[] buffer);
export newtype iter nuint WCharWriter(wchar[] buffer);

export newtype iter nuint ACharReader(achar[] buffer);
export newtype iter nuint ACharWriter(achar[] buffer);
```

The stream contract is deliberately small:

- the caller supplies the buffer;
- the stream step reports progress as `nuint`;
- `0` means no more progress for end-of-file style readers;
- errors are reported through `thrown IoError` on helpers that expose errors.

Helper methods layer more ergonomic operations on top:

```camp
writer.writeLine("sample");
reader.readLine(within allocator, catch error);
```

Streams are library APIs built from language iterators and callable newtypes.
They are not a separate hidden runtime object model.

## Files

The file library wraps native handles in a value newtype:

```camp
export newtype FileHandle: nint
{
	export static FileHandle open(string path, FileAccess access, FileMode mode, FileOptions options = default, thrown IoError error);
	export void close();
	export ulong getLength(thrown IoError error);
	export ulong getPosition(thrown IoError error);
	export void setPosition(ulong value, thrown IoError error);
	export void read(byte[] buffer, out nuint readCount, thrown IoError error);
	export void write(const byte[] buffer, thrown IoError error);
	export ByteReader getByteReader();
	export CharReader getCharReader();
	export ByteWriter getByteWriter();
	export CharWriter getCharWriter();
}
```

`FileHandle` is a raw native handle wrapper. It is not a class, and `delete
file` is not the close operation. Use `finally close()` for scoped cleanup:

```camp
IoError error;
FileHandle file = FileHandle.open(
	path,
	FileAccess.READ,
	FileMode.OPEN_EXISTING,
	catch error) finally close();
```

End of file for reads is represented by a successful read with `readCount == 0`.
I/O errors are reported with `IoError`.

## Collections

Collections such as `List<T>`, `HashMap<K, V>`, and `HashSet<T>` are standard
library classes. They use Camp's generic constraints to state copy behavior.

For example, a list that owns contiguous element storage uses `T: copyable`
because it stores and moves element values:

```camp
within (allocator)
{
	auto values = new List<int>() finally delete;
	values.add(10);
}
```

Hash-based collections use hash/equality policies so key identity is explicit.
Pointer and string keys should be understood according to the chosen policy,
not by assuming one universal library behavior.

Use collection metadata or source for exact method names and overloads.

## Math

`Std::Math` provides numeric constants and helper functions such as minimum,
maximum, and absolute value overloads:

```camp
using Std::Math;

int smaller = min(left, right);
double larger = max(first, second);
```

Primitive numeric types and numeric conversions are language features. The
helper functions and type-scope constants are library declarations.

## Time And Timing

`Std::Time` defines date/time value types and formatting/parsing helpers, such
as `Date`, `TimeOfDay`, `DateTime`, `OffsetDateTime`, `Instant`, `UtcOffset`,
and `TimeSpan`.

```camp
using Std::Time;

Date date = { 2026, 6, 12 };
string iso = date.format.copyString() finally delete;
```

Timing helpers expose native timers through ordinary newtypes and delegates:

```camp
TimerHandle handle = startTimer(10, within(default) new delegate h => {
	stopTimer(h);
});
```

These APIs show how Camp models native handles, escaped callbacks, and cleanup
without adding special syntax for timers or clocks.

## Native Interop With `extern`

`extern` declares a function, variable, type, constructor, or destructor
implemented outside the current Camp body.

```camp
@symbol("puts")
extern int cPuts(const char* text);
```

Use `extern` declarations to represent native or separately compiled surfaces.
Wrap them in ordinary Camp APIs when callers should use safer types, counted
arrays, `thrown` slots, lifetimes, or allocator-aware ownership.

```camp
@symbol("strlen")
extern nuint nativeStringLength(const char* text);

nuint lengthOf(const char[] text)
{
	return text.length;
}
```

The wrapper keeps unsafe or target-specific details local to the boundary.

## Extern Classes

An `extern class` represents a foreign object type. Camp does not own its
layout.

```camp
export extern class NativeWindow
{
	export extern NativeWindow();
	export extern ~NativeWindow();
	export extern void show();
}
```

Construction and destruction call the external create/delete surfaces declared
by the extern constructor and destructor. Camp does not synthesize hidden
layout, allocator logic, or ordinary managed class helper bodies for an extern
class.

Extern class inheritance is foreign opaque inheritance: extern classes inherit
only from other extern classes, and non-extern classes inherit only from
non-extern classes.

## Source Symbols And Native Symbols

The Camp source name and native symbol name are separate concepts.

```camp
@symbol("CreateFileW")
extern nint createFileWide(wchar* path);
```

Callers use the Camp name:

```camp
nint handle = createFileWide(path);
```

Generated native output uses the symbol override. This separation lets Camp
source remain readable while matching required ABI spellings.

## Call Specs

Call specs describe target calling conventions for concrete callable surfaces.

```camp
export newtype fn _stdcall int WindowProcedure(WindowHandle handle);
extern _cdecl int nativeCompare(const void* left, const void* right);
```

The set of valid call specs comes from the selected target. A call spec is part
of callable compatibility and ABI shape. If an interface slot or callable
newtype requires a call spec, implementing methods or assigned callables must
match that contract.

## Type Specs

Type specs apply to target-capable carrier types such as data pointers, raw
function pointers, and natural integers.

```camp
byte* _far farBytes;
fn* _near rawCallback;
nint _huge address;
```

The selected target defines which type specs exist and which carriers accept
them. Type specs do not tunnel through constructed types. For example, a
specifier on an array carrier is not the same as a specifier on the element
pointer unless the type spelling and target policy say so.

## Raw Function Pointers

`fn*` is a raw function-pointer carrier. It is useful at native boundaries
where the program needs to carry an untyped or target-specific function address.

```camp
export extern void consumeRaw(fn* value);
```

Do not confuse `fn*` with a Camp callable value. A `delegate`, `once`, `iter`,
or `async` value may have call/context components. A raw function pointer is
only the raw call address carrier. Conversions involving `fn*` follow the raw
carrier and unsafe-cast rules described in
[Pointers, Qualifiers, And Conversions](07-pointers-qualifiers-and-conversions.md).

## Arrays And Native Boundaries

Camp arrays lower as separate pointer and length components. This is ideal for
Camp-to-Camp APIs and for native APIs designed around spans.

```camp
export void writeBytes(const byte[] data);
```

For native APIs that expect a C pointer without a length, expose that pointer
explicitly:

```camp
extern int cWrite(const byte* data, nuint length);
```

Do not rely on hidden null termination for counted arrays. If a native API
needs a zero-terminated string, pass a string pointer or create a terminated
copy with an appropriate library helper.

## Strings And Native Boundaries

Use `string`, `wstring`, or `astring` for zero-terminated native string
pointers. Use counted arrays when the API needs an explicit length.

```camp
@symbol("puts")
extern int cPuts(string text);

void writeCounted(const char[] text)
{
	string terminated = text.copyString() finally delete;
	cPuts(terminated);
}
```

String literals can target zero-terminated string types, const character
pointers, and const counted character arrays. Mutable string pointers are not
inferred from string literals.

## Pointers And Ownership At Native Boundaries

Native APIs often make ownership rules implicit. Camp APIs should make them
explicit in the wrapper.

```camp
extern void* nativeCreate();
extern void nativeDestroy(void* handle);

newtype NativeHandle: void*
{
	void close()
	{
		nativeDestroy(this);
	}
}
```

Use `finally close()` or `finally delete` according to the actual ownership
contract. Use lifetimes such as `escaped`, `scoped`, and `constof(...)` to
express pointer validity when the compiler can enforce it.

## Target-Conditioned Code

Use preprocessor symbols and target-provided capabilities to isolate
target-specific declarations.

```camp
#if WINDOWS
@symbol("CreateFileW")
extern nint createFileWide(wchar* path);
#else
@symbol("open")
extern int openFile(const char* path, int flags);
#endif
```

Keep target-conditioned code close to the interop boundary. Ordinary program
logic should call a small Camp wrapper that hides the target split.

## Generated Headers And Metadata

Exported declarations can appear in generated C-facing headers and metadata.
The exact generated C spelling is target-owned, but the source contract is
defined by Camp declarations:

- exported structs expose layout;
- exported classes are opaque across the public ABI;
- exported enums use precise underlying widths;
- exported newtypes preserve nominal names;
- exported callable newtypes preserve named callable contracts;
- exported interface implementation relationships expose vtable objects where
  needed for generic interface dispatch.

Metadata describes the Camp source surface. It does not include generated
async frames, iterator internals, hidden vtable fields, or helper thunks in the
primary source-level view.

## Interop Design Guidelines

Prefer a two-layer API:

```camp
@symbol("native_read")
extern nint nativeRead(nint handle, void* buffer, nuint length, out IoError error);

void read(FileHandle handle, byte[] buffer, out nuint count, thrown IoError error)
{
	nint result = nativeRead((nint)handle, buffer.elements, buffer.length, out error);
	if (result < 0)
		throw error;
	count = (nuint)result;
	error = default;
}
```

The extern declaration matches the native ABI. The Camp wrapper exposes Camp
semantics: counted arrays, a nominal handle, `out` success data, and a
`thrown` error slot.

Prefer explicit wrappers when native APIs use:

- nullable pointer sentinels;
- ownership transfer;
- platform-specific calling conventions;
- global error state;
- zero-terminated strings;
- raw function pointers;
- target-specific integer widths;
- callback contexts.

Camp's interop features are designed to make those boundaries precise without
letting native ambiguity leak through the whole codebase.
