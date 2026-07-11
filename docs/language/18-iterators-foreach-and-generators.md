# Iterators, `foreach`, And Generators

Camp iterators are explicit state machines with a small protocol surface. The
language provides `iter`, `yield`, and `foreach` because they are readable, but
the underlying model remains ordinary storage plus ordinary functions.

That model matters for users: iterator values need cleanup, generator
parameters are retained in state, failing iterators expose `thrown` slots, and
generic iterators obey the same copyability and size-capability rules as other
generic code.

## Iterator Type Forms

The ordinary iterator callable type is `iter T`:

```camp
iter int values;
```

The parenthesized form is used when the iterator includes a thrown slot:

```camp
iter(int, thrown RangeError) checkedValues;
```

An iterator type has exactly one yielded type and may optionally end with one
`thrown` type.

Valid forms:

```camp
iter int
iter(int)
iter(int, thrown RangeError)
```

Invalid forms:

```camp
iter(thrown RangeError)             // ERROR
iter(int, int)                      // ERROR
iter(int, thrown RangeError, int)   // ERROR
iter(int, thrown A, thrown B)       // ERROR
iter(in int)                        // ERROR
```

The yielded type is an ordinary value slot. The thrown slot is an error slot,
not a yielded value.

## Plain Iterator Values

A function may return an iterator value without being a generator.

```camp
iter int getValues(int first, int last)
{
	return createRangeIterator(first, last);
}
```

This function runs when called and returns an existing iterator value. It does
not use `yield` and does not create a generated state type merely because its
return type is `iter int`.

## Generator Declarations

A function body may use `yield` only when the declaration chooses a generated
iterator state container:

```camp
struct iter int range(int first, int last)
{
	for (int value = first; value <= last; value++)
		yield value;
}
```

```camp
class iter int powersOfTwo(int count)
{
	int value = 1;

	for (int index = 0; index < count; index++)
	{
		yield value;
		value *= 2;
	}
}
```

The prefix selects the state container:

| Form | Generated state container | Typical use |
|---|---|---|
| `struct iter T` | generated fixed struct | zero-allocation local iteration |
| `class iter T` | generated class | stored, returned, or exported iteration |

Both generated types are real Camp-visible types. The generated name appends
`Iter` to the generator name, such as `rangeIter`.

## Generator Parameters

Calling a generator stores its arguments in the generated iterator state. The
generator body starts when the iterator is advanced, not when the generator is
called.

Because parameters are retained state, a generator parameter list may not
contain:

- `in` parameters;
- `out` parameters;
- trailing `thrown` parameters.

Default arguments are allowed.

```camp
struct iter int repeat(int value, nuint count = 1)
{
	for (nuint index = 0; index < count; index++)
		yield value;
}
```

Pointer-bearing parameters must be valid for the generated state container.
For `struct iter`, retained pointers constrain the lifetime of the iterator
state. For `class iter`, retained pointer-bearing values must be escaped
because the generated class state is escaped. This includes a member generator
receiver; a `class iter` member needs an escaped receiver surface.

## Failing Iterators

Iterator failure is part of the iterator type:

```camp
enum IterError
{
	OK = 0,
	FAILED
}

struct iter(int value, thrown IterError error) numbers(int failAt)
{
	yield 1;

	if (failAt == 2)
		throw IterError.FAILED;

	yield 3;
}
```

The yielded type appears first. The optional `thrown` type appears inside the
`iter(...)` type form.

A generator parameter list does not declare `thrown` because calling the
generator only creates state. The body executes when the iterator's
`next(...)` protocol is driven.

An ordinary function returning an iterator may still fail while preparing that
iterator:

```camp
export iter char chars(const char[] text, within allocator, thrown TextError)
{
	return charsOwned(text.copyString(within allocator));
}
```

Here the function can report allocation or preparation failure before returning
the iterator value. The iterator itself has whatever thrown shape its own type
declares.

## `yield`

`yield` writes the next logical value through caller-provided storage and
suspends the generator state until the next advance.

```camp
struct iter int firstThree()
{
	yield 1;
	yield 2;
	yield 3;
}
```

The yielded expression must be assignable to the iterator's yielded type. A
generator cannot yield several ordinary values at once. Use a named struct
when one logical item contains multiple fields:

```camp
struct Point
{
	int x;
	int y;
}

struct iter Point points()
{
	yield { .x = 1, .y = 2 };
}
```

Lifetime checks apply to yielded values. For example, yielding a span view to
local fixed-size array storage is invalid because the view would outlive the
storage.

## Iterator Protocol

For a generated iterator state type `Y` yielding `T`, the basic protocol is:

```camp
bool next(Y* this, T* current);
```

Where:

- `this` points at iterator state;
- `current` points at caller-provided storage for the next yielded value;
- the boolean result indicates whether a value was produced.

For a failing iterator, the error slot follows the current-value slot:

```camp
bool next(Y* this, T* current, thrown E);
```

As elsewhere in Camp, the default value of the error type means success.

The exact helper names vary by generated state type and visibility, but the
source model is stable: iterator advancement is `next(...)` plus deterministic
cleanup.

## `foreach`

`foreach` drives arrays and iterators.

```camp
foreach (int value in values)
	sum += value;
```

```camp
foreach (auto value in range(1, 5))
	Console.writeLine(value);
```

The loop variable is scoped to the loop body. `break` exits the loop and runs
iterator cleanup. `continue` advances to the next item.

For arrays, `foreach` enumerates element storage in order. For iterator values,
`foreach` repeatedly calls the iterator protocol until it returns false or
throws.

## `foreach` And Thrown Flow

A failing iterator's thrown slot participates in ordinary error flow.

```camp
int sumUntilFailure(thrown IterError error)
{
	int total = 0;

	foreach (auto value in numbers(2))
		total += value;

	return total;
}
```

The loop can also be protected by `try` / `catch`:

```camp
try
{
	foreach (auto value in numbers(2))
		total += value;
}
catch (IterError error)
{
	total = 0;
}
```

If the loop body does not catch the error and the enclosing function has no
compatible thrown slot, the compiler reports the unhandled thrown value.

## Manual Iteration

The iterator protocol can be driven manually when a loop needs precise control.

```camp
struct iter int range(int start, int count)
{
	for (int index = 0; index < count; index++)
		yield start + index;
}

void sample()
{
	rangeIter sequence = range(5, 3) finally delete;
	int current = 0;

	while (sequence.next(&current))
	{
		log(current);
	}
}
```

Manual iteration is useful at low-level boundaries, but `foreach` is the
ordinary source form for most code.

## Iterator Cleanup

Iterator state is cleaned up deterministically. Cleanup runs when iteration
finishes normally, when a loop exits early, or when an error leaves the loop.

Generators can register cleanup in their bodies:

```camp
struct iter int numbers()
{
	finally releaseScratch();

	yield 1;
	yield 2;
}
```

For a `struct iter`, cleanup destroys the fixed-struct state in place. For a
`class iter`, cleanup follows the generated class destruction path.

If a yielded value or retained parameter owns resources, make ownership visible
with ordinary cleanup constructs such as `finally delete` or cleanup methods.

## Arrays And `foreach`

Arrays are built into `foreach` because they are the most common sequence
shape.

```camp
void fill(byte[] buffer, byte value)
{
	foreach (byte current in buffer)
	{
		...
	}
}
```

A loop variable receives the current element according to the element type and
copyability rules. For fixed structs and other non-copyable element forms, use
APIs that expose addresses or references rather than copying values out.

In generic code, enumerating `T[]` under `T: any` requires `sizeof(T)` so the
compiler can compute element stride. Copying element values requires
`T: copyable`.

## Iterator Values As Callables

Iterator values fit into Camp's callable model. They carry a call target and
context that ultimately drive `next(...)`.

```camp
fn bool(void*, int*) rawNext;
iter int values;
```

A manually written callable with the same low-level shape is not automatically
an iterator. Use the keyworded iterator form or an explicit `(iter)` cast when
the value should participate in iterator semantics.

Callable newtypes can name iterator contracts:

```camp
export newtype iter nuint ByteReader(byte[] buffer);
```

Such a value can be called directly or used in `foreach` when it has the
iterator shape expected by the language and library.

## Generic Iterators

Generic iterators obey ordinary generic rules.

```camp
struct iter T iterate<T: copyable>(T[] values, sizeof(T))
{
	foreach (auto value in values)
		yield value;
}
```

Copying `T` values requires `T: copyable`. Merely walking storage under
`T: any` may be valid when the code has `sizeof(T)` and avoids copying.

For `T: any`, `T* current` points at the storage form of `T`: materialized
storage for compiler-expanded forms, or the fixed instance/storage object for
fixed structs, classes, and fixed-size arrays.

## Iterators And Expanded Values

An iterator yields exactly one source value. That value may itself be an
expanded form when the language permits it:

```camp
struct iter const char[] lines()
{
	yield "alpha";
	yield "omega";
}
```

The current-value storage then follows the expanded-form rules for the yielded
type. For arrays and strings, be careful that yielded views do not point to
storage that becomes invalid before the caller consumes the value.

When an iterator needs to expose several related fields, prefer a named struct:

```camp
struct Token
{
	TokenKind kind;
	const char[] text;
}

struct iter Token tokens(const char[] source)
{
	...
}
```

## `struct iter` Versus `class iter`

Use `struct iter` when:

- the iterator is short-lived;
- the state can live in caller-owned storage;
- zero-allocation iteration is desirable;
- retained references can be proven not to outlive their anchors.

Use `class iter` when:

- the iterator must be stored or returned through escaped surfaces;
- the state needs class identity or heap allocation;
- retained pointer-bearing values are escaped;
- exported API shape benefits from an opaque state object.

This choice is part of the API contract. It affects allocation, lifetime
requirements, generated state shape, and cleanup.

## Common Pitfalls

Do not put `thrown` in a generator parameter list. Put it in the iterator type:

```camp
struct iter(int value, thrown IterError error) readValues()
{
	...
}
```

Do not yield multiple values. Use a named struct.

Do not return or yield views to local fixed-size array storage.

Do not assume `iter T` is a collection. It is a stateful callable protocol and
must be cleaned up.

Do not use a `class iter` with scoped retained pointers. Escaped state requires
escaped retained values.
