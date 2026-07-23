+++
nav_title = "14. Iterators"
+++

# Iterators, `foreach`, And Generated Sequences

An iterator is what you use when a value can produce a sequence without already
being an array. It might walk a buffer, filter another iterator, parse tokens,
read from a device, or generate numbers on demand. Camp gives that pattern a
source-level shape with `iter`, `yield`, and `foreach`, while keeping the
underlying ABI close to ordinary state plus ordinary functions.

That last part matters. A Camp iterator is not a hidden runtime object. It has
state, a `next(...)` protocol, and deterministic cleanup. `foreach` is the
comfortable surface over that protocol; manual iteration is still available
when you need to see the machinery.

This chapter starts with the everyday path: consume a generator with
`foreach`. Then it opens up the iterator type forms, failure flow, cleanup,
generic iterators, and the C-shaped model that makes iterators useful at native
boundaries.

## The Everyday Shape

Most iterator code looks like this:

```camp
int sumRange(int first, int last)
{
	int total = 0;

	foreach (int value in range(first, last))
		total += value;

	return total;
}

struct iter int range(int first, int last)
{
	for (int value = first; value <= last; value++)
		yield value;
}
```

`range` is a generator. Calling it creates iterator state. The body runs as the
iterator is advanced, and each `yield` writes one current value for the caller.

`foreach` drives that process. It asks the iterator for a value, runs the loop
body when one is produced, and cleans up the iterator when the loop is done.

## Iterator Type Forms

The ordinary iterator type is `iter T`:

```camp
iter int values;
```

The parenthesized form is used when the iterator carries a thrown slot:

```camp
iter(int, thrown ReadError) checkedValues;
```

An iterator type has exactly one yielded type and may optionally end with one
`thrown` type.

Valid forms:

```camp
iter int
iter(int)
iter(int, thrown ReadError)
```

Invalid forms:

```camp
iter(thrown ReadError)              // ERROR: no yielded value
iter(int, int)                      // ERROR: two yielded values
iter(int, thrown ReadError, int)    // ERROR: value after thrown slot
iter(int, thrown A, thrown B)       // ERROR: two thrown slots
iter(in int)                        // ERROR: yielded slot is not an in parameter
```

The yielded type is an ordinary value slot. The thrown slot is an error slot,
not a second yielded value. When an iterator needs to produce several fields,
yield a named struct that carries those fields.

## Plain Iterator Values

A function can return an iterator value without being a generator:

```camp
iter int readCachedNumbers()
{
	return openCachedNumberIterator();
}
```

That function runs normally when called. It does not use `yield`, and it does
not get a generated state type merely because its return type is `iter int`.
It returns some iterator value produced elsewhere.

This distinction is useful at ABI boundaries. A library may expose a function
that returns a named iterator contract, while the actual state comes from
native code, a generated class iterator, or another Camp routine.

## Generator Declarations

Use `struct iter` or `class iter` when the function body itself contains
`yield` or `yield break`:

```camp
nuint countPowers(int limit)
{
	nuint count = 0;

	foreach (int value in powersOfTwo(limit))
		count++;

	return count;
}

struct iter int powersOfTwo(int limit)
{
	int value = 1;

	while (value <= limit)
	{
		yield value;
		value *= 2;
	}
}
```

The prefix chooses the generated state container:

| Form | Generated state | Typical use |
|---|---|---|
| `struct iter T` | generated fixed struct | local, zero-allocation iteration |
| `class iter T` | generated class | stored, escaped, or exported iteration |

Both generated state types are real Camp-visible types. For a generator named
`powersOfTwo`, the generated iterator state type is named `powersOfTwoIter`.

## What Calling A Generator Does

Calling a generator stores its arguments in iterator state. The generator body
does not run immediately:

```camp
struct iter int repeat(int value, nuint count = 1)
{
	for (nuint index = 0; index < count; index++)
		yield value;
}
```

When code calls `repeat(7, 3)`, it gets iterator state containing `value` and
`count`. The first loop iteration starts only when `foreach` or a manual
`next(...)` call advances that state.

Because generator parameters are retained state, generator parameter lists
cannot contain:

- `in` parameters;
- `out` parameters;
- trailing `thrown` parameters.

Default arguments are allowed because they are resolved when the iterator state
is created.

Pointer-bearing parameters must be valid for the chosen state container. A
`struct iter` can retain scoped values as long as the generated fixed-struct
state cannot outlive them. A `class iter` stores state in an escaped class
object, so retained pointer-bearing values must be escaped too.

That rule includes an instance generator's receiver. In ordinary user code,
put a `class iter` member on an `escaped class` when the generated iterator
state needs to retain `this`.

## `yield`

`yield` produces one logical current value and suspends the generator until the
next advance:

```camp
struct iter int firstThree()
{
	yield 1;
	yield 2;
	yield 3;
}
```

The yielded expression must be compatible with the iterator's yielded type. To
yield multiple pieces of information, make one value that carries them:

```camp
enum TokenKind
{
	IDENTIFIER,
	NUMBER
}

struct Token
{
	TokenKind kind;
	const char[] text;
}

struct iter Token tokens(unscoped const char[] source)
{
	yield { .kind = IDENTIFIER, .text = source[..1] };
}
```

Lifetime checks apply to yielded values. A yielded view must remain valid for
the iterator protocol surface:

```camp
struct iter const char[] firstPart(unscoped const char[] text)
{
	yield text[..1];
}
```

That works because the yielded slice is tied to the caller-provided `text`.
This does not work:

```camp
struct iter const char[] badLocal()
{
	char[] scratch = init char[16];
	yield scratch; // ERROR: yielded view points at generator-local storage
}
```

The local array storage would be part of the iterator's state, but the yielded
span would escape through the current-value slot in a way the signature does
not promise.

Use `yield break;` to end a generator early without producing another value:

```camp
struct iter int nonnegativePrefix(int[] values)
{
	foreach (int value in values)
	{
		if (value < 0)
			yield break;
		yield value;
	}
}
```

A generator body must contain at least one `yield` or `yield break` statement,
even if the statement is not reachable on every path. `yield break;` is the
right spelling for an iterator that intentionally produces no values.

Do not use `return` inside a generator. `return` remains valid in ordinary
functions that return an `iter T` value without being generators.

## Failing Iterators

Iterator failure belongs in the iterator type, not the generator parameter
list:

```camp
enum ReadError
{
	OK = 0,
	END_OF_STREAM,
	INVALID_DATA
}

struct iter(int value, thrown ReadError error) readNumbers(int failAt)
{
	yield 1;

	if (failAt == 2)
		throw ReadError.INVALID_DATA;

	yield 3;
}
```

The yielded type appears first. The `thrown` slot appears after it inside the
`iter(...)` type form.

Calling `readNumbers(2)` only creates iterator state. The error can occur later
when the iterator is advanced. That is why `thrown` does not belong in the
generator parameter list.

An ordinary function that prepares an iterator can still fail before returning
the iterator:

```camp
iter byte openBytes(const char[] path, within allocator, thrown OpenError error)
{
	return openOwnedByteIterator(path, within allocator);
}
```

Here `openBytes` can report a preparation error immediately. The iterator it
returns has its own iterator type, which may or may not also include a thrown
slot.

## `foreach`

`foreach` is the ordinary way to consume arrays and iterators:

```camp
int sumValues(iter int values)
{
	int total = 0;

	foreach (int value in values)
		total += value;

	return total;
}
```

For arrays, `foreach` enumerates elements in order. For iterators, it drives
the iterator protocol until no more values are produced or an error leaves the
loop.

The loop variable is scoped to the loop body. `continue` advances to the next
item. `break` exits the loop and still runs iterator cleanup.

With a failing iterator, `foreach` participates in ordinary thrown flow:

```camp
int sumUntilFailure(thrown ReadError error)
{
	int total = 0;

	foreach (auto value in readNumbers(2))
		total += value;

	return total;
}
```

Or the loop can catch the error locally:

```camp
int sumOrZero()
{
	int total = 0;

	try
	{
		foreach (auto value in readNumbers(2))
			total += value;
	}
	catch (ReadError error)
	{
		total = 0;
	}

	return total;
}
```

This is the same `try` / `catch` surface you saw in the errors chapter. Camp is
still modeling explicit error slots, not runtime exception objects.

## The Iterator Protocol

Beneath `foreach`, an iterator is driven by a `next(...)`-shaped protocol. For
a generated iterator state type `Y` yielding `T`, the source model is:

```camp
bool next(Y* this, T* current);
```

`this` points at iterator state. `current` points at caller-provided storage
for the next yielded value. The boolean result says whether a value was
produced.

For a failing iterator, the thrown slot follows the current-value slot:

```camp
bool next(Y* this, T* current, thrown ReadError error);
```

The exact helper names depend on the generated state type and visibility, but
the protocol shape is the important part.

## Manual Iteration

Most code should use `foreach`, but manual iteration is useful at low-level
boundaries and in iterator adapters:

```camp
int sumManual(int first, int last)
{
	rangeIter iterator = range(first, last) finally delete;
	int current = 0;
	int total = 0;

	while (iterator.next(&current))
		total += current;

	return total;
}

struct iter int range(int first, int last)
{
	for (int value = first; value <= last; value++)
		yield value;
}
```

The `finally delete` cleanup is not decoration. It makes sure the iterator
state is destroyed if the loop exits early or an error leaves the function.

Iterator callable values can also be called directly:

```camp
nuint countValues(iter int values)
{
	int current = 0;
	nuint count = 0;

	while (values(&current))
		count++;

	values(null);
	return count;
}
```

Passing `null` as the current slot is the low-level cleanup call for an
iterator callable value. Prefer `foreach` or `finally delete` unless you are
intentionally working at this protocol level.

## Cleanup

Iterator cleanup is deterministic. It runs when iteration finishes normally,
when `break` exits a loop, or when an error leaves the loop.

A generator can register cleanup in its body:

```camp
struct iter int numbersWithScratch()
{
	finally releaseScratch();

	yield 1;
	yield 2;
}
```

For a `struct iter`, cleanup destroys the generated fixed-struct state in
place. For a `class iter`, cleanup follows the generated class destruction
path.

If the iterator retains values that own resources, use the same cleanup
features you would use outside an iterator: `finally`, `finally delete`,
cleanup methods, and explicit ownership in the API contract.

## ABI Shape For `struct iter`

A `struct iter` generator lowers to caller-owned state plus helper functions.
For this Camp generator:

```camp
struct iter int range(int first, int last)
{
	for (int value = first; value <= last; value++)
		yield value;
}
```

A C-facing model is roughly:

```c
typedef struct rangeIter rangeIter;

struct rangeIter {
	int state;
	int first;
	int last;
	int value;
};

void range(int first, int last, rangeIter *state);
bool rangeIter_next(rangeIter *state, int *current);
void rangeIter_destroy(rangeIter *state);
```

The names and fields are illustrative, but the ownership story is real:
the caller has the state storage, advances it, and destroys it.

For an instance generator, the generated state also retains the original
receiver. Inside the generator body, `this.member` continues to mean the
source receiver's member, not a field on the generated iterator state.

That is why `struct iter` is the natural form for short-lived local iteration
and adapters that do not need heap allocation.

## ABI Shape For `class iter`

A `class iter` generator uses generated class state:

```camp
class iter int repeated(int value, nuint count)
{
	for (nuint index = 0; index < count; index++)
		yield value;
}
```

Its C-facing model is closer to an opaque handle:

```c
typedef struct repeatedIter repeatedIter;

repeatedIter *repeated(int value, size_t count);
bool repeatedIter_next(repeatedIter *state, int *current);
void repeatedIter_destroy(repeatedIter *state);
```

That makes `class iter` a better fit when iterator state must be stored,
returned through escaped surfaces, or hidden behind an ABI boundary. The trade
is the ordinary class trade: allocation and escaped lifetime requirements are
now part of the shape.

## Iterator Values As Callables

Iterator values participate in Camp's callable model. A simple iterator value
is conceptually a call target plus a context pointer:

```camp
fn bool(void*, int*) rawNext;
void* rawContext;
```

The source form is still `iter int`, not a naked pair of fields:

```camp
iter int values;
```

This is why iterator values can be passed around and adapted without a special
runtime collection interface. It is also why the ABI remains understandable to
C code: advancing an iterator is ultimately a function call with a context and
a current-value pointer.

A manually written function with a similar low-level shape is not
automatically an iterator. Use an iterator type, a generated iterator, a
callable newtype, or an explicit conversion when the value is meant to
participate in iterator semantics.

Callable newtypes can name iterator contracts:

```camp
newtype iter nuint ByteReader(byte[] buffer);
newtype iter nuint ByteWriter(const byte[] buffer);
```

Those names are useful for stream-like APIs. A reader fills a caller-provided
buffer and yields how many bytes were read. A writer consumes a buffer and
yields how many bytes were accepted.

## Arrays And `foreach`

Arrays are built into `foreach` because they are already pointer-plus-length
sequence views:

```camp
nuint countNonZero(const byte[] bytes)
{
	nuint count = 0;

	foreach (byte value in bytes)
	{
		if (value != 0)
			count++;
	}

	return count;
}
```

In generic code, array iteration follows the same rules as other generic array
operations. If the element type is only `T: any`, the loop should usually work
with element storage instead of pretending that each element can be copied as a
source value:

```camp
void visitEach<T: any>(T[] values, fn void(T* value) visit, sizeof(T))
{
	for (nuint index = 0; index < values.length; index++)
		visit(&values[index]);
}
```

`sizeof(T)` supplies the element stride. The callback receives a pointer to the
element's storage form, so this works for fixed structs, fixed-size arrays,
classes, expanded forms, and ordinary scalar values. If the loop body copies,
returns, yields, or keeps the element value in a local, the generic signature
also needs `T: copyable`.

For fixed structs, classes, fixed-size arrays, and other non-copyable element
forms, prefer APIs that expose addresses or other storage-aware views rather
than copying elements out by value.

## Generic Iterators

Generic iterators obey the same generic rules you learned in the previous
chapter:

```camp
struct iter T iterate<T: copyable>(T[] values, sizeof(T))
{
	foreach (auto value in values)
		yield value;
}
```

This iterator copies each yielded `T`, so it requires `T: copyable`. It also
asks for `sizeof(T)` because the generated state and array enumeration need
the element size.

A generic adapter over an existing iterator may work under `T: any` if it does
not copy yielded values in a way the constraint forbids:

```camp
nuint countIterator<T: any>(iter T values, sizeof(T))
{
	T current = default;
	nuint count = 0;

	while (values(&current))
		count++;

	values(null);
	return count;
}
```

For `T: any`, the `T* current` slot points at the storage form of `T`. If `T`
is an expanded form, that means materialized storage. If `T` is a fixed struct
or fixed-size array, that means storage for the fixed value.

## Iterators And Expanded Values

An iterator yields exactly one source value. That value can itself be an
expanded Camp value when the language permits it:

```camp
struct iter const char[] words()
{
	yield "alpha";
	yield "omega";
}
```

The current-value storage follows the expanded-form rules for the yielded type.
For arrays and counted text, be careful about the storage behind the yielded
view. String literals and caller-owned buffers can be fine. A span into local
temporary storage is not.

When the current item has several fields, prefer a named struct:

```camp
struct Line
{
	nuint number;
	const char[] text;
}

struct iter Line lines(unscoped const char[] source)
{
	yield { .number = 1, .text = source[..5] };
}
```

That keeps the iterator's single yielded value honest while still giving
callers a rich item.

## Choosing `struct iter` Or `class iter`

Use `struct iter` when:

- the iterator is short-lived;
- caller-owned state is enough;
- avoiding allocation matters;
- retained references can be proven not to outlive their anchors;
- exposing the generated state shape is acceptable for this API.

Use `class iter` when:

- iterator state must be stored or returned through escaped surfaces;
- the state should be opaque at an ABI boundary;
- the ABI should remain stable even if the iterator's internal state changes;
- class identity or heap allocation is the right model;
- retained pointer-bearing values are already escaped.

This choice is part of the API. It affects allocation, lifetime requirements,
generated type shape, and cleanup. A `struct iter` exposes its generated
fixed-struct state over the ABI; changing the generator's retained state can
therefore become an ABI change. A `class iter` keeps that state private behind
an opaque class pointer, which gives the implementation more room to evolve
without breaking callers.

## Common Mistakes

Do not put `thrown` in a generator parameter list. Put it in the iterator type:

```camp
struct iter(int value, thrown ReadError error) readValues()
{
	...
}
```

Do not yield several ordinary values. Use a named struct.

Do not yield a span view to local temporary storage.

Do not treat `iter T` as a collection. It is a stateful protocol value and
must be cleaned up.

Do not use `class iter` with scoped retained pointers. Escaped state requires
escaped retained values.

When in doubt, start with `foreach`. Drop down to manual `next(...)` only when
you need explicit protocol control, ABI adaptation, or an iterator adapter that
cannot be expressed cleanly as a `foreach` loop.
