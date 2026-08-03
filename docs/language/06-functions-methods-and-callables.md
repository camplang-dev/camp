+++
nav_title = "6. Functions And Callables"
+++

# Functions, Methods, And Callables

Camp function signatures look familiar, but they carry more of the API contract
than a C signature usually does. A signature can say where outputs go, how
errors move, which allocation context is active, what the receiver is, whether
a callable carries context, and whether a callback is meant to run once.

This chapter is a reference for reading and writing callable surfaces: ordinary
functions, methods, named and default arguments, `out`, `thrown`, `within`,
receiver methods, function values, delegates, `once` callbacks, callable
newtypes, lambdas, method references, and overloads.

The details can look dense at first. The guiding idea is simple: a Camp
signature is not only a list of values. It is the contract for how a call moves
values, errors, allocation, and sometimes execution itself.

## Function Declarations

A function declaration has a return type, name, parameter list, and either a
body or a valid bodyless surface.

```camp
int add(int left, int right)
{
	return left + right;
}
```

Read the declaration in layers:

| Layer | Example | Meaning |
|---|---|---|
| visibility and behavior | `export`, `public`, `internal`, `extern` | Where the function is visible and who implements it. |
| result | `int` | Primary success result. |
| name | `add` | Source name used by callers. |
| parameters | `(int left, int right)` | Inputs and special call slots. |
| body or surface | `{ ... }`, `=>`, or `;` | Block body, expression body, or bodyless contract. |

For exported functions, the source signature is part of the API and ABI
contract. Special forms such as arrays, delegates, `out`, `thrown`, `within`,
and generic capabilities may lower to more than one ABI component, but the
source signature is what callers read and write.

## Parameters And Arguments

Ordinary parameters are inputs:

```camp
int clamp(int value, int low, int high)
{
	if (value < low)
		return low;
	if (value > high)
		return high;
	return value;
}
```

Arguments are usually positional:

```camp
int volume = clamp(requested, 0, 100);
```

Named arguments use `name: value` and are useful when a call has several
defaulted or same-typed parameters:

```camp
void record(int a = 1, int b = 2, int c = 3)
{
}

record(c: 30, a: 10);
record(100, c: 300);
```

Named arguments clarify binding to parameters. They do not make a bad call
valid if required arguments are still missing.

## Default Arguments

Parameters can have default values:

```camp
int find(const char[] value, bool caseSensitive = false)
{
	return caseSensitive ? (int)value.length : 0;
}

int length = find("hello");
int checkedLength = find("hello", caseSensitive: true);
```

Defaults are part of the declaration surface. In public APIs, keep them simple
and stable. A default that depends on private implementation details can make an
API harder to call correctly from generated metadata, wrappers, or other
languages.

Library APIs that need logging, assertion, or property-bag convenience can use
source-capture defaults. These intrinsics are valid only as parameter default
values:

```camp
void assert(bool condition, string expression = sourceof(condition), string file = caller(sourcefile), uint line = caller(sourceline));
string getPropertyValue(string key = caller(propertyname));
```

`sourceof(parameterName)` captures the source text that the caller wrote for
that argument. `caller(...)` captures facts about the caller, such as source
file, source line, function name, qualified name, or property name.

When a function with defaults is converted to a callable value, the compiler
may need a wrapper that supplies omitted arguments. That is still the same
source contract: callers can omit the defaulted slots where the callable shape
allows it.

## `out` Parameters

Use `out` when a function should write an additional success result into
caller-provided storage.

```camp
bool tryParsePort(const char[] text, out int port)
{
	if (text.length == 0)
	{
		port = default;
		return false;
	}

	port = 80;
	return true;
}
```

Callers bind an output slot with `out`:

```camp
int port = default;
if (tryParsePort(text, out port))
	usePort(port);
```

An `out` parameter is not a hidden return tuple. It is part of the callable
shape. That matters for overloads, callable values, async lowering, and native
ABI design.

The function must assign each `out` parameter before it returns, even on a
failure path. That rule keeps the caller from accidentally reading uninitialized
storage after a failed try-style call.

Use `out` for a small number of additional results. If a function naturally
returns several related values, a named result struct may be clearer.

## `thrown` Parameters And `catch` Arguments

Camp uses explicit error values rather than runtime exception handling. The
keywords are familiar, but their job is closer to the error-return patterns C
libraries already use: a value travels through an explicit error slot instead
of unwinding the stack through a runtime exception system.

A function that can fail can expose a `thrown` slot:

```camp
enum ParseError
{
	OK = 0,
	EMPTY
}

int parsePort(const char[] text, thrown ParseError error)
{
	if (text.length == 0)
		throw ParseError.EMPTY;

	return 80;
}
```

Callers can catch the value at the call site:

```camp
ParseError error = default;
int port = parsePort(text, catch error);
```

If the caller has a compatible `thrown` slot, the error can propagate through
the caller. If not, the call must catch or discard it deliberately.

`thrown` is a callable slot, not a general everyday type. Use optionals for
ordinary absence; use `thrown` when failure should participate in explicit
error flow.

## Intentional Discards

Sometimes ignoring a result is the clearest thing to do, but Camp wants that
choice to be visible. `_` is a write-only discard. Use it when a value, `out`
slot, or `catch` slot is produced and the program is intentionally not keeping
it.

```camp
_ = pollDevice();
```

For `out` and `catch`, the discard occupies the caller-provided storage slot
without giving you a local variable:

```camp
tryParsePort(text, out _);
parsePort(text, catch _);
```

Each discard is separate hidden storage as needed. You cannot read from `_`,
declare it as an ordinary local, or use it as a real name. Treat it as a small
signal to the next reader: "yes, this value exists, and this code is choosing
not to use it."

## `within` Parameters And Allocation Context

`within` carries allocation context through a signature.

```camp
Buffer* createBuffer(nuint capacity, within allocator)
{
	return new Buffer(capacity);
}
```

Callers can supply an allocation context explicitly:

```camp
Buffer* buffer = within (arena) createBuffer(4096);
```

Or they can select a context for a block or expression:

```camp
within (arena)
{
	Buffer* buffer = new Buffer(4096);
	delete buffer;
}
```

`within` is not just a naming convention. It affects `new`, `delete`,
delegate-context allocation, async-frame allocation where applicable, and
generated cleanup that must return storage to the correct allocator.

## Caller-Prepared Results

Some APIs produce a result whose size is not known until the function looks at
its inputs. Text formatting, byte serialization, and platform-style queries
often use the same pattern: first ask how much storage is needed, then call
again with a buffer to receive the result.

Camp gives that pattern a direct signature shape with `prep`. A `prep`
parameter is the result buffer supplied by the caller, and the function returns
the length needed for the complete result:

```camp
nuint toString(in Status this, prep char[] buffer = default)
{
	const char[] text = this.enabled ? "enabled" : "disabled";
	nuint count = min(text.length, buffer.length);

	for (nuint index = 0; index < count; index++)
		buffer[index] = text[index];

	return text.length;
}
```

Omit the buffer in an ordinary call when you want the prepared result. The
compiler asks for the required length, creates scoped storage, and calls the
method again to fill it:

```camp
char[] text = status.toString();
```

Use the explicit buffer form when you want to reuse storage. Supplying the prep
parameter makes this an ordinary call that returns the required length:

```camp
nuint required = status.toString(buffer: default);
char[] storage = init char[required];
status.toString(buffer: storage);
```

An omitted buffer can allocate input-sized scoped storage, so keep the result
when it will be used repeatedly. For large or repeated results, measuring once
and reusing explicit storage can reduce stack use and repeated work.

If the prepared result needs allocated lifetime, put `(new)` directly before
the call and use the ordinary allocation context. Keep the owning array so it
can be cleaned up:

```camp
char[] text = within (default) (new) status.toString();
// ...
delete text.elements;
```

Prep-bearing getter methods use explicit method-call syntax. This keeps an
allocation-producing operation visible instead of making it look like a field:

```camp
struct Control
{
	nuint getText(const this, prep char[] buffer = default)
	{
		const char[] text = "Ready";
		nuint count = min(text.length, buffer.length);

		for (nuint index = 0; index < count; index++)
			buffer[index] = text[index];

		return text.length;
	}
}

char[] label = control.getText();
```

The receiver and ordinary arguments are evaluated once. The generated sizing
and writing calls reuse those values, so `next().toString()` does not call
`next()` twice. The `prep` modifier remains part of callable and interface
contracts; calls through a prep-bearing surface get the prepared-result
behavior, while calls through an ordinary mutable-array surface remain ordinary
calls.

## Methods And Receiver Calls

A method is a function associated with a type. The receiver is the value the
method is called on.

```camp
class Counter
{
	int value;

	void add(int amount)
	{
		this.value += amount;
	}

	int getValue()
	{
		return this.value;
	}
}
```

Call methods with member syntax:

```camp
counter.add(3);
int value = counter.getValue();
```

Camp uses `.` for both value and pointer receivers. There is no separate `->`.
Receiver type controls whether the call mutates, dispatches virtually, calls
through an interface, or captures receiver context in a method reference.

Get-style methods have an implicit const receiver. Other methods can write the
receiver explicitly:

```camp
int computeTotal(const this)
{
	return this.value + 10;
}
```

## Property Accessors

Camp property syntax is method syntax with a nicer face. There is no separate
runtime property object, no hidden backing field, and no different ABI rule.
When a method has an accessor-shaped name and signature, the compiler can
rewrite property syntax to the method call.

```camp
class Counter
{
	int value;

	int getValue()
	{
		return this.value;
	}

	void setValue(int value)
	{
		this.value = value;
	}
}

int count = counter.Value; // counter.getValue()
counter.Value = count + 1; // counter.setValue(count + 1)
```

Named accessors begin with `get` or `set` followed by the property name. A
getter reads; a setter takes the assigned value as its final ordinary
parameter and returns `void`. Get-style instance methods have the same implicit
`const this` receiver rule you saw above, so a simple getter is normally a
non-mutating operation.

Indexed properties use ordinary parameters before the setter value:

```camp
class ReadingList
{
	int[] values;

	nuint getLength()
	{
		return this.values.length;
	}

	int getItem(@index nuint index)
	{
		return this.values[index];
	}

	void setItem(@index nuint index, int value)
	{
		this.values[index] = value;
	}

	int get(@index nuint index)
	{
		return this.values[index];
	}

	void set(@index nuint index, int value)
	{
		this.values[index] = value;
	}
}

int last = readings.Item[^1];
readings.Item[0] = 10;
```

The exact method names `get` and `set` create a nameless indexer surface:

```camp
int first = readings.[0];
readings.[1] = 20;
```

Overloaded setters are useful when the index or key is stable and the assigned
value chooses the stored type:

```camp
class JsonArray
{
	void setElement(@index nuint index, overload int value)
	{
	}

	void setElement(@index nuint index, overload bool value)
	{
	}

	void setElement(@index nuint index, overload string value)
	{
	}
}

json.Element[0] = 10;
json.Element[1] = true;
json.ElementString[2] = null;
```

The concrete property name, such as `ElementString`, is available because the
concrete accessor method is named `setElementString` after overload naming is
applied. Use the concrete form when the assigned value has no type of its own,
such as `null`, or cast the value in the ordinary property form.

After rewriting, the call follows the method's normal rules. `thrown` slots,
`await`, generic arguments, overloads, visibility, constness, and lifetimes all
belong to the accessor method. If the method call would need `catch`, the
property form needs it too. If the getter is async, write `await value.Name`;
Camp does not insert `await` for you.

Property rewriting is a fallback. If a visible field or member named `Value`
already exists, `counter.Value` binds to that member rather than rewriting to
`getValue()`. Use accessors when the surface should feel field-like; call the
method directly when the operation is heavy, surprising, or iterator-like.

## Receiver-Preserving `this` Returns

An instance method can return `this` to preserve the receiver's static shape.
This is useful for fluent APIs.

```camp
class Builder
{
	int count;

	this add(int value)
	{
		this.count = this.count + value;
		return this;
	}

	this addTwo()
	{
		return this.add(2);
	}
}
```

At the call site, the result keeps the receiver's static type:

```camp
FancyBuilder* builder = new FancyBuilder();
FancyBuilder* same = builder.add(3).addTwo();
```

`this` as a return type means "the receiver," not "some value of this class."
A method returning `this` should return the receiver or a chain of calls on the
receiver that also returns `this`.

## Callable Type Families

Camp has several callable families. Choose the one that matches the ownership
and invocation contract.

| Form | Meaning |
|---|---|
| `fn R(P)` | Direct function target, no captured context. |
| `delegate R(P)` | Callable target plus context. |
| `once R(P)` | Context-carrying callable intended for one invocation. |
| `iter T` | Iterator protocol surface. |
| `async R(P)` | Callback-shaped async callable. |

Examples:

```camp
fn int(int value) transform;
delegate void(const char[] message) logger;
once void() completion;
```

The return type, parameter list, `out` slots, `thrown` slots, lifetimes,
call-specs, and generic capabilities are part of the callable type. Signature
compatibility does not insert arbitrary caller-side conversions. If a callback
needs a different shape, write an adapter.

## `fn` Values

Use `fn` for direct functions without captured context.

```camp
int doubleValue(int value)
{
	return value * 2;
}

fn int(int) transform = doubleValue;
int result = transform(21);
```

A `fn` value is not the same as a `delegate`. It has no hidden context. That
makes it useful for plain function tables and native callback surfaces that
expect only a function target.

Raw `fn*` erases the signature:

```camp
fn* raw = (fn*)transform;
fn int(int) recovered = (fn int(int))raw;
```

`fn*` is not callable until it is recovered to a concrete function type. Use it
only at low-level boundaries where signature erasure is deliberate.

## Delegates And Captured Context

Use `delegate` when a callable may carry context: a bound method, a lambda with
captured values, or a callback object.

```camp
newtype delegate int Mapper(int value);

int apply(Mapper mapper, int value)
{
	return mapper(value);
}
```

Lambdas can target delegates:

```camp
int offset = 4;
Mapper mapper = (int value) => value + offset;
int result = apply(mapper, 6);
```

If a delegate escapes, its captured context must also be safe to retain. When
you allocate delegate context explicitly, you are responsible for the cleanup
contract exposed by the producer.

```camp
void registerCompletion(escaped delegate void() completion);

void waitForSignal(int signalId)
{
	registerCompletion(new delegate () =>
	{
		notify(signalId);
		delete delegate;
	});
}
```

`delete delegate` is valid only inside a `new delegate` lambda. It deletes the
generated delegate context when there is one, and does nothing for a valid
`new delegate` lambda whose context is null. For a void-returning lambda, it
can be the final statement. For a non-void lambda, register it with `finally`
so it runs after the return value is produced:

```camp
escaped delegate int() makeOneShotScore(int baseScore)
{
	return new delegate () =>
	{
		finally delete delegate;
		return baseScore + 10;
	};
}
```

Use this only when the callback contract makes self-cleanup correct, such as a
one-shot native callback that will not call the delegate again. When you use
`finally delete delegate`, make it the final cleanup registration in that
lambda. It is invalid in `fn` lambdas, scoped delegate lambdas, direct `once`
lambdas, and ordinary lambdas that did not use `new delegate`. Do not delete
`.context` directly; the generated context layout is compiler-owned. The
source contract is that a delegate has callable behavior plus context.

## `once` Callbacks

Use `once` when the API contract says a callable is consumed by a single
invocation.

```camp
int runOnce(once int() callback)
{
	return callback();
}

int value = 4;
once int() later = () => value + 1;
int result = runOnce(later);
```

`once` is common for completions, postponed calls, async continuation shapes,
and ownership-transfer callbacks.

`once` does not automatically mean "delete whatever context exists." The
producer of the callable owns that cleanup rule. Some generated once contexts
delete themselves after invocation; a `once` value received from another API is
just a one-call contract unless that producer says more.

## Callable Newtypes And Ascription

Callable newtypes give callback shapes meaningful names.

```camp
newtype delegate int Mapper(int value);
newtype fn bool ParseInt(const char[] text, out int value);
```

Use them when the role matters more than the raw signature. Two callbacks may
both be `delegate void()` but mean very different things: completion, cleanup,
notification, retry, cancellation. A newtype lets the API say which role it
expects.

A function or method can be ascribed to a callable newtype:

```camp
bool tryParseInt(const char[] text, out int value) : ParseInt
{
	value = 0;
	return text.length != 0;
}
```

Ascription is useful for public APIs, overload resolution, metadata, and
documentation. It names the callable contract without requiring callers to
reverse-engineer it from a raw signature.

## Lambdas

Lambdas are target-typed by the callable type expected at the assignment or
call site.

```camp
delegate int(int value) doubleValue = value => value * 2;
```

Use explicit parameter types when they make the example or API clearer:

```camp
Mapper mapper = (int value) => value + 4;
```

Captures are inferred from the lambda body. A scoped delegate can capture
scoped values. An escaped delegate can capture only values that are safe to
store in its generated context. This is where callables meet lifetimes: a
callback that runs later must not retain a pointer to storage that disappeared
earlier. A later chapter explains lambda lifetimes and capture storage in more
detail.

## Method References

A method reference can become a callable value. If the receiver is known, the
callable captures the receiver as context. If the receiver is not known, the
callable may expect a receiver parameter.

```camp
newtype delegate void ClickAction();

class Button
{
	void click()
	{
	}

	void notify() : ClickAction
	{
	}
}

auto handler = button.click;    // inferred as delegate void()
delegate void() again = handler;

auto action = button.notify;    // inferred as ClickAction
ClickAction named = action;
```

Method references preserve receiver constness, lifetime facts, virtual
dispatch, interface dispatch, and callable ascription. When the method is
ascribed to a callable newtype, `auto` keeps that named callable type instead
of reducing the value to only its raw delegate shape.

When a method reference crosses an API boundary, prefer a named callable
newtype so the callback's role is clear.

## Overloads And Overload Selectors

Camp supports a deliberately limited overload model. It is not trying to be
C++ or C# overload resolution. It exists for cases where one source-level call
name really should cover a small family of ABI-visible operations, while still
making the distinguishing parameter explicit in source and metadata.

An overload family uses an `overload` selector parameter:

```camp
class Writer
{
	static void write(overload int value)
	{
	}

	static void write(overload string value, bool newline = false)
	{
	}
}
```

The selector can also appear after stable parameters, which is common for
indexed containers and property bags:

```camp
class PropertyBag
{
	bool tryGet(const char[] key, overload out int value)
	{
		return false;
	}

	bool tryGet(const char[] key, overload out bool value)
	{
		return false;
	}

	void set(const char[] key, overload int value)
	{
	}

	void set(const char[] key, overload bool value)
	{
	}
}
```

Callers write the ordinary call:

```camp
Writer.write(123);
Writer.write("ready");
bag.set("retryCount", 3);

if (bag.tryGet("enabled", out bool enabled))
{
}
```

The compiler chooses the candidate from the selector argument. Values such as
`null`, `default`, aggregate initializers, and untyped lambdas need a concrete
target type before they can select an overload, so use a cast or call the
concrete overload name when the selector value would otherwise be typeless.
When the compiler cannot infer the intended callable, bind the target to an
explicitly typed local or wrap it with a small adapter.

```camp
fn int(int) selected = clampPositive;
```

Across the ABI, overloaded functions still have distinct symbols. The
overload selector contributes to the flattened name, so a pair of exported
functions like this:

```camp
export extern void write(overload int value);
export extern void write(overload const char[] value);
```

can be represented as separate C-facing symbols such as `writeInt` and
`writeCharArray`, while Camp callers write the smaller source form:

```camp
write(123);
write("ready");
```

Use overloads for compact, type-directed API families. For broader behavior
differences, distinct function names, options structs, default arguments, or
separate result types are usually clearer.

## Async And Iterator Surfaces At A Glance

Async and iterator declarations build on the same callable-shape ideas.

An async function is callback-shaped at the boundary. Its completion is
`once`-like. Its signature can still include ordinary parameters, `thrown`
slots, `within` allocation context, and receiver information.

An iterator has protocol state and a callable surface that produces values over
time. It can also participate in `thrown` flow and cleanup.

The async and iterator chapters go deeper. For now, read their signatures the
same way you read any other Camp callable:

- What values go in?
- What values come out?
- Where do errors go?
- Is there an allocation context?
- Does the callable carry or retain context?
- Is the callable single-use?

## Choosing The Right Callable Form

Use this table as a first pass:

| Need | Prefer |
|---|---|
| Ordinary named behavior | function or method |
| Additional success output | `out` parameter or result struct |
| Explicit error flow | `thrown` slot |
| Allocation context | `within` parameter or `within` expression |
| Context-free callback | `fn` |
| Callback with receiver or captures | `delegate` |
| Single-use completion | `once` |
| Meaningful public callback role | callable `newtype` |
| Fluent method returning the receiver | `this` return |
| Dynamic behavior on a class hierarchy | virtual method |
| Dynamic behavior through a contract | interface method |

When in doubt, make the callable's contract visible in the signature. Camp's
call syntax stays familiar because the signature does the honest work.
