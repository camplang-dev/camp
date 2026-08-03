+++
nav_title = "16. Standard Library"
+++

# The Standard Library In Practice

Camp's standard library is deliberately ordinary Camp code. It gives you the
pieces that small programs reach for first: console I/O, allocation helpers,
array and string operations, formatting, files, streams, collections, math,
time, timers, and a few native-facing utility types.

Rather than trying to memorize every overload, read this as a tour of the
library's style. The examples are small on purpose: they show how `Std` fits
with the language features you already know, and where to look when you need
the exact shape of a specific API.

## Using `Std`

Unless the build uses `--nostdlib`, ordinary source files receive an implicit
root `Std` import. Most examples can therefore start directly with the code that
uses the library:

```camp
export int main()
{
	Console.writeLine("ready");
	return 0;
}
```

`Std` is a namespace, not a runtime prelude. The implicit import makes ordinary
standard-library declarations visible: `Console`, `List<T>`, `FileHandle`,
`CharWriter`, array helpers, string helpers, and so on.

You may still write an explicit root import when you want a different lookup
shape. `using Std as S;` aliases the namespace, and `using Std { Console };`
selects only named declarations. Any explicit root `Std` import replaces the
implicit one for that file.

Some library areas live in child namespaces:

```camp
using Std::Time;
```

Use the child namespace when the domain benefits from shorter names, as with
`Date`, `Instant`, and `TimeSpan`.

## Language Forms Versus Library APIs

Camp keeps a sharp line between the language and the library.

The language gives you forms such as:

- primitive types, pointers, arrays, optionals, delegates, iterators, and async;
- `thrown`, `within`, lifetimes, `new`, `delete`, and `finally`;
- `struct`, `class`, `interface`, `enum`, `newtype`, and generics;
- `extern`, `@symbol`, call specs, and type specs.

The standard library gives you ordinary declarations built from those forms:

- `Console`, `FileHandle`, `List<T>`, `HashMap<K, V>`, and `HashSet<T>`;
- `Allocator` and `HeapAllocator`;
- array copying, sorting, searching, and resizing helpers;
- string searching, trimming, copying, case conversion, and parsing helpers;
- `CharReader`, `CharWriter`, `ByteReader`, and `ByteWriter`;
- formatting, math, date/time, timers, and atomics.

That line helps when reading code. If something is a keyword or a type form, it
belongs to the language. If it is a class, newtype, function, method, or inline
constant in `Std`, it is library code.

## Console I/O

`Console` is the first library class most programs touch:

```camp
export int main(string[] args)
{
	Console.write("argument count: ");
	Console.writeLine(args.length);
	return 0;
}
```

`Console.write` and `Console.writeLine` are overloaded for text, characters,
booleans, and common numeric types. They are useful for simple programs,
diagnostics, examples, and tests.

When code wants a stream-shaped surface instead of a static helper, ask the
console for a writer:

```camp
void writeReportHeader()
{
	CharWriter writer = Console.Writer;
	writer.writeLine("Report");
	writer.writeLine("------");
}
```

That `CharWriter` is a callable iterator-style value. The console is not a
special language feature; it is a standard-library object exposing the same
stream model other APIs can use.

## Allocation And `within`

The standard library defines the common allocator surface:

```camp
interface Allocator
{
	void* alloc(nuint size);
	void* realloc(void* ptr, nuint newSize);
	void free(void* ptr);
}
```

`HeapAllocator` is the default heap-backed allocator value, and the library
also exports native allocation functions such as `malloc`, `realloc`, and
`free`.

Most code does not call those functions directly. It uses `new`, `delete`, and
`within`:

```camp
void collectValues(Allocator* allocator)
{
	within (allocator)
	{
		auto values = new List<int>() finally delete;
		values.add(10);
		values.add(20);
		Console.writeLine(values.Length);
	}
}
```

The `within` block chooses the allocation context for operations inside it.
The list remembers the allocator it uses for element storage, and
`finally delete` makes the local ownership easy to see.

Passing `null` as an allocator is a common way to use the default native
allocation path when an API accepts an `Allocator*`.

## Arrays

Arrays are language values, but the standard library layers useful operations
over them:

```camp
int compareInt(in int left, in int right)
{
	if (left < right)
		return -1;
	if (left > right)
		return 1;
	return 0;
}

void sortScores()
{
	int[] scores = [3, 1, 4, 2];
	scores.sort<int>(compareInt);

	int[] copy = scores.copyArray<int>() finally delete;
	Console.writeLine(copy.length);
}
```

Helpers such as `copyTo`, `copyFrom`, `copyArray`, `reverse`, `sort`,
`binarySearch`, and `findIndex` keep the same generic discipline you saw in
the generics chapter. Operations that copy or rearrange elements use
`T: copyable`. Operations that only inspect through `in T` can work with
broader erased values.

For one-off loops, plain indexing is still often clearer:

```camp
int sum(const int[] values)
{
	int total = 0;
	for (nuint index = 0; index < values.length; index++)
		total += values[index];
	return total;
}
```

Use the helper when it captures a real operation; use the loop when the loop is
the clearest description of the work.

## Text And Strings

Camp has both zero-terminated string pointer types and counted text views. The
standard library helps you move between them and perform common text
operations.

```camp
void inspectText(string text)
{
	const char[] view = text[..];

	if (view.trim().startsWith("camp", true))
		Console.writeLine("matched");
}
```

Search and comparison helpers operate on counted text:

```camp
bool hasExtension(const char[] path)
{
	return path.endsWith(".camp", true);
}
```

Borrowing helpers such as `trim`, `trimStart`, and `trimEnd` return views into
the original text. Copy-producing helpers allocate new storage:

```camp
string makeUpperCopy(const char[] text, Allocator* allocator)
{
	within (allocator)
		return text.uppercaseCopy();
}
```

The caller who receives an owned string should clean it up according to the API
contract:

```camp
void printUpper(const char[] text, Allocator* allocator)
{
	string upper = makeUpperCopy(text, allocator) finally delete;
	Console.writeLine(upper);
}
```

The same general pattern exists for `wstring` and `astring` helpers. Use the
string family that matches the native boundary or storage convention you are
working with.

## Formatting

Formatting in `Std` uses `toString` methods with `prep` buffers. Omitting the
buffer produces the formatted character array; supplying the buffer calls the
formatter directly and returns its required length. Interpolated strings use
the same preparation shape for runtime holes. Character, text, primitive, and
date/time formatting APIs use this `toString` convention.

You often do not need to see that two-step protocol directly:

```camp
void printTotal(int total)
{
	Console.writeLine($"total: {total}");
}
```

An interpolated string with only constant text is still just constant text:

```camp
auto title = $"Camp"; // string
```

An interpolation that contains runtime values produces text at the expression
site. With `auto`, the result is `string`:

```camp
int total = 42;
auto text = $"total: {total}";
Console.writeLine(text);
```

Without `new`, that runtime text uses scoped storage. Use `new` when you need
heap lifetime:

```camp
escaped string formatTotal(int total)
{
	return within(default) new $"total: {total}";
}
```

That returns an owned `string`, so ordinary cleanup applies:

```camp
void showTotal(int total)
{
	string text = within(default) new $"total: {total}" finally delete;
	Console.writeLine(text);
}
```

Date/time values use the same style, which makes library formatting feel
consistent across domains.

You can make your own formatting surface by adding a formatter method. A
formatter returns the required character count. When a buffer is supplied, it
writes as much of the formatted text as fits. It does not write a null
terminator.

```camp
public nuint formatHex(in byte this, prep char[] buffer = default)
{
	const char[] digits = "0123456789ABCDEF";
	uint value = (uint)this;
	nuint required = 2;

	if (buffer.length > 0)
	{
		buffer[0] = digits[(nuint)(value >> 4)];
	}
	if (buffer.length > 1)
		buffer[1] = digits[(nuint)(value & 15)];

	return required;
}

void printByte(byte value)
{
	Console.writeLine($"0x{value.formatHex()}");
}
```

The interpolation uses the `formatHex` call as the hole's formatter. That keeps
the formatting choice local to the expression without changing how plain `byte`
values format elsewhere.

For direct formatting, call `toString()` normally:

```camp
char[] text = total.toString();
```

Use `.length` when only the required character count matters. When formatting
many values, measure and reuse a buffer instead of repeatedly creating scoped
arrays:

```camp
nuint required = total.toString().length;
char[] storage = init char[required];
total.toString(buffer: storage);
```

## Files

The file API wraps native handles in a `FileHandle` newtype. It is not a class,
and `delete file` is not how you close it. Call `close`, usually as cleanup.

```camp
bool writeSmallFile(string path)
{
	IoError error = default;
	FileOptions options = default;

	FileHandle file = FileHandle.open(
		path,
		FileAccess.WRITE,
		FileMode.CREATE_OR_TRUNCATE,
		options,
		catch error) finally close();

	if (error != default)
		return false;

	const byte[] bytes = [(byte)'o', (byte)'k', (byte)'\n'];
	file.write(bytes, catch error);
	return error == default;
}
```

The API uses ordinary Camp error flow. Opening can report an `IoError`; reading
and writing can report one too. End of file for a read is a successful read
with a count of zero.

On targets without file support, file APIs may be marked unsupported. That is a
target capability issue, not a different source-language rule.

## Streams

Streams in `Std` are small callable contracts. A reader fills a caller-provided
buffer and yields how much it produced. A writer consumes a buffer and yields
how much it accepted.

Representative stream newtypes include:

```camp
newtype iter nuint ByteReader(byte[] buffer);
newtype iter nuint ByteWriter(byte[] buffer);
newtype iter nuint CharReader(char[] buffer);
newtype iter nuint CharWriter(char[] buffer);
```

You saw iterators in the previous chapter; these are named iterator contracts
for common I/O shapes. Higher-level helpers sit on top:

```camp
void writeLines(CharWriter writer)
{
	writer.writeLine("alpha");
	writer.writeLine("beta");
}
```

Files and the console both expose stream values:

```camp
void copyGreetingToConsole()
{
	CharWriter writer = Console.Writer;
	writeLines(writer);
}
```

The design is intentionally plain. A stream is not a hidden runtime object; it
is a callable value with a context and a small protocol.

## Lists

`List<T>` is the standard growable contiguous collection. It stores and moves
values, so its type parameter is `T: copyable`.

```camp
void collectReadings()
{
	auto readings = new List<int>() finally delete;

	readings.add(10);
	readings.add(20);
	readings.add(15, 1);

	Console.writeLine(readings.Length);
	Console.writeLine(readings.Item[1]);
}
```

The list exposes common collection operations: add, insert, remove, clear,
copy, sort, search, and indexed access. Its backing storage belongs to the
list until you explicitly copy it or take it:

```camp
int[] takeReadings(List<int>* readings)
{
	return readings.takeBuffer();
}
```

After `takeBuffer`, the caller owns the returned array and should eventually
delete it.

## Hash Maps And Hash Sets

Hash-based collections make hashing and equality explicit through
`HashPolicy<T>`. That matters because strings, pointers, and handles do not
all have one obvious identity rule.

```camp
void countNames()
{
	auto counts = new HashMap<string, int>(string.CASE_INSENSITIVE_HASH_POLICY)
		finally delete;

	counts.Item["North"] = 1;
	counts.Item["north"] = counts.Item["north"] + 1;

	Console.writeLine(counts.Item["NORTH"]);
}
```

Sets use the same idea:

```camp
void rememberIds()
{
	auto ids = new HashSet<int>(int.HASH_POLICY) finally delete;

	ids.add(10);
	ids.add(20);

	if (ids.contains(10))
		Console.writeLine("seen");
}
```

When a collection takes a policy, choose the policy that matches the meaning of
the key. Pointer identity, case-sensitive string identity, and
case-insensitive string identity are different choices.

## Math Helpers

`Std` provides numeric limits and small helpers:

```camp
int clampScore(int value)
{
	return min(max(value, 0), 100);
}

bool isByteSized(int value)
{
	return value >= byte.MIN && value <= byte.MAX;
}
```

These are ordinary overloaded functions and inline constants. Numeric types,
casts, and overflow behavior are language topics; `min`, `max`, `abs`, and
type limits are library conveniences.

## Time

`Std::Time` contains small value types for dates, times, instants, offsets, and
durations:

```camp
using Std::Time;

void printDate()
{
	Date date = { 2026, 7, 14 };
	char[] text = date.toString();
	Console.writeLine(text);
}
```

The time API is intentionally modest. It includes useful value types and ISO-ish
formatting/parsing helpers, not a full calendar/time-zone framework.

```camp
using Std::Time;

bool parseDate(const char[] text, out Date date)
{
	return text.tryParse(out date) && date.isValid();
}
```

Values are ordinary structs and newtypes. If input may be untrusted, check
`isValid`.

## Timing, Timers, And Atomics

The timing helpers expose target-backed sleep, async sleep, repeating timers,
and a small set of atomic operations:

```camp
void pauseBriefly()
{
	sleep(10);
}
```

Async sleep uses the async callback shape:

```camp
bool timerDone;

void scheduleFlag()
{
	sleepAsync(50, () =>
	{
		timerDone = true;
	});
}
```

Timers use escaped callbacks because the callback may outlive the call that
starts the timer:

```camp
void startHeartbeat()
{
	TimerHandle handle = startTimer(1000, new delegate h =>
	{
		Console.writeLine("tick");
		stopTimer(h);
	});
}
```

Timer availability is target-dependent. If a target does not support timers or
sleeping, these declarations may be marked unsupported.

Atomics are deliberately narrow:

```camp
void publish(nint* slot, nint value)
{
	atomicExchange(slot, value);
}
```

They cover native integers and pointers. Build richer synchronization
abstractions in a library layer when a program needs them.

## Reading The Library

When you need exact signatures, use the source and metadata rather than this
guide. The library lives under `lib/std/src`, and the files are small enough to
read directly:

- `std_console.camp` for `Console`;
- `std_array.camp` for array helpers;
- `std_string.camp`, `std_wstring.camp`, and `std_astring.camp` for text;
- `std_format.camp` for formatting;
- `std_stream.camp` for readers and writers;
- `std_file.camp` for files;
- `std_list.camp`, `std_hashmap.camp`, and `std_hashset.camp` for collections;
- `std_math.camp` for limits and numeric helpers;
- `std_time.camp` and `std_timing.camp` for time and timers.

The patterns are more important than memorizing every method:

- standard-library types are ordinary Camp declarations;
- allocation is explicit through `within`, owned returns, and cleanup;
- arrays and strings keep their source-level storage shapes;
- streams are callable iterator contracts;
- collections state their copy and equality requirements;
- target-backed features may be unavailable on some targets.

Once those patterns feel natural, reading a new standard-library API is mostly
reading ordinary Camp.
