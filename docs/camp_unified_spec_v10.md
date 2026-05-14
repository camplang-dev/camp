
# 1. Basic Types

Camp keeps its core types small, explicit, and ABI-visible.

This section defines the type forms that appear everywhere else in the language:

- primitive scalar types
- pointer types
- plain function types
- ordinary function declarations and calls
- grouped `params` types and their storage counterpart forms
- built-in grouped forms such as arrays, optionals, delegates, and strings
- enum types
- `newtype`
- the conversion rules that connect these forms

The design goal is not to make types magical. The design goal is to make them predictable. A Camp type should tell the reader what kind of value exists, how that value travels through calls, and what shape the value has at the ABI boundary.

Several later language features build directly on the material in this section. In particular:

- `params` explains why Camp can model multiple values, arrays, optionals, delegates, and strings in one consistent way.
- `fn`, `delegate`, and ordinary function declarations explain Camp’s callable surface before async, iterators, and lambdas add further structure.
- the conversion rules here explain how narrow nominal forms such as `String` relate to wider nominal forms such as `StringView`, how structural grouped values become nominal ones, and how grouped values become real storage when storage is required.

Camp favors explicit surface distinctions where those distinctions help prevent mistakes:

- owning strings and borrowed string views are different types
- `newtype` introduces a real nominal boundary even when the machine representation stays the same
- grouped values are not silently treated as stored aggregates
- `in` changes transport, not source-level meaning
- `out` is explicit caller-provided result storage, not a hidden reference category

That explicitness keeps the language compatible with the C ABI without forcing programmers to work at raw C’s level of ceremony.

## 1.1 Primitive Types

Primitive types are the indivisible built-in types of the language.

Camp defines a fixed set of primitive names. Their meaning does not drift by platform. That stability is important for header generation, ABI predictability, foreign interop, and low-level work on small or unusual targets.

### 1.1.1 Scalar primitive overview

| Type | Meaning |
|---|---|
| `sbyte` | 8-bit signed integer |
| `byte` | 8-bit unsigned integer |
| `short` | 16-bit signed integer |
| `ushort` | 16-bit unsigned integer |
| `int` | 32-bit signed integer |
| `uint` | 32-bit unsigned integer |
| `long` | 64-bit signed integer |
| `ulong` | 64-bit unsigned integer |
| `nint` | signed pointer-sized integer, but never smaller than 32 bits |
| `nuint` | unsigned pointer-sized integer, but never smaller than 32 bits |
| `float` | 32-bit floating-point value |
| `double` | 64-bit floating-point value |
| `bool` | boolean truth value |
| `char` | UTF-8 code unit |
| `wchar` | UTF-16 code unit |
| `uchar` | Unicode code point |
| `achar` | ASCII or system-code-page character |
| `void` | no value |

`void` is listed with the primitives because it participates in signatures, function types, delegates, and ABI reasoning, even though it is not a storable scalar value in the ordinary sense.

### 1.1.2 Integer types

Camp’s integer model is deliberately conventional:

- the fixed-width signed and unsigned types have stable sizes
- the pointer-sized types track the target machine’s pointer width, but remain at least 32 bits
- no platform chooses a different size for `int`, `long`, or the other fixed-width names

This gives code a predictable baseline:

```camp
int itemCount = 120;
uint flags = 0;
long fileOffset = 123456789;
nuint bytesCopied = 0;
```

The fixed-width integer names are the right choice whenever a program cares about exact representation, serialized layout, protocol stability, or exported ABI shape.

The pointer-sized integer names are the right choice whenever a program is modeling:

- sizes
- indexes that naturally follow native pointer width
- machine handles that are naturally pointer-sized
- foreign APIs that use pointer-sized integer conventions

For example:

```camp
newtype WindowHandle: nint;
newtype ByteCount: nuint;
```

A common Camp style is to use fixed-width integers for externally defined data and pointer-sized integers for machine-shaped values internal to a target or API boundary.

### 1.1.3 Signedness and intent

Camp’s integer names encode signedness directly. There is no ambiguity in the type name itself.

Use signed integer types when negative values are part of the domain:

```camp
int delta = -4;
long temperatureOffset = -12;
```

Use unsigned integer types when the domain is naturally non-negative or bitwise:

```camp
uint permissions = 0x12;
ulong checksum = 0;
nuint length = buffer.length;
```

That distinction matters more in Camp than in languages that lean heavily on implicit widening and overloaded arithmetic rules, because Camp’s design generally prefers obviousness over aggressive silent coercion.

### 1.1.4 Pointer-sized integers

`nint` and `nuint` deserve separate emphasis.

They are defined as:

- the size of a pointer
- or 32 bits, whichever is greater

So on a 16-bit target they are still 32-bit types, and on a 32-bit or 64-bit target they follow the pointer size.

That rule avoids a class of awkwardness that appears on very small platforms when a “native integer” becomes too small for ordinary indexing or length work. It also keeps the type useful on retro targets without making it surprising on modern ones.

Examples:

```camp
nuint count = values.length;
nint nativeResult = foreignCall();
```

### 1.1.5 Floating-point types

Camp provides two floating-point primitive types:

- `float`
- `double`

Use them in the ordinary way:

```camp
float ratio = 0.5;
double distance = 12.75;
```

Camp’s design documents currently use these types in the standard library surface and type tables, and nothing about them is meant to be exotic. They are plain scalar numeric types, not library wrappers and not special object categories.

### 1.1.6 `bool`

`bool` is the ordinary logical truth type.

```camp
bool found = false;
bool finished = count == 0;
```

It is also a common payload and control type in APIs:

```camp
bool has(StringView name);
bool remove(StringView name);
```

Like the other scalar primitives, `bool` participates directly in `params` values, delegates, arrays, and optionals.

### 1.1.7 Character types

Camp distinguishes character encodings explicitly in the type system.

| Type | Meaning |
|---|---|
| `char` | one UTF-8 code unit |
| `wchar` | one UTF-16 code unit |
| `uchar` | one Unicode code point |
| `achar` | one ASCII or system-code-page character |

These are not interchangeable spelling variants of “character.” They represent different things.

That distinction matters because:

- UTF-8 and UTF-16 use different code-unit sizes
- code units and code points are not the same concept
- some APIs genuinely want ASCII or code-page text
- string indexing and slicing in Camp operate on code units, not code points

Examples:

```camp
char unit8 = 'A';
wchar unit16 = 'A';
uchar codePoint = 0x1F600;
achar ascii = 'A';
```

The names deliberately reflect representation rather than cultural intuition. A `char` is not “a Unicode character” in the abstract. It is a UTF-8 code unit. A `uchar` is the full code point form.

### 1.1.8 Character widening and narrowing

The current built-in character conversion chain is:

Implicit widening:

```text
achar -> char -> wchar -> uchar
```

Explicit narrowing:

```text
uchar -> wchar -> char -> achar
```

These conversions are representation-level conversions. The built-in casts do not perform checked validation or “best effort” text transcoding rules beyond the conversion itself. If a program needs safer policy, that policy belongs in helper APIs rather than in hidden primitive semantics.

Examples:

```camp
achar a = 'A';
char c = a;          // implicit
wchar w = c;         // implicit
uchar u = w;         // implicit

char c2 = (char)u;   // explicit narrowing
achar a2 = (achar)c; // explicit narrowing
```

This chain is intentionally simple. It lets code express common widening naturally while forcing the programmer to acknowledge narrowing.

### 1.1.9 `void`

`void` means “no value.”

It is used in:

- function return types
- delegate return types
- function and delegate signatures that conceptually produce no result

Examples:

```camp
void logLine(StringView text);
delegate void(StringView text) logger;
```

`void` is not a grouped type and not a storable payload type in the ordinary sense. It exists to describe absence of a result.

### 1.1.10 Primitive defaults

Many later features depend on the default value of a type. Camp’s ordinary success-code convention, optional values, and async iteration all rely on this idea.

The important practical defaults in this section are:

- numeric zero for numeric primitives
- `false` for `bool`
- `null` for pointer-like values
- the type’s ordinary all-default form for grouped types built from primitive components

A status enum intended for ordinary success-code checking therefore commonly gives its success member the value `0`:

```camp
enum IoError
{
	OK = 0,
	E_NOT_FOUND,
	E_ACCESS_DENIED
}
```

That is not a special enum-only exception. It is simply good alignment with Camp’s ordinary default-value conventions.

## 1.2 Pointers

Camp uses explicit pointer syntax.

A pointer type is written with `*`:

```camp
int* p;
char* text;
Window* window;
void* raw;
```

The language does not blur the distinction between a value and a pointer to that value.

That matters especially for object types later in the language. A class instance is not an implicit reference. A pointer to a class instance is still written explicitly with `*`. The same syntax is used uniformly for all pointed-to types.

### 1.2.1 Ordinary pointer meaning

For an ordinary non-`params` type `T`, `T*` means a pointer to one `T`.

Examples:

```camp
int* valuePtr;
byte* buffer;
Window* dialog;
void* nativeHandle;
```

This is the familiar C-style meaning.

### 1.2.2 `void*`

`void*` remains useful as an untyped pointer form for low-level interop and machine-level state.

Examples from the design space include:

```camp
newtype NativeBufferPtr: void*;
newtype PluginContext: void*;
newtype NativeState: void*;
```

Camp keeps `void*` available because foreign APIs, opaque callbacks, and low-level allocators still need it.

### 1.2.3 Pointer explicitness

Camp does not use hidden object-reference syntax. If code is passing, storing, or returning an address, that address is spelled as a pointer.

```camp
Window local = init Window(640, 480);
Window* alias = &local;
```

This explicitness keeps value semantics and reference semantics visually separate.

### 1.2.4 Pointers and grouped types

Grouped `params` values need special treatment. A grouped value does not have one ordinary aggregate address. As a result, `T*` for a `params` type ordinarily means “apply pointer formation componentwise,” not “pointer to one aggregate grouped object.”

That rule belongs to `params`, not to primitive pointer syntax, but it is important enough to state early because it prevents one of the most common wrong intuitions about grouped values.

For example, with:

```camp
params StringView(@nosuffix char* units, nuint length);
```

the type:

```camp
StringView*
```

is structurally a pointer-to-components shape, not a pointer to one opaque boxed aggregate object.

When a real one-address storage object is required, Camp uses `struct(T)` and `(struct)expr` instead.

### 1.2.5 Pointer defaults

The default value of a pointer-like type is `null`.

That fact is used directly in several areas of the language and standard APIs:

- optional payloads can themselves be pointer types
- async iteration may treat `null` as an all-default non-yielding step unless a pointer payload is wrapped in `T?`
- ordinary low-level APIs still use `null` in the expected way

Example:

```camp
byte* data = null;
```

### 1.2.6 Pointer lifetimes are a separate layer

This section defines pointer *types*. Lifetime annotations are a separate layer.

Camp distinguishes three source-level lifetime forms:

- `escaped`
- `scoped`
- `unscoped(...)`

`escaped` is a storage/context fact meaning the value is not in stack storage.

For parameters and return parameters, `scoped` is the default lifetime. `unscoped(...)` removes that `scoped` restriction for the stated relations. Return values may also use `scoped` or `scoped(...)` to describe caller-visible lifetime relationships.

These annotations are not limited to raw pointer types. They may also appear on aggregate or context values, where they describe the lifetime of the contained pointers.

For delegate-like callable values, lifetime annotations describe the lifetime of the hidden context pointer.

The full rules are defined in the lifetime chapter.

## 1.3 Callable Types

Camp uses callable signatures directly in type positions.

The built-in callable forms are structural and compiler-recognized:

| Form | Meaning |
|---|---|
| `fn R(...)` | plain callable target with no context |
| `delegate R(...)` | callable value with `context` and `call` |
| `once R(...)` | one-shot delegate |
| `iter R(...)` | iterator callable |
| `async R(...)` | async callable |
| `async iter R(...)` | async iterator callable |

These forms are used the same way wherever a type is written.

Examples:

```camp
fn bool(int value)
delegate void(StringView text)
once void()
iter int()
async int(int left, int right)
async iter StringView?()
```

The plain `fn` form is just a function signature. It does not carry closure state and it is not a grouped value.

The other callable forms are blessed structural types. Matching shape alone is not enough to participate in delegate, iterator, or async semantics. When code constructs a matching shape without using the keyworded form, an explicit cast such as `(delegate)`, `(iter)`, or `(async)` blesses it.

Delegate-like callable signatures may spell an explicit `this` parameter first in order to qualify the hidden context parameter.

Examples:

```camp
delegate void(scoped this, StringView text)
once void(escaped this)
async int(const this, int left, int right)
```

This is permitted for callable forms that carry context:

- `delegate`
- `once`
- `iter`
- `async`
- `async iter`

A plain `fn` signature may not declare `this` because it has no hidden context parameter.

Lifetime annotations written on a delegate-like callable value apply to that hidden context pointer.

### 1.3.1 `fn` versus context-carrying callables

This distinction is central:

| Form | Meaning |
|---|---|
| `fn` | plain callable target |
| `delegate` / `once` / `iter` / `async` | callable value with compiler-recognized semantics |

A context-carrying callable can represent:

- a lambda with captured state
- a bound instance method
- an escaped copied closure
- a scoped closure that uses surrounding state directly
- an iterator frame
- an async frame

A plain `fn` does not carry that context.

That is why interface vtable entries are modeled conceptually with `fn` targets that take an explicit context parameter:

```camp
struct IRefVTable<U>
{
	@mustinit fn void(U* ctx) retain;
	@mustinit fn void(U* ctx) release;
}
```

The function target itself is plain. The context is explicit in the parameter list.

### 1.3.2 Example: plain function target

```camp
bool isEven(int value)
{
	return (value & 1) == 0;
}

fn bool(int value) test = isEven;
```

The important property here is not syntactic cleverness. The important property is that `test` refers to a callable target with exactly the stated signature and no hidden closure object.

### 1.3.3 Callable forms in ABI thinking

Camp’s general design rule is that features should lower to explicit call targets and explicit data. `fn` is the direct expression of that idea for plain callables.

The other callable forms reuse ordinary signatures, but also mark the value as participating in delegate, iterator, or async behavior. Later sections define those behaviors in detail.

## 1.4 Functions

Camp function declarations are ordinary, explicit signatures. Camp does not use overloads, hidden reference categories, or opaque call-resolution machinery to make one name mean many different unrelated callable surfaces.

A function name therefore refers to one callable declaration.

This keeps calls easy to read, easy to lower, and easy to export.

### 1.4.1 Basic declaration form

An ordinary function declaration has the familiar shape:

```camp
ReturnType Name(ParameterList)
{
	...
}
```

Examples:

```camp
int add(int left, int right)
{
	return left + right;
}

void printLine(StringView text)
{
	Console.writeLine(text);
}
```

Camp’s syntax is intentionally close to C and C# here. The important differences emerge from the type system and ABI model, not from decorative declaration syntax.

### 1.4.2 Parameters are explicit

Each parameter has:

- a type
- a name
- optionally a default value
- optionally a transport or calling modifier such as `in` or `out`

Example:

```camp
void moveWindow(nint x, nint y, bool repaint = true)
{
	...
}
```

### 1.4.3 Return types are explicit

Ordinary function return types are written explicitly.

```camp
bool has(StringView name);
nuint count();
StringView getText();
void clear();
```

Grouped results are handled through `params` and `out`, not through tuple syntax or hidden multi-return runtime mechanisms.

### 1.4.4 No overloads

Camp does not support overloads.

This is an intentional design choice. It avoids the ambiguity, backtracking, and diagnostic complexity that overload sets often introduce. One function name therefore identifies one callable declaration, and call matching remains straightforward.

If a design needs optional behavior at the same call site, the preferred tools are:

- default arguments
- options objects
- grouped `params` forms
- distinct method names

That rule is especially helpful once exported APIs, header generation, and property-accessor rewriting are involved.

### 1.4.5 Default arguments

Parameters may have default values:

```camp
void logLine(StringView text = default)
{
	...
}

void connect(StringView host, ushort port = 80)
{
	...
}
```

Default arguments keep the surface compact without introducing constructor-like overload families or ad hoc helper wrappers.

A common Camp pattern is to use one declaration with a few carefully chosen defaults instead of many sibling declarations with nearly identical meanings.

### 1.4.6 Calls

An ordinary call uses positional arguments:

```camp
int sum = add(3, 4);
printLine("hello");
```

Calls are matched left to right. Because Camp does not have overloads, call matching does not perform overload-style backtracking. This becomes important once grouped `params` values are allowed to spread across adjacent parameters.

### 1.4.7 Named arguments

Camp also supports named arguments.

Named arguments participate in the same call surface as positional arguments, including when nominal `params` parameters expand into ABI-visible component parameters.

Example:

```camp
void setSize(int width, int height);

setSize(width: 640, height: 480);
```

Named arguments are especially useful when:

- later parameters have default values
- multiple parameters have the same primitive type
- a call mixes ordinary parameters and exploded nominal `params` components

### 1.4.8 Mixing positional and named arguments

Positional and named arguments may be mixed, but no component may be supplied more than once.

That rule applies both to ordinary parameters and to the component names introduced by a nominal `params` parameter after expansion.

Example:

```camp
params Point(int x, int y);

void place(Point pt, int color);

place(10, pt_y: 20, color: 3); // valid if `x` and `y` are each supplied once
```

By contrast:

```camp
params Point(int x, int y);

void draw(Point pt);

Point p = (x: 10, y: 20);

draw(pt: p, pt_x: 10); // ERROR
draw(10, pt: p);       // ERROR
```

Camp rejects duplicate supply rather than guessing what the programmer intended.

### 1.4.9 Parameters expand when nominal `params` is used

If a parameter is a nominal `params` type, that parameter expands into multiple ABI-visible parameters.

Example:

```camp
params Point(int x, int y);

void moveTo(Point position);
```

Conceptually, the call surface expands to:

```camp
void moveTo(int position_x, int position_y);
```

This naming rule is used for both:

- ABI/header-visible parameter names
- named-argument resolution in Camp source

So the source surface and the lowered surface stay aligned.

### 1.4.10 `@nosuffix` in parameter expansion

Some nominal grouped types read better if one component omits its suffix during expansion.

For that, one component of a nominal `params` declaration may be marked `@nosuffix`.

Example:

```camp
params StringView(@nosuffix char* units, nuint length);

void setText(StringView value);
```

The expanded call shape becomes:

```camp
void setText(char* value, nuint value_length);
```

not:

```camp
void setText(char* value_units, nuint value_length);
```

Important restrictions:

- at most one component may use `@nosuffix`
- it affects parameter expansion and named-argument spelling only
- it does not rename the actual members of the grouped value

So these remain the member names:

```camp
text.units
text.length
```

### 1.4.11 `in` parameters

`in` is a transport feature.

An `in T` parameter:

- behaves like a by-value parameter in source
- is passed as a hidden pointer in the ABI
- does not expose pointer mechanics at the call site
- does not by itself change the lifetime of the logical value

Example:

```camp
void printPoint(in Point pt)
{
	log(pt.x);
	log(pt.y);
}
```

The caller writes:

```camp
Point p = (x: 10, y: 20);
printPoint(p);
```

not some address-taking ceremony. The parameter still reads like a value parameter in source.

This is especially important in erased generic code, where `in T` is usually the right surface form when the API wants value-like transport for `T: any`.

### 1.4.12 When to use `in` versus `T*`

Use `in T` when the call contract is logically “pass a value.”

Use `T*` when the contract truly needs:

- stable address identity
- explicit pointer semantics
- mutation through shared storage
- a pointer that may be retained beyond the call

This distinction matters because `in` deliberately does **not** turn a value parameter into an aliasing-sharing reference category. It only changes transport.

### 1.4.13 `out` parameters

`out` means caller-provided result storage.

A simple example:

```camp
void getBounds(out int width, out int height);
```

The caller may supply explicit storage:

```camp
int width = 0;
int height = 0;

getBounds(out width, out height);
```

But Camp also gives trailing `out` parameters a more compact grouped-result surface when the caller omits them.

### 1.4.14 Omitted trailing `out` values

If a call omits one trailing `out` parameter, that omitted value becomes the ordinary return value.

If a call omits multiple trailing `out` values, the result becomes a structural grouped `params` value.

Example:

```camp
void getStats(out int length, out int capacity);

auto stats = getStats();
auto (length, capacity) = getStats();
```

This is not a separate tuple subsystem. It is the ordinary grouped-value model applied to omitted trailing `out` results.

The omitted names are preserved when available, so the grouped result can carry meaningful component names.

### 1.4.15 `thrown`

Camp has two surface forms for error results.

| Form | Meaning |
|---|---|
| `..., thrown E` | trailing error parameter |
| `thrown(E) f(..., out T value)` | error returned in the ordinary ABI return slot |

A trailing `thrown` parameter may omit its name:

```camp
int calculateSomeValue(int a, int b, thrown CalcError)
{
	...
}
```

If the name is omitted, the implicit parameter name is `error`. That implicit name is used for ABI spelling and named-argument resolution. Another ordinary parameter named `error` in the same signature is therefore a normal compile-time error.

A different explicit name may be written when needed:

```camp
int calculateSomeValue(int a, int b, thrown CalcError errorValue)
{
	...
}
```

The error type must be one of:

- an integral type, including `nint` and `nuint`
- an enum type
- a `newtype` whose underlying type is integral
- a pointer type, including interface pointers

If the error type is a pointer, it is treated as `escaped`, the same way a pointer `out` result is.

Camp’s ordinary convention is:

- the default value of the status or error type means success
- any non-default value means failure

Many status enums therefore place `OK = 0` or an equivalent success value first and explicitly assign it `0`.

At the call site, a thrown result may be handled in three ordinary ways:

```camp
int divide(int a, int b, thrown CalcError)
{
	if (b == 0)
		throw E_DIV_ZERO;

	return a / b;
}

thrown(CalcError) tryDivide(int a, int b, out int result)
{
	if (b == 0)
		throw E_DIV_ZERO;

	result = a / b;
}

void sample(thrown CalcError)
{
	auto value = divide(10, 2);
	auto value2 = tryDivide(10, 2);

	CalcError localError;
	
	divide(10, 0, catch localError);
	tryDivide(10, 0, catch auto anotherLocalError);

	try
	{
		divide(10, 0);
	}
	catch (CalcError caught)
	{
		...
	}
}
```

When `thrown(E)` is the return type, `catch` still appears in the argument list. In that call form, the thrown return is handled as though it were an additional final argument, while omitted trailing `out` values still form the ordinary result.

A `thrown` parameter is not an `out` parameter. It does not participate in omitted trailing `out` result formation.

### 1.4.16 Functions returning grouped values

A function may explicitly return a nominal grouped type:

```camp
params DivResult(int quotient, int remainder);

DivResult divideInt(int a, int b)
{
	return (quotient: a / b, remainder: a % b);
}
```

This is often clearer than a long list of explicit `out` parameters when the grouped result has a meaningful name and identity.

### 1.4.17 Calls and grouped arguments

Grouped structural `params` values spread automatically in calls:

```camp
void setPair(int left, int right);

auto xy = 5, 10;
setPair(xy); // same as setPair(5, 10)
```

Spreading is positional and left-to-right. There is no overload-style backtracking.

Example:

```camp
void doWork(int a, int b, int c, int d);

auto lr = 10, 20;

doWork(lr, 30, 40); // a=10, b=20, c=30, d=40
doWork(1, lr, 4);   // a=1,  b=10, c=20, d=4
doWork(1, 2, lr);   // a=1,  b=2,  c=10, d=20
```

Nominal grouped values do **not** spread everywhere. They spread only when the destination endorses that exact nominal type at that parameter position.

```camp
params Point(int x, int y);

void makeLine(Point start, Point end);
void makeRange(int x1, int y1, int x2, int y2);

Point a = (x: 1, y: 2);
Point b = (x: 3, y: 4);

makeLine(a, b);               // OK
makeLine(a, 3, 4);            // OK
makeLine(1, 2, 3, 4);         // OK

makeRange(a, b);              // ERROR
makeRange(a.x, a.y, b.x, b.y);// OK
```

This keeps nominal grouped types meaningful instead of letting them dissolve into raw positional component lists everywhere.

### 1.4.18 Initializer lists are not call spreading

A grouped value does not spread in an initializer list.

```camp
auto rectArgs = (x: 1, y: 2, width: 3, height: 4);

Rect a = { 1, 2, 3, 4 };        // OK
Rect b = rectArgs;              // ERROR
Rect c = { rectArgs };          // ERROR
Rect d = init Rect(rectArgs);   // OK
Rect e = (struct)rectArgs;      // OK
```

Spreading is a call-argument rule, not a general “expand everywhere” rule.

### 1.4.19 Functions, methods, and callable values

This section defines ordinary function declarations and calls.

Later sections add:

- method-like invocation
- delegates and lambdas
- iterators
- async functions

But those later surfaces still build on the ordinary signature model defined here. Camp does not introduce a disconnected callable subsystem for each feature.

## 1.5 `params` Types

`params` is one of Camp’s most important type mechanisms.

A `params` value is a grouped set of independent components. It is not an aggregate object with one implied address. That one sentence explains much of the rest of Camp:

- multiple values can travel together without inventing tuple objects
- arrays, strings, optionals, and delegates can all be expressed as built-in grouped forms
- functions can return multiple values naturally
- grouped forms can spread across calls without becoming separate library-defined wrapper types
- when one real address is needed, Camp makes storage formation explicit with `struct(T)` and `(struct)expr`

### 1.5.1 Core idea

A nominal grouped declaration looks like this:

```camp
params Point(int x, int y);
```

A value of type `Point` has two components:

- `x`
- `y`

But those components are conceptually separate. The grouped value is not secretly “a tiny struct.”

This means:

- grouped values do not automatically have one whole-object address
- pointer and array derived forms often distribute across components
- many validity questions can be answered componentwise

Camp’s normative mental model is:

> unless a rule says otherwise, reason about a grouped value by imagining each component used independently in the corresponding position

### 1.5.2 Why grouped values exist

Camp uses grouped values for:

- multiple-value grouping
- arrays
- strings and string views
- optionals
- delegates
- omitted trailing `out` results

That reuse is not accidental. Camp does not want one mechanism for tuples, another for strings, another for delegates, and another for “multi-return result packs.” It wants one small, explicit model that lowers cleanly.

### 1.5.3 `params(T)` and `struct(T)`

Camp makes the grouped-versus-storage distinction explicit at both the type level and the value level. These forms apply to grouped values and copyable storage values. Fixed structs and classes do not convert to grouped form.

Type-level forms:

- `params(T)` — grouped-value form
- `struct(T)` — materialized storage form

Value-level conversions:

- `(params)expr`
- `(struct)expr`

This gives the language symmetry:

- `params(StructType)` explodes a struct-like storage value into grouped form
- `struct(ParamsType)` materializes a grouped value into stored fields

Example:

```camp
struct PointStorage
{
	int x;
	int y;
}

void drawPoint(int x, int y);

PointStorage pt = { .x = 10, .y = 20 };
drawPoint((params)pt);
```

And in the other direction:

```camp
params Point(int x, int y);

void draw(Point pt);

auto grouped = (x: 5, y: 10);
draw((struct)grouped);
```

### 1.5.4 Fixed types do not participate

A fixed type does not convert to or from grouped form using `params(T)` and `struct(T)`.

These forms are for grouped values and copyable storage values, not for class instances or fixed structs.

### 1.5.5 Structural versus nominal grouped forms

Camp has two grouped categories:

- structural grouped values
- nominal grouped types

Structural grouped values are anonymous. Nominal grouped values are declared types with real names.

#### Structural grouped examples

Unnamed structural grouping:

```camp
auto pair = 5, 10;
```

Named structural grouping:

```camp
auto pointLike = (x: 5, y: 10);
```

In both cases, the result is structural, not nominal.

#### Nominal grouped example

```camp
params Point(int x, int y);
```

A `Point` is a distinct nominal type.

### 1.5.6 Structural grouped identity

Two structural grouped types are structurally identical when they have the same:

- arity
- component order
- component types

Component names do **not** affect structural identity.

So these are structurally identical:

```camp
params(int x, int y)
params(int left, int right)
params(int, int)
```

and these are not:

```camp
params(int, bool)
params(bool, int)
params(int, int, int)
```

That distinction is important: names matter for access and ABI spelling, but not for structural identity itself.

### 1.5.7 Structural-to-nominal conversion

A structural grouped value may convert to a nominal grouped type if the shapes match, but named structural forms are intentionally restricted.

A structural grouped value may be assigned to a nominal grouped type only when:

1. all structural components are unnamed
2. or all structural components are named and the names match the nominal component names exactly, in the same order

Examples:

```camp
params Point(int x, int y);

Point a = (1, 2);         // OK
Point b = (x: 1, y: 2);   // OK
Point c = 1, 2;           // OK

Point d = (left: 1, right: 2); // ERROR
Point e = (y: 1, x: 2);        // ERROR
```

This rule prevents a named structural value from looking “almost right” while actually supplying the wrong semantic names.

### 1.5.8 Erasing nominal identity

A nominal grouped value does not silently become structural, but `(params)expr` may erase its nominal identity explicitly:

```camp
params Point(int x, int y);

Point p = (x: 1, y: 2);
auto structural = (params)p;
```

After that conversion, the result is structural.

### 1.5.9 Declaring nominal grouped types

A nominal grouped declaration takes one of two forms.

#### Ordinary nominal declaration

```camp
params Point(int x, int y);
params Range<T>(T start, T end);
```

#### Narrow nominal declaration with widening

```camp
params String: StringView;
```

A narrowing declaration introduces a distinct nominal type that implicitly widens to an existing grouped type.

This is how the current string-owning types are expressed.

### 1.5.10 Consequences of narrowing declarations

Given:

```camp
params String: StringView;
```

the type `String`:

- is distinct from `StringView`
- inherits the component names of `StringView`
- widens implicitly to `StringView`
- does not declare its own separate parameter list

So if `StringView` has components `units` and `length`, then `String` uses those same names for member access and parameter expansion.

### 1.5.11 Recursive grouped declarations are forbidden

A nominal grouped declaration may not directly contain a fixed type. A pointer to a fixed type is allowed, but the fixed value itself is not a grouped component.

A nominal grouped declaration may not recursively contain itself, even through pointer-shaped distributive expansion.

This is rejected:

```camp
params Abc(int value, Abc* next); // ERROR
```

The reason is not aesthetic. Recursive grouped types would imply unbounded distributive expansion.

### 1.5.12 Creating grouped values

Structural grouped values may be created:

- by comma outside a call
- by named grouped formation
- by explicit grouped type annotation

Examples:

```camp
auto pair = 1, 2;
auto named = (x: 1, y: 2);
params(int x, int y) p = (1, 2);
```

Nominal grouped values may be created from structurally matching values:

```camp
params Point(int x, int y);

Point a = 1, 2;
Point b = (1, 2);
Point c = (x: 1, y: 2);
```

### 1.5.13 Component access

Named grouped components may be accessed by name:

```camp
params Point(int x, int y);

Point p = (x: 10, y: 20);
log(p.x);
log(p.y);

auto pair = (left: 1, right: 2);
log(pair.left);
log(pair.right);
```

Unnamed structural grouped values do not have member names:

```camp
auto pair = 5, 10;
// pair.?  // no member names exist
```

When names are needed, use:

- deconstruction
- a named structural annotation
- conversion to an appropriate nominal type

### 1.5.14 Deconstruction

Any grouped value may be deconstructed when the arity matches:

```camp
params Point(int x, int y);

Point p = (x: 7, y: 8);
auto (x, y) = p;

auto pair = 10, 20;
auto (a, b) = pair;
```

This is often the clearest way to work with unnamed structural results.

### 1.5.15 Equality

Grouped values compare componentwise.

Two grouped values are equal when corresponding components are equal.

```camp
params Point(int x, int y);

Point p = (x: 1, y: 2);
auto q = 1, 2;
auto r = 1, 3;

bool a = (p == q); // true
bool b = (q != r); // true
```

This rule is reused by built-in grouped forms such as optionals.

### 1.5.16 Call spreading

Structural grouped values spread automatically in call positions.

```camp
void setPair(int left, int right);

auto xy = 5, 10;
setPair(xy);
```

Nominal grouped values spread only when endorsed by the destination signature.

This is one of the key differences between structural and nominal grouping.

### 1.5.17 Crossing argument boundaries

Structural grouped values may cross nominal grouping boundaries because they are just positional components.

Nominal grouped values may not cross such boundaries.

```camp
params Point(int x, int y);

void makeLine(Point p1, Point p2);

auto crossable = 2, 3;
Point p = (x: 10, y: 20);

makeLine(1, crossable, 4); // OK
makeLine(1, p, 4);         // ERROR
```

This preserves the integrity of nominal grouping.

### 1.5.18 Grouped values do not spread in initializers

Grouped values do not spread in initializer lists.

```camp
auto rectArgs = (x: 1, y: 2, width: 3, height: 4);

Rect a = { 1, 2, 3, 4 };        // OK
Rect b = rectArgs;              // ERROR
Rect c = { rectArgs };          // ERROR
Rect d = init Rect(rectArgs);   // OK
Rect e = (struct)rectArgs;      // OK
```

Again, spreading is specifically a call-argument rule.

### 1.5.19 Addressability

A grouped value has no single address as a whole, but its components may be addressable.

That gives rise to a practical rule:

- if code needs pointer-to-components semantics, a grouped lvalue can often supply it directly
- if code needs one whole-object address, materialize storage first

Example of materializing first:

```camp
auto pair = (x: 5, y: 10);
auto temp = (struct)pair;
usePointPointer(&temp);
```

### 1.5.20 Built-in grouped form: arrays

An array type `T[]` is a compiler-reserved nominal grouped form with canonical components:

- `elements`
- `length`

Conceptually:

```camp
T[] -> (elements, length)
```

Arrays are not ordinary user-defined generic library types. They are built into the language model.

Example:

```camp
byte[] buffer = new byte[256];
log(buffer.length);
```

At the language level, `T[]` is an array of `T`. When a context requires materialized storage for grouped element types, that storage model is explicit and separate.

Example:

```camp
params Point(int x, int y);

Point[] points = init Point[10];

Point p = points[4];
points[4] = 10, 20;
```

Semantically, this is an array of `Point`, even though implementation details may materialize element storage where required.

### 1.5.21 Built-in grouped form: optionals

An optional type `T?` is a compiler-reserved nominal grouped form with canonical components:

- `value`
- `specified`

Conceptually:

```camp
T? -> (value, specified)
```

This is a built-in grouped form, not a library wrapper type.

### 1.5.22 Built-in grouped form: delegates

A delegate value is a two-component nominal grouped form:

- `context`
- `call`

The call target receives the context first.

```camp
delegate bool(int value)
```

A delegate value therefore participates in the general grouped model while remaining a first-class callable abstraction. Callable `newtype` declarations introduce nominal callable types later.

### 1.5.23 Built-in grouped form: strings and views

Strings and string views are nominal grouped types.

Current canonical declarations are:

```camp
params StringView(@nosuffix char* units, nuint length);
params WStringView(@nosuffix wchar* units, nuint length);
params AStringView(@nosuffix achar* asciichars, nuint length);

params String: StringView;
params WString: WStringView;
params AString: AStringView;
```

So the current canonical member names are:

| Type | Components |
|---|---|
| `StringView` | `units`, `length` |
| `WStringView` | `units`, `length` |
| `AStringView` | `asciichars`, `length` |
| `String` | inherited from `StringView` |
| `WString` | inherited from `WStringView` |
| `AString` | inherited from `AStringView` |

This preserves both a compact surface spelling and a fully ordinary lowered model.

### 1.5.24 Delete support is opt-in

Grouped types are not deletable by default.

Delete support exists only when the specific nominal grouped type declares destructor syntax.

Consequences:

- `String` may define a destructor and therefore may support `delete`
- `StringView` does not become deletable just because `String` is
- delegates are not deletable by default
- array-like grouped forms are not deletable by default

Delete availability follows declared lifecycle surface, not mere shape.

### 1.5.25 Built-in grouped forms at a glance

The language reuses the grouped model for several built-in forms.

| Form | Canonical components | Notes |
|---|---|---|
| `T[]` | `elements`, `length` | compiler-reserved array form |
| `T?` | `value`, `specified` | compiler-reserved optional form |
| `delegate R(...)` | `context`, `call` | call target receives context first |
| `StringView` | `units`, `length` | borrowed UTF-8 text view |
| `WStringView` | `units`, `length` | borrowed UTF-16 text view |
| `AStringView` | `asciichars`, `length` | borrowed ASCII or code-page text view |
| `String` | inherited from `StringView` | owning UTF-8 string, implicit widening to `StringView` |
| `WString` | inherited from `WStringView` | owning UTF-16 string |
| `AString` | inherited from `AStringView` | owning ASCII or code-page string |

This table is useful because it shows how much surface area Camp avoids duplicating. Arrays, optionals, delegates, and strings do not need four separate runtime representations with four separate mental models. They are all ordinary, ABI-visible grouped forms with feature-specific rules layered on top.

### 1.5.26 What grouped values are not

Because grouped values are so widely used, it is worth being explicit about what they are *not*.

A grouped value is not:

- an anonymous class instance
- a hidden tuple object
- a heap allocation
- a hidden temporary struct with one implied address
- a library-defined generic wrapper with opaque runtime behavior

When a grouped value needs to behave like real storage, Camp says so explicitly:

```camp
auto grouped = (x: 10, y: 20);
auto stored = (struct)grouped;
```

That explicit bridge is one of the language’s most important clarity points. It lets grouped values remain light and distributive in ordinary expression and call positions, while still permitting one-address storage when real storage is actually needed.

## 1.6 Arrays

Arrays are built into the language as nominal grouped forms, but they are common enough to warrant separate practical treatment here.

### 1.6.1 Shape

An array type `T[]` has two canonical components:

- `elements`
- `length`

Conceptually, it behaves like a lightweight pair of pointer plus count.

This does **not** make arrays “just syntax for a user-defined library type.” The form is compiler-reserved and receives dedicated language treatment.

### 1.6.2 Basic use

```camp
byte[] buffer = new byte[256];
nuint len = buffer.length;
```

Array shapes appear throughout the language and standard library:

- raw binary buffers
- text code-unit buffers
- arrays of nominal grouped values
- arrays used in generic algorithms
- array-based reader and writer APIs

### 1.6.3 Arrays of grouped values

Arrays may contain nominal grouped element types:

```camp
params Point(int x, int y);

Point[] points = init Point[10];
points[4] = 10, 20;
Point p = points[4];
```

At the source level, this is simply an array of `Point`. When actual contiguous element storage is needed, the storage model is explicit rather than hidden.

Arrays may also contain fixed types. Fixed array elements use the same copyability rules as class elements: an element may be initialized in place, but it may not be copied by value.

### 1.6.4 Arrays are not strings

Even when their physical layout may be compatible, arrays and string types are distinct nominal forms with different semantics and method surfaces.

This avoids a long list of problems:

- accidental treatment of arbitrary binary buffers as text
- accidental reliance on string-only APIs on raw arrays
- confusion about borrowing, ownership, encoding, and null termination

### 1.6.5 Array APIs follow the grouped model

The current array API direction reinforces the grouped interpretation:

- borrowed slices return array values
- `addressOf()` exposes element-address access explicitly
- copy-producing APIs are named as copies
- generic array APIs often use `in T` transport and `sizeof(T)` where needed

Examples from the current API direction:

```camp
scoped T[] slice<T: any>(const T[] this, @range nuint index = 0, nuint count = ^0, sizeof(T));
scoped T* addressOf<T: any>(T[] this, @index nuint index, sizeof(T));
T[] copy<T: any>(const T[] this, within allocator, sizeof(T));
```

Copy-producing array APIs are valid only when the element operation is valid for the substituted type. They do not copy fixed struct or class elements by value.

### 1.6.6 Array indexing and slicing shape

Raw array indexing is still expressed with `[]`.

Slicing is expressed through ordinary methods and index-aware parameters rather than through a separate hidden slicing operator category.

This keeps the call surface ordinary while still allowing convenient syntax through method and property rewriting.

## 1.7 Optional Values

Optional values are built into Camp with the `?` suffix.

### 1.7.1 Core model

A type `T?` is a distinct grouped type derived from `T`. `T` may not be a fixed type. Use `T*?` when the optional payload should be a nullable pointer to a fixed value.

Its canonical components are:

- `value`
- `specified`

`T?` is the language form. It is not a user-defined generic alias or library wrapper.

### 1.7.2 Construction

A value of type `T` implicitly converts to `T?`:

```camp
int? x = 5;
```

This is the one built-in convenience conversion attached to optionals.

The default value of `T?` is the unspecified state:

```camp
int? missing = default;
```

Conceptually:

- `missing.specified == false`
- `missing.value` contains the default payload value for `T`

A programmer may also form an optional explicitly using grouped syntax:

```camp
int? a = (value: 10, specified: true);
int? b = (value: 0, specified: false);
```

### 1.7.3 Access

Presence is checked through `.specified`:

```camp
if (x.specified)
{
	log(x.value);
}
```

The payload is read through `.value`.

Reading `.value` performs no automatic safety check. If `specified` is false, the payload slot still contains whatever payload value is stored there.

This is deliberate. Camp does not invent hidden control flow or runtime wrapping rules here.

### 1.7.4 Semantics

Optional values add no hidden semantics beyond their grouped representation.

In particular, Camp does **not** add:

- automatic unwrapping
- flow-sensitive “definitely present” analysis
- null-based special cases
- hidden propagation
- special comparison rules

Equality is ordinary componentwise grouped equality:

```camp
int? a = 5;
int? b = 5;
int? c = default;

bool same = (a == b);
bool missing = (c == default);
```

### 1.7.5 Composition

Optionals compose naturally with other type forms:

```camp
int? value;
String? text;
int?[] items;
int?? nested;
```

When generic code needs a real storage form for an optional, it uses:

```camp
struct(T?)
```

just like any other grouped form.

### 1.7.6 Why optionals matter elsewhere

Optionals are especially important because they solve one recurring problem cleanly: distinguishing “no logical value” from a payload that happens to be the default value of its type.

That is why async iterators use `T?` as the explicit presence channel when `0`, `false`, or `null` must be carried as real data.

## 1.8 Delegates

Delegates are grouped callable values.

They belong here because they are a built-in grouped type form, not merely a library abstraction layered on top of functions.

### 1.8.1 Shape

A delegate value consists of:

- `context`
- `call`

The `call` target receives `context` first.

Conceptually:

```camp
delegate bool(int value)
```

is a grouped value whose lowered call target is equivalent to a plain function receiving the context plus the ordinary declared parameters.

### 1.8.2 Using delegate types

A delegate type may be written anywhere a type is needed:

```camp
delegate void(StringView text) logger;
delegate bool(int value) predicate;
delegate int(int left, int right) comparer;
```

Lifetime annotations on a delegate value describe the lifetime of that hidden context pointer.

For example:

- `escaped delegate bool(int value)` means the delegate context is non-stack
- `scoped(owner) delegate void(Node* owner)` means the delegate context will not outlive `owner`

Delegate-like callable signatures may also spell an explicit `this` parameter first in order to qualify the hidden context parameter:

```camp
delegate void(scoped this, StringView text) logger;
```

Later sections define lambdas, method references, `once`, `iter`, and `async`. This section only defines the delegate value model itself.

### 1.8.3 Invocation model

If code writes:

```camp
bool eitherGreater(int a, int b, delegate bool(int value) comparer)
{
	return comparer(a) || comparer(b);
}
```

then the invocation is conceptually equivalent to:

```camp
return comparer.call(comparer.context, a)
	|| comparer.call(comparer.context, b);
```

The grouped delegate value itself may therefore spread according to the general grouped rules, although ordinary code usually writes the packed delegate form.

### 1.8.4 What delegates are for

Delegates cover several cases with one uniform model:

- lambdas
- bound instance methods
- callbacks with context
- postponed operations
- escaped copied closures
- scoped closures that use surrounding state directly

Camp does not want a separate runtime closure object category for each of these cases. The grouped delegate model is enough.

### 1.8.5 Delegates versus plain `fn`

Use `fn` for a plain callable target with no grouped context.

Use `delegate` when code needs:

- captured state
- a bound receiver
- a context-plus-call ABI shape
- a first-class callback surface that may carry data with it

### 1.8.6 Deletion

Delegates are not deletable by default.

A delegate value does not imply ownership of some magical closure object. If the target or captured state needs destruction, that ownership belongs to the actual storage or object model involved, not to the delegate type as such.

## 1.9 Strings and String Views

String types are ordinary basic types in Camp, not a late library afterthought.

They are also grouped nominal types, not hidden runtime objects.

### 1.9.1 Core split

Camp distinguishes:

- borrowed string-view types
- ordinary string types

For UTF-8:

- `StringView`
- `String`

For UTF-16:

- `WStringView`
- `WString`

For ASCII or code-page text:

- `AStringView`
- `AString`

This split is fundamental.

A `StringView` is a non-owning view. It is not “the immutable version of `String`.” A `String` is the ordinary string type. It is not merely “a pointer plus length that happens to own memory.” The type names are chosen to make borrowing visible in code.

### 1.9.2 Current canonical declarations

The current canonical grouped declarations are:

```camp
params StringView(@nosuffix char* units, nuint length);
params WStringView(@nosuffix wchar* units, nuint length);
params AStringView(@nosuffix achar* asciichars, nuint length);

params String: StringView;
params WString: WStringView;
params AString: AStringView;
```

So:

- the view types declare the canonical components directly
- the owning string types are distinct narrow nominal forms that widen implicitly to their corresponding view types

### 1.9.3 Widening direction

`String` may be passed where a `StringView` is expected.

The reverse is not implicit.

```camp
void printLine(StringView text)
{
	Console.writeLine(text);
}

String text = "hello";
printLine(text); // OK
```

This widening direction is one of the most important conversion rules in the language. It makes borrowed APIs ergonomic without erasing ownership distinctions.

### 1.9.4 Deletion

`String` may define destructor syntax and therefore may support `delete`.

`StringView` does not define destructor syntax and is therefore not deletable.

Delete availability does not “leak across” the widening relationship. The fact that `String` may be deletable does not make `StringView` deletable.

### 1.9.5 Strings are distinct from arrays

Strings and arrays may share compatible physical layouts, but they remain distinct nominal types because they have different semantics and method surfaces.

This avoids a long list of problems:

- accidental treatment of arbitrary binary buffers as text
- accidental reliance on string-only APIs on raw arrays
- confusion about borrowing, ownership, encoding, and null termination

### 1.9.6 Null termination

Only string literals are automatically null-terminated.

That guarantee should not be generalized to arbitrary `String` values produced by:

- slicing
- concatenation
- builder operations
- foreign APIs
- conversion helpers

A specific API may promise null termination if it wishes. The type itself does not imply it.

### 1.9.7 Code-unit indexing

String indexing and slicing operate in code units, not code points.

This is why the view methods use names and parameter wording such as:

- `getChar(@index nuint unit)`
- `getCharUnits(@index nuint unit)`
- `slice(@range nuint index = 0, nuint count = ^0)`

The parameter name `unit` is intentional.

For example, on `StringView`:

```camp
uchar getChar(const StringView this, @index nuint unit);
nuint getCharUnits(const StringView this, @index nuint unit);
```

These let the caller decode Unicode code points from a code-unit indexed string view while keeping the indexing contract explicit.

### 1.9.8 Borrowing versus copying

Borrowing operations return view types. Copy-producing operations advertise their allocation clearly, typically with `Copy` in the name.

Examples from the current API direction:

```camp
StringView trim(const StringView this);
String uppercaseCopy(const StringView this, within allocator);
String lowercaseCopy(const StringView this, within allocator);
String toStringCopy(const StringView this, within allocator);
```

That naming helps the reader answer two questions immediately:

- does this operation allocate?
- does the result borrow from existing storage or own new storage?

### 1.9.9 Splitting strings

`splitCopy` allocates the outer array but does not deep-copy the inner segments. The segments are `StringView` values pointing into existing string storage.

That detail matters because it preserves efficiency without hiding allocation:

- the array itself is an allocated result
- the inner string segments are borrowed views

### 1.9.10 Strings in APIs

Borrowed textual accessors commonly return `StringView`, not `String`.

Example:

```camp
export extern StringView getReason();
export extern StringView getUrl();
export extern StringView getStatusText();
```

That convention makes ownership visible and avoids silent copies.

### 1.9.11 Text families and ordinary usage

Camp deliberately keeps the three text families parallel:

| Family | View type | Ordinary string type | Typical use |
|---|---|---|---|
| UTF-8 | `StringView` | `String` | general text, source-facing APIs, most common default |
| UTF-16 | `WStringView` | `WString` | UTF-16-oriented foreign APIs or host environments |
| ASCII / code page | `AStringView` | `AString` | legacy APIs, code-page text, explicitly narrow text surfaces |

The point of the parallel naming is not abundance for its own sake. The point is that code can remain explicit about representation without inventing unrelated naming schemes for each family.

Typical usage looks like this:

```camp
void writeUtf8(StringView text);
void writeUtf16(WStringView text);
void writeLegacy(AStringView text);
```

The caller can pass an owning string to the corresponding view-typed API without noise, but the API still advertises the representation it expects.

### 1.9.12 String methods remain ordinary methods

Camp deliberately keeps string operations on the ordinary method surface.

That means:

- borrowed operations return view types
- allocating operations say so in their names
- index-aware operations are ordinary methods with index-like parameters
- property and indexer ergonomics are surface rewriting, not a hidden string-only subsystem

Representative examples from the current API direction include:

```camp
StringView trim(const StringView this);
StringView slice(const StringView this, @range nuint index = 0, nuint count = ^0);
uchar getChar(const StringView this, @index nuint unit);
String uppercaseCopy(const StringView this, within allocator);
String toStringCopy(const StringView this, within allocator);
```

This keeps the string model aligned with the rest of Camp: small core rules, explicit ownership, and ordinary ABI-visible calls.

## 1.10 Enum Types

An enum introduces a nominal type whose values are named constants.

The current source documents use enums extensively for status codes, modes, flags-like choices, and protocol states.

Examples:

```camp
enum HttpMethod
{
	GET,
	POST,
	PUT,
	PATCH,
	DELETE,
	HEAD,
	OPTIONS
}
```

```camp
enum HttpError
{
	OK = 0,
	E_INVALID_URL,
	E_TIMEOUT,
	E_PROTOCOL_ERROR
}
```

### 1.10.1 Purpose

Use an enum when a value belongs to a closed named set and the names themselves carry meaning.

Enums are especially useful for:

- mode switches
- state machines
- status codes
- error categories
- option selection

### 1.10.2 Declaration

An enum declaration lists members inside braces.

Members may be:

- plain names
- or explicitly assigned values where useful

Examples in the design documents include both styles.

### 1.10.3 Explicit values

Explicit member values are especially useful for status enums:

```camp
enum LookupError
{
	OK = 0,
	E_NOT_FOUND,
	E_DUPLICATE
}
```

That aligns naturally with Camp’s ordinary success-code convention, where the default value of the status type means success.

### 1.10.4 Enums in APIs

Enums appear naturally in signatures, structs, and grouped values:

```camp
struct HttpRequestOptions
{
	HttpMethod method;
	HttpRedirectMode redirect;
	HttpCacheMode cache;
	HttpCredentialsMode credentials;
}
```

Because enums are ordinary named types, they make such declarations much easier to read than raw integers would.

### 1.10.5 Enums and defaults

When an enum is used as an error or status type, it is common and advisable to assign the success member the value `0`.

Camp’s error-handling model does not require every enum to be a status enum, but when an enum *is* used in that role, aligning success with the default value is the natural fit.

### 1.10.6 What this section does not assume

The current source set uses enums heavily, but does not yet give them a dedicated standalone spec page. Accordingly, this section defines only what is already stable and visible in the source material:

- enums are nominal named sets of constants
- members may have explicit values
- enums are used directly in ordinary type positions
- status enums commonly align `OK = 0` with Camp’s ordinary success convention

More detailed representation rules, if any, should be added only when they are intentionally fixed.

## 1.11 `newtype`

`newtype` provides nominal distinction over an underlying structural representation.

It exists for cases where the machine representation is already right, but the type identity is too weak.

### 1.11.1 Purpose

Typical uses include:

- foreign handles
- IDs
- bit masks
- pointer-like opaque tokens
- semantically distinct integers with identical representation
- named callable signatures

Examples:

```camp
newtype WindowHandle: nint;
newtype UserId: uint;
newtype NativeBufferPtr: void*;
newtype PermissionBits: uint;
```

```camp
newtype delegate bool Predicate(int value);
newtype async void Completion(int result, thrown CalcError);
```

These values may all be ordinary integers, pointers, or callable shapes at the machine level, but Camp treats them as different types.

### 1.11.2 Core rule

A `newtype`:

- is a real distinct nominal type
- has the same ABI representation as its underlying structural form
- requires explicit casts to cross the boundary
- does not invent hidden wrapper storage, hidden fields, or hidden runtime behavior

Example:

```camp
newtype UserId: uint;
newtype GroupId: uint;

void loadUser(UserId id);
void loadGroup(GroupId id);

UserId user = (UserId)10;
GroupId group = (GroupId)10;

loadUser(user);   // OK
loadGroup(group); // OK
loadUser(group);  // ERROR
```

This is exactly the sort of mistake `newtype` is meant to prevent.

### 1.11.3 Underlying forms

Camp v1 uses two `newtype` families.

| Family | Form |
|---|---|
| value `newtype` | `newtype TypeName: UnderlyingType;` |
| callable `newtype` | `newtype delegate R Name(...);` and related callable forms |

Allowed value underlyings are:

- numeric types
- pointer types

Examples:

```camp
newtype PixelCount: int;
newtype UserToken: ulong;
newtype DataPtr: byte*;
newtype OpaqueState: void*;
```

Disallowed value underlyings include:

```camp
newtype Name: String;      // ERROR
newtype Values: int[];     // ERROR
newtype Client: HttpClient;// ERROR
```

Callable `newtype` underlyings use the ordinary callable forms:

```camp
newtype fn int Parser(StringView text);
newtype delegate bool Predicate(int value);
newtype once void Completion(int result);
newtype async int Loader(StringView path, thrown IoError);
newtype async iter StringView? LineSource();
```

### 1.11.4 Declaration forms

A value `newtype` declaration may be:

```camp
newtype TypeName: UnderlyingType;
```

or exported:

```camp
export newtype TypeName: UnderlyingType;
```

A callable `newtype` places the name inside the signature:

```camp
newtype delegate bool Predicate(int value);
export newtype async void Completion(int result, thrown CalcError);
```

A `newtype` may also open a member scope:

```camp
newtype FileHandle: nint
{
	bool isValid()
	{
		return this != 0;
	}
}
```

### 1.11.5 Methods on `newtype`

A `newtype` scope may contain ordinary methods.

It may not contain fields, virtual methods, constructors, or destructors.

Examples of invalid members:

```camp
newtype Counter: int
{
	int value;          // ERROR
	virtual void a();   // ERROR
	Counter();          // ERROR
	~Counter();         // ERROR
}
```

This keeps `newtype` aligned with its purpose: nominal distinction over an existing representation, not a miniature aggregate object model.

### 1.11.6 `this` semantics

In a `newtype` instance method:

- `this` is passed by value
- `this` is read-only

So code like this is intentionally invalid:

```camp
newtype Counter: int
{
	void increment()
	{
		this += 1; // ERROR
	}
}
```

The correct mental model is not “tiny mutable object.” The correct mental model is “typed value with helper methods.”

### 1.11.7 ABI representation

A `newtype` has the same ABI representation as its underlying structural form.

That means:

- passing it uses the same machine-level convention
- returning it uses the same ABI rule as the underlying form
- storing it uses the same representation as the underlying form

For callable `newtype`s, this also means the nominal name survives in ABI signatures.

### 1.11.8 Header generation

In generated C headers, an exported `newtype` is emitted as a typedef of its underlying C form.

Conceptually:

```camp
export newtype FileHandle: nint;
```

may produce:

```c
typedef intptr_t FileHandle;
```

Likewise, an exported callable `newtype` produces a named function-signature typedef rather than spelling the structural callable form inline at every use site.

The important point is not the exact C spelling. The important point is that the semantic type name survives in the public header.

### 1.11.9 Method naming

Methods declared on a `newtype` use the `newtype`’s own name in symbol generation.

Conceptually:

```camp
newtype FileHandle: nint
{
	bool isValid();
}
```

lowers like:

```c
bool FileHandle_isValid(FileHandle this_value);
```

not as though the method belonged to `nint`.

That preserves the nominal identity the programmer explicitly introduced.

## 1.12 Conversions

Camp’s conversion model is intentionally smaller and more visible than that of many modern languages.

This section defines the core conversion relationships that are already fixed by the current source documents.

### 1.12.1 Design principles

The stable pattern across the language is:

- allow implicit conversion where one form is clearly a widening or convenience view of another
- require explicit conversion when crossing a nominal boundary
- avoid hidden ownership changes
- avoid hidden materialization of storage
- avoid hidden unwrapping of grouped forms

The result is a conversion model that is practical, but not eager to guess.

### 1.12.2 Primitive character conversions

The current built-in character conversion chain is:

Implicit widening:

```text
achar -> char -> wchar -> uchar
```

Explicit narrowing:

```text
uchar -> wchar -> char -> achar
```

This is the clearest primitive conversion relationship presently fixed in the source documents.

### 1.12.3 Optional lifting

A value of type `T` implicitly converts to `T?`.

```camp
int? count = 5;
String? maybeText = someString;
```

This is a genuine convenience conversion and one of the very few grouped conversions that Camp makes implicit by default.

There is no opposite implicit conversion. `T?` does not implicitly unwrap to `T`.

### 1.12.4 String widening

Owning string types implicitly widen to their corresponding view types:

```text
String   -> StringView
WString  -> WStringView
AString  -> AStringView
```

This follows from the current narrowing declarations:

```camp
params String: StringView;
params WString: WStringView;
params AString: AStringView;
```

The reverse direction is not implicit:

```text
StringView  -/-> String
```

That rule preserves ownership clarity.

### 1.12.5 Structural grouped to nominal grouped

A structural grouped value may convert to a nominal grouped type when the shapes match and the naming rule is satisfied.

Allowed:

```camp
params Point(int x, int y);

Point a = (1, 2);
Point b = (x: 1, y: 2);
Point c = 1, 2;
```

Rejected:

```camp
Point d = (left: 1, right: 2);
Point e = (y: 1, x: 2);
```

This preserves both structural convenience and nominal meaning.

### 1.12.6 Erasing nominal identity

A nominal grouped value does not implicitly become structural.

Use `(params)` when explicit erasure of nominal identity is desired:

```camp
Point p = (x: 1, y: 2);
auto structural = (params)p;
```

### 1.12.7 Grouped value to materialized storage

Use `(struct)` when a grouped value must become real copyable storage with one address:

```camp
auto grouped = (x: 5, y: 10);
auto stored = (struct)grouped;
```

This is a value conversion, not a constructor call.

### 1.12.8 Materialized storage to grouped value

Use `(params)` when a storage value should be viewed as grouped form:

```camp
struct Size
{
	int width;
	int height;
}

Size s = { .width = 640, .height = 480 };
auto grouped = (params)s;
```

### 1.12.9 `T` and `struct(T)` value conversion

At the value level, a grouped type `T` and its materialized storage counterpart `struct(T)` are interconvertible. Fixed structs and classes are not part of this conversion.

That does **not** collapse pointer identity. The value conversion is easy; storage identity remains real.

This distinction is one of the main reasons Camp keeps grouped values and storage values separate instead of pretending they are the same thing.

### 1.12.10 Newtype conversions

Crossing a `newtype` boundary is explicit only.

```camp
newtype UserId: uint;

UserId id = (UserId)42;
uint raw = (uint)id;
```

There is no implicit conversion in either direction, and sibling `newtype`s over the same base type remain distinct.

### 1.12.11 No implicit optional unwrapping

There is no implicit conversion from `T?` to `T`.

Presence must be checked explicitly through `.specified`, and the payload is read explicitly through `.value`.

This keeps optional behavior consistent with Camp’s general preference for visible control flow.

### 1.12.12 No implicit owning conversion from views

A view type does not implicitly become an owning string type.

```camp
StringView view = "hello";
String owned = view; // not an implicit conversion
```

Ownership-changing operations are expected to be explicit and usually allocation-bearing.

### 1.12.13 No implicit aggregate address formation

A grouped value does not implicitly materialize itself merely because code asks for a pointer-shaped use that truly requires one whole-object address.

Materialize first:

```camp
auto pair = (x: 1, y: 2);
auto temp = (struct)pair;
usePointPointer(&temp);
```

This rule prevents a large class of hidden temporary-creation behavior.

### 1.12.14 Call spreading is not general conversion

Structural grouped values spread in calls. That does not mean they “convert to anything with the right number of fields everywhere.”

Spreading is specifically a call-argument rule. It is not initializer-list spreading, not hidden storage synthesis, and not a general-purpose aggregate coercion rule.

### 1.12.15 Conversions and deletion are independent

Delete availability is not a conversion rule.

In particular:

- `String` may widen to `StringView`
- `String` may support `delete`
- `StringView` does not thereby become deletable

Similarly, a grouped form is not deletable merely because some related form with a similar shape is.

### 1.12.16 Conversions in generic code

Generics add one important wrinkle.

In erased generic code:

- `in T`, `out T`, and return `T` may materialize storage when transport requires it for copyable types
- for classes and fixed structs, generic use follows the same non-copying rules as ordinary class-like use
- `T*` means pointer to the materialized storage form of `T` for grouped or otherwise materialized copyable types, and pointer to the instance for fixed types

So for a grouped or otherwise materialized copyable type in that specific generic context:

```text
T*  means  struct(T)*
```

This is not a general rewrite of pointer meaning. It is the precise rule for erased-generic pointer-to-storage contracts.

### 1.12.17 Practical summary table

| From | To | Implicit? | Notes |
|---|---|---|---|
| `achar` | `char` / `wchar` / `uchar` | yes, by widening chain | representation widening |
| `char` | `wchar` / `uchar` | yes | representation widening |
| `wchar` | `uchar` | yes | representation widening |
| `uchar` | `wchar` / `char` / `achar` | no | explicit narrowing |
| `T` | `T?` | yes | optional lifting |
| `String` | `StringView` | yes | ownership-to-view widening |
| `WString` | `WStringView` | yes | ownership-to-view widening |
| `AString` | `AStringView` | yes | ownership-to-view widening |
| structural grouped | matching nominal grouped | sometimes | names must be unnamed or exact |
| nominal grouped | structural grouped | no | use `(params)` |
| grouped value | copyable storage value | no direct hidden materialization | use `(struct)` |
| copyable storage value | grouped value | no hidden explosion | use `(params)` |
| `newtype` | underlying intrinsic type | no | explicit cast required |
| underlying intrinsic type | `newtype` | no | explicit cast required |

The main pattern is simple: implicit conversion is reserved for obvious widening and convenience cases; explicit conversion is used for nominal boundaries and storage-boundary changes.
# 2. Data Structures

Camp has two primary user-defined type categories:

- `struct`
- `class`

They may look similar in source. Both can declare fields and methods. Both can declare constructors and destructors. Both can implement interfaces. The difference is not surface spelling alone. The difference is the kind of value the declaration introduces.

A `struct` is a plain value type. A struct may be copyable, or it may be declared `fixed struct` to make the instance fixed in place.

A `class` is an object type with explicit pointer semantics.

That distinction affects all of the following:

- copyability
- layout visibility
- inheritance
- virtual dispatch
- interface implementation strategy
- construction and destruction shape at the ABI boundary

Camp also treats storage and abstraction separately. A grouped value can be materialized into real stored fields, and a class can live either in existing storage or in allocated storage. So the important distinction is never “stack versus heap.” It is always the value model.

This section defines that model.

## 2.1 `struct` and `class`

Camp uses `struct` for ordinary data and `class` for objects with identity.

### 2.1.1 Quick comparison

| Feature | copyable `struct` | `fixed struct` | `class` |
|---|---|---|---|
| Core model | plain value type | fixed-in-place value type | object type with identity |
| Copy semantics | copied by value | not copyable by ordinary value assignment | not copyable by ordinary value assignment |
| Pointer spelling | explicit when needed | explicit when needed | always explicit |
| Inheritance | none | none | single inheritance |
| Virtual dispatch | never | never | optional and explicit |
| Interfaces | may implement | may implement | may implement |
| Exported layout | visible | visible | opaque |
| Hidden `_vt` field | never | never | only for `virtual class` / `abstract class` |
| Hidden interface fields | never | never | one per declared implemented interface |
| Constructors | zero or one explicit constructor | zero or one explicit constructor | one explicit constructor, or implicit parameterless constructor |
| Destructors | optional | optional | optional |
| `init` | allowed | allowed | allowed |
| `new` | allowed | allowed | allowed |

The last two rows matter because they dispel a common wrong intuition. A `class` is not “the heap kind,” and a `struct` is not “the stack kind.” Both can be created in existing storage or allocated storage. The difference is value model, layout visibility, and copyability.

### 2.1.2 `struct`: plain value type

A `struct` behaves like ordinary stored data.

Unless declared `fixed struct`, its defining properties are:

- it is copied by value
- it has no inheritance
- it has no hidden dispatch field
- its full layout is visible wherever the type itself is layout-visible
- passing, returning, and assigning it use ordinary value semantics unless another feature explicitly says otherwise

Example:

```camp
struct Point
{
	int x;
	int y;
}

Point translate(Point p, int dx, int dy)
{
	p.x += dx;
	p.y += dy;
	return p;
}

void sample()
{
	Point a = { .x = 10, .y = 20 };
	Point b = a;
	b.x = 99;
}
```

After `Point b = a;`, the two values are independent. That is the baseline rule for copyable structs.

A struct may still own resources, define methods, and define a destructor. None of that changes its category. It remains a value type.

### 2.1.3 `fixed struct`: fixed-in-place struct

A `fixed struct` is a struct whose instance may not be copied by ordinary safe operations after it has been created. It has class-style copyability rules while remaining a struct in all other respects.

Its defining properties are:

- it has no inheritance
- it has no hidden dispatch field
- its full layout is visible wherever the type itself is layout-visible
- it may be constructed in existing storage with `init` or allocated storage with `new`
- after creation, the instance can be used through its own storage or through explicit pointers to that storage
- assigning, passing, returning, or extracting the instance by value is not allowed when that operation would copy it

Example:

```camp
fixed struct TextBuilder
{
	char* buffer;
	nuint length;
	nuint capacity;

	TextBuilder(char* buffer = null, nuint capacity = 0)
	{
		this.buffer = buffer;
		this.length = 0;
		this.capacity = capacity;
	}
}

void sample(char* scratch)
{
	TextBuilder builder = init TextBuilder(scratch, 256);
	TextBuilder* ptr = &builder;

	// TextBuilder copy = builder; // ERROR: fixed structs are not copied by value
}
```

This is useful for stack-allocated builders, parser or scanner state, synchronization primitives, registration tokens, address-sensitive foreign ABI structs, state machines, safe interior pointers, and compiler-generated context storage.

### 2.1.4 `class`: object type with explicit pointer semantics

A `class` represents an object.

Its defining properties are:

- the instance itself is not copied by ordinary value assignment
- pointers to instances are written explicitly with `*`
- a bare class type denotes an actual instance, not a hidden reference
- single inheritance is supported
- virtual dispatch is available, but only when explicitly requested
- exported class layout is opaque across the public ABI

Example:

```camp
class Window
{
	int width;
	int height;

	Window(int width, int height)
	{
		this.width = width;
		this.height = height;
	}

	void resize(int width, int height)
	{
		this.width = width;
		this.height = height;
	}
}

void sample()
{
	Window local = init Window(640, 480);
	Window* alias = &local;
	Window* heap = new Window(800, 600);

	local.resize(700, 500);
	alias.resize(720, 520);
	heap.resize(1024, 768);

	delete heap;
}
```

The key point is that `Window` is the instance type, and `Window*` is the pointer type. Camp does not collapse those into one syntax category.

### 2.1.5 The storage location does not change the type category

Camp intentionally allows both `struct` and `class` values to be constructed either:

- in storage that already exists, using `init`
- in newly allocated storage, using `new`

That means these are both valid categories of code:

```camp
struct Size
{
	int width;
	int height;

	Size(int width, int height)
	{
		this.width = width;
		this.height = height;
	}
}

class Dialog
{
	int width;
	int height;

	Dialog(int width, int height)
	{
		this.width = width;
		this.height = height;
	}
}

void sample()
{
	Size a = init Size(10, 20);
	Size* b = new Size(30, 40);

	Dialog c = init Dialog(200, 100);
	Dialog* d = new Dialog(400, 200);

	delete b;
	delete d;
}
```

So the question is never “Where does this live?” The question is “What kind of thing is this?”

- `Size` is still copied by value whether it is local or heap-backed.
- `TextBuilder` is still fixed in place whether it is local or heap-backed.
- `Dialog` is still a non-copyable object whether it is local or heap-backed.

### 2.1.6 Copyable types and fixed types

The practical copyability difference is easiest to see in assignment and aliasing. A copyable type may be copied by ordinary value operations. A fixed type may not. Classes and fixed structs are fixed types; ordinary structs are copyable types unless another rule says otherwise.

| Operation | copyable struct | fixed struct | class |
|---|---|---|---|
| `a = b` | copies the value | not a value-copy operation | not an object-copy operation |
| `&a` | pointer to the struct value | pointer to the fixed struct value | pointer to the class instance |
| method call on value | operates on that value | operates on that instance | operates on that instance |
| method call on pointer | same member syntax | same member syntax | same member syntax |

Example:

```camp
struct CounterValue
{
	int count;

	void increment()
	{
		this.count++;
	}
}

class CounterObject
{
	int count;

	void increment()
	{
		this.count++;
	}
}

void sample()
{
	CounterValue a = { .count = 0 };
	CounterValue b = a;
	b.increment();
	// a.count == 0, b.count == 1

	CounterObject x = init CounterObject();
	CounterObject* p = &x;
	p.increment();
	// x.count == 1
}
```

Both declarations use similar syntax. The meaning is different.

For fixed structs, ordinary use is class-like with respect to address identity:

```camp
fixed struct CounterState
{
	int count;

	void increment()
	{
		this.count++;
	}
}

void sample()
{
	CounterState state = { .count = 0 };
	CounterState* p = &state;
	p.increment();
	// state.count == 1
}
```

### 2.1.7 Inheritance belongs only to classes

Structs do not inherit from other structs. This applies to both copyable structs and fixed structs. There is no struct base subobject, no struct override model, and no struct virtual dispatch.

Classes support single inheritance:

```camp
class Connection
{
	int id;
}

class SecureConnection: Connection
{
	bool verifyCertificates;
}
```

The base subobject of a derived class begins at offset `0`. This prefix-layout rule is central to Camp’s class ABI model.

Conceptually:

```c
struct Connection
{
	int32_t id;
};

struct SecureConnection
{
	struct Connection base;
	bool verifyCertificates;
};
```

That prefix layout makes several later rules straightforward:

- `Derived*` to `Base*` conversion
- base method lowering
- virtual dispatch in class hierarchies
- fixup thunks for interface implementation

### 2.1.8 Exported layout visibility

Camp treats exported structs and exported classes differently.

#### Exported struct

An exported struct remains layout-visible.

```camp
export struct GuidPair
{
	Guid left;
	Guid right;
}

export fixed struct ParserState
{
	nuint position;
	nuint line;
}
```

Its public C-facing representation includes its actual fields. This applies to both copyable structs and fixed structs.

#### Exported class

An exported class remains opaque.

```camp
export class HttpClient
{
	String baseUrl;
	int timeoutMs;
}
```

Public consumers see that `HttpClient` exists and can name pointers to it, but they do not see the class layout. The full layout exists only in the private header.

This difference is intentional.

- structs are part of the data ABI directly
- classes are part of the callable ABI directly, but their storage is private

### 2.1.9 Structural storage forms

Section 1 introduced grouped values and their storage counterpart forms. The storage-side forms belong conceptually with copyable storage, not with fixed types.

Camp provides:

- `struct(T)` — materialized storage for a grouped type
- `struct(...)` — anonymous structural storage with real fields

These are storage forms, not object forms.

Example:

```camp
auto grouped = (x: 10, y: 20);
auto stored = (struct)grouped;
```

The result is a real one-address storage object. That places it in the copyable value/storage side of the language. It does not behave like a class or fixed struct.

Anonymous `struct(...)` forms follow stricter compatibility rules than structural `params`:

- field names must match
- field order must match
- field types must match

They are useful for locals, fields, casts, and temporary storage, but they are not part of the exported nominal ABI surface.

## 2.2 Fields, Methods, Constructors, and Destructors

Once a type category is chosen, the next questions are ordinary ones:

- what members may be declared
- how those members are named and called
- how instances are constructed
- how instances are torn down

### 2.2.1 Fields

A field is stored state declared within a struct or class body.

```camp
struct Size
{
	int width;
	int height;
}

class ImageCache
{
	Map<String, Image> entries;
	int version;
}
```

The key difference is not field syntax but field visibility and layout exposure.

#### Struct fields

If a struct’s layout is visible, all of its fields are visible.

Structs do not have the public/private header split that classes use for layout opacity. An exported struct’s public ABI surface includes its actual fields.

A copyable struct may not contain a fixed struct or class instance as an in-place field, because copying the enclosing value would copy the fixed field. A fixed struct may contain fixed struct fields, and it may contain in-place class fields when the class layout is visible. A class may also contain a fixed struct field.

#### Class fields

Class fields are part of the concrete object layout, but exported class layout is private. Public callers do not access those fields directly through the generated C ABI surface.

This means Camp code inside the defining module may know a class’s full field layout while outside C consumers see only an opaque type.

### 2.2.2 Methods

A method is an ordinary function associated with a receiver type.

A method may be declared inside the type body. In that case, the receiver type is supplied by the enclosing type declaration.

```camp
struct Accumulator
{
	int value;

	void add(int delta)
	{
		this.value += delta;
	}
}
```

A method may also be declared outside the type body by declaring an ordinary function whose first parameter is named exactly `this`.

```camp
struct Counter
{
	int value;
}

void increment(Counter* this)
{
	this.value += 1;
}
```

The `this` parameter is the receiver. It is not a modifier and it does not have another source-level name.

A type is complete at the end of its declaration. Fields, constructors, destructors, interface methods, and `abstract`, `virtual`, `override`, and `sealed` methods must be declared in the type body. Methods declared outside the type body are ordinary receiver methods; they do not affect layout, vtable order, or interface conformance.

### 2.2.3 Method-like invocation

A function whose first parameter is named `this` gains method-style invocation when the method symbol is visible without namespace qualification.

```camp
Counter c = { .value = 0 };
c.increment();
```

Conceptually, that lowers like an ordinary receiver-first function:

```c
void Counter_increment(struct Counter* this);
```

The exact helper spelling is part of the source and ABI surface. Camp does not require a hidden runtime object model to support method syntax. Methods are ordinary ABI-visible functions with receiver conventions.

A receiver method that must be named with namespace qualification is called by its qualified symbol name, not by receiver syntax.

```camp
MyModule::CustomerArray_processOrders(customers);
```

The symbol of an instance method is the type name of its `this` parameter, followed by `_`, followed by the method name. In-scope and out-of-scope declarations use the same rule.

```camp
// Symbol: Point_distanceFromOrigin
double distanceFromOrigin(in Point this);

// Symbol: CustomerArray_processOrders
void processOrders(Customer*[] this);

// Symbol: Customer_processIfSpecified
void processIfSpecified(Customer*? this);
```

When the `this` parameter contains an array type, `Array` is added to the type-name portion of the symbol. Other declarators are not significant for this purpose. Generic receiver types contribute no type name.

```camp
// Symbol: addItems
void addItems<T: any>(in T this, nuint count);

// Symbol: Array_indexOf
nint indexOf<T: any>(const T[] this, in T match, sizeof(T));

// Symbol: ArrayArray_find
nint find<T: any>(const T[][] this, in T match, sizeof(T));
```

Symbol names share one module namespace. If two declarations would produce the same symbol name, the program is invalid. User declarations also may not collide with compiler-generated helper symbols.

### 2.2.4 `this`

Inside an instance method, `this` refers to the current receiver.

For structs and classes, `this` behaves like the current instance storage. The method can read and write fields in the ordinary way:

```camp
class Meter
{
	int value;

	void reset()
	{
		this.value = 0;
	}
}
```

The important semantic difference is still the underlying type category:

- modifying a struct method receiver modifies that struct value
- modifying a class method receiver modifies that class instance

An in-scope instance method may spell the receiver explicitly as the first parameter, but it may not declare an explicit receiver type or transport mechanism:

```camp
void appendChild(escaped this, Widget* child)
void paint(const this)
void detach(unscoped this)
```

Rules for in-scope explicit receivers:

- the receiver parameter must be named exactly `this`
- it appears first
- it has no explicit type because the receiver type is implied
- it may carry only `const`, `unscoped`, or `escaped`

If `this` is omitted, the receiver still exists implicitly. Lifetime defaults for the implicit receiver are defined in the lifetime chapter.

An out-of-scope receiver method must declare a fully specified receiver type:

```camp
void reset(Meter* this)
bool isOrigin(in Point this)
void inspect(const Point this)
void inspectPtr(const Point* this)
```

An out-of-scope `this` parameter may use the ordinary parameter surface for its type and transport, except that it may not be `out` or `thrown`.

### 2.2.5 Static methods

A method may also be static when declared inside a type body.

Static methods:

- are associated with the type
- do not have an instance receiver
- may be accessed through type-name dot syntax
- also have a canonical symbol name

Example:

```camp
class FileHandle
{
	static FileHandle* open(StringView path);
}

auto file = FileHandle.open(path);
auto same = FileHandle_open(path);
```

A static method may not declare an explicit `this` parameter.

Static methods are still ordinary functions at the ABI level. They simply do not carry `this`.

A static method's symbol is the declaring type name, followed by `_`, followed by the method name. A static method declared out of scope is written directly using that canonical symbol name.

When an expression begins with `TypeName.`, or with a namespace-qualified type name followed by `.`, the type name must resolve to a known type. Static lookup then includes visible no-receiver functions whose symbols begin with `TypeName_`; the member name is the portion after the underscore.

### 2.2.6 Constructors: unified surface syntax

Camp uses dedicated constructor syntax for both structs and classes:

```camp
TypeName(...)
{
	...
}
```

Example:

```camp
struct Range
{
	int start;
	int end;

	Range(int start, int end)
	{
		this.start = start;
		this.end = end;
	}
}
```

```camp
class Logger
{
	String name;

	Logger(String name)
	{
		this.name = name;
	}
}
```

Camp considered more fragmented lifecycle surfaces, but unified constructor syntax keeps ordinary code easy to read while still lowering cleanly to explicit helpers.

### 2.2.7 Struct constructor rules

A struct may declare:

- zero constructors
- or one constructor

Rules:

- the constructor is optional
- if present, it must have at least one parameter
- it initializes already-existing storage
- it does not allocate storage by itself

Example:

```camp
struct LeftRightPadding
{
	int left;
	int right;

	LeftRightPadding(int left, int right)
	{
		this.left = left;
		this.right = right;
	}
}
```

The “at least one parameter” rule is important. It leaves parameterless struct formation to ordinary value initialization rather than giving it constructor semantics.

So this:

```camp
Rect r = {};
```

is raw value formation, not a constructor call.

### 2.2.8 Class constructor rules

A class may declare:

- one explicit constructor
- or no constructor, in which case the compiler provides an implicit parameterless constructor

Rules:

- constructor overloads do not exist
- optional construction behavior should use optional parameters or options objects
- a class with no explicit constructor behaves as though it had an empty parameterless constructor

Example with an explicit constructor:

```camp
class HttpClient
{
	HttpClientOptions options;

	HttpClient(HttpClientOptions* options = null)
	{
		this.options = options ?? HttpClientOptions();
	}
}
```

Example with an implicit parameterless constructor:

```camp
class TokenCache
{
	int count;
}
```

Conceptually, the second form behaves as though it had:

```camp
TokenCache()
{
}
```

### 2.2.9 Base constructor invocation

A derived class constructor may call the base constructor using `base(...)`.

```camp
class Connection
{
	String name;

	Connection(String name)
	{
		this.name = name;
	}
}

class SecureConnection: Connection
{
	bool verifyCertificates;

	SecureConnection(String name, bool verifyCertificates = true)
	{
		base(name);
		this.verifyCertificates = verifyCertificates;
	}
}
```

Rules:

- `base(...)` is valid only in a class constructor
- if present, it must be the first constructor action
- it may not be conditional or delayed
- if the base requires constructor arguments and has no accessible parameterless constructor, omitting `base(...)` is an error
- if the base has an accessible parameterless constructor, omission means that constructor is used

Structs do not have base chaining because structs do not inherit.

### 2.2.10 Destructors: unified surface syntax

Camp uses dedicated destructor syntax for both structs and classes:

```camp
~TypeName()
{
	...
}
```

Examples:

```camp
struct NativeBuffer
{
	char* data;
	nuint length;

	~NativeBuffer(within allocator)
	{
		if (this.data != null)
			allocator.free(this.data);
	}
}
```

```camp
class Socket
{
	nint handle;

	~Socket()
	{
		if (this.handle != 0)
			closeSocket(this.handle);
	}
}
```

A destructor describes teardown of the instance itself. It does not, by itself, choose stack versus heap policy. Deallocation behavior belongs to the operation that uses the destructor.

### 2.2.11 Struct destructor rules

A struct may declare:

- zero destructors
- or one destructor

Rules:

- the destructor is optional
- it may declare `within allocator`
- it may not declare any other parameters
- it destroys the struct value in place
- it does not imply deallocation of enclosing storage

Example:

```camp
struct TempFile
{
	String path;

	~TempFile()
	{
		deleteFile(this.path);
	}
}
```

A struct destructor is often used for resource-owning value types such as buffers, file wrappers, and temporary handles.

### 2.2.12 Class destructor rules

A class may declare:

- zero destructors
- or one destructor

Rules:

- the destructor is optional
- it may declare `within allocator`
- it may not declare any other parameters

Example:

```camp
class ImageCache
{
	Map<String, Image> entries;

	~ImageCache()
	{
		this.entries.clear();
	}
}
```

Class destruction is conceptually split into two layers at the ABI level:

- destruction of an already-existing instance
- destruction plus deallocation of instance storage

Source code still uses the ordinary destructor declaration surface.

### 2.2.13 Raw value formation versus construction

Camp keeps raw value formation separate from construction.

| Form | Meaning |
|---|---|
| `Rect r = {};` | form a raw value directly |
| `init Rect(...)` | construct in storage that already exists |
| `new Rect(...)` | allocate storage, then construct |
| `delete x;` | run destruction semantics; pointer-form delete may also deallocate |

That distinction matters for both structs and classes.

- raw formation does not call a constructor
- `init` and `new` do call constructor semantics when a constructor exists

Example:

```camp
struct Rect
{
	int x;
	int y;
	int width;
	int height;
}

Rect a = {};
Rect b = { .x = 10, .y = 20, .width = 30, .height = 40 };
```

No constructor is involved there. For a fixed struct, raw value formation is allowed only as direct formation in the final storage; it may not be used to create a temporary value that is then copied into place.

### 2.2.14 `init` and `new` as the ordinary lifecycle surface

Both structs and classes may be used with both operators.

```camp
Size local = init Size(640, 480);
Size* heap = new Size(800, 600);

Window dialog = init Window(320, 200);
Window* popup = new Window(640, 400);
```

Source-level rules that matter here:

- `init` is call-like; parentheses are required
- `new` is call-like; parentheses are required
- classes may be initialized in local storage
- structs do not gain hidden parameterless constructors
- target-typed `new()` is available when a parameterless constructor path exists

Examples:

```camp
Window* dialog = new();
auto cache = new TokenCache();
```

The full allocator model and detailed `delete` rules are defined later. What matters here is the ordinary lifecycle surface attached to user-defined types.

### 2.2.15 Trailing initializer syntax after construction

An `init` or `new` expression may be followed by initializer syntax that applies after construction.

```camp
class HttpRequest
{
	StringView method;
	StringView url;
	HeaderCollection headers;
	int timeoutMs;

	HttpRequest(StringView method, StringView url)
	{
		this.method = method;
		this.url = url;
		this.timeoutMs = 30000;
	}
}

auto retry = init HttpRequest("GET", url)
{
	.headers.["accept"] = "application/json",
	.timeoutMs = 5000,
};
```

```camp
class HttpClient
{
	HeaderCollection defaultHeaders;
	int timeoutMs;
	bool followRedirects;

	HttpClient()
	{
		this.timeoutMs = 30000;
		this.followRedirects = true;
	}
}

auto client = new HttpClient()
{
	.defaultHeaders.["user-agent"] = "CampBot/1.0",
	.timeoutMs = 10000,
};
```

This pattern is useful when the constructor establishes the main invariant and a few field or property overrides follow.

For lifetime checking, the constructor call and its trailing initializer are part of the same initialization operation. Pointer-bearing values retained by trailing initializer assignments participate in the initialized value's lifetime in the same way as pointer-bearing values retained by the constructor body.

### 2.2.16 Arrays of constructor-bearing element types

Camp does not implicitly call a constructor for every element of `new T[n]` or `init T[n]`.

```camp
auto clients = new HttpClient[10];
auto states = init ParserState[10];
auto rects = init Rect[50];
```

All are legal forms, but element construction is not implied. If the element type defines a constructor, the compiler should warn rather than invent hidden bulk-construction behavior.

Fixed array elements use class-like copyability rules. An individual element may be initialized in place, but it may not be copied out by value.

```camp
fixed struct ParserState
{
	nuint position;

	ParserState(nuint position = 0)
	{
		this.position = position;
	}
}

void sample()
{
	ParserState[] states = init ParserState[4];
	states[0] = init ParserState();

	// auto copy = states[0]; // ERROR: copies a fixed struct value
}
```

This keeps array formation simple and ABI-transparent.

### 2.2.17 Public and private header model

Camp distinguishes two generated views of type information:

- a public header
- a private header

The distinction matters most for classes, but it also affects non-exported structs.

#### Public header

The public header contains the surface visible across the C ABI.

- exported structs, including fixed structs, appear with full layout
- exported classes appear as opaque types
- exported methods and helper functions appear with their callable ABI surface
- exported interface implementation relationships may expose non-fixup vtable objects

#### Private header

The private header contains full type details and internal helper surfaces used within the defining module.

- full class layout appears here
- hidden `_vt` and `_vt_InterfaceName` fields appear here
- internal construction and destruction helpers appear here
- non-exported structs, including fixed structs, appear here with full layout

This is not just an implementation convenience. It is part of Camp’s type model. A Camp module knows more about its own classes than outside consumers do.

### 2.2.18 Exported versus non-exported structs

An exported struct, including a fixed struct, includes its real layout wherever that export surface is visible:

```camp
export struct GuidPair
{
	Guid left;
	Guid right;
}
```

Conceptually, the public C-facing form contains those fields directly.

A non-exported struct appears only where it is needed privately. If an exported signature needs to mention `Type*`, the public header may contain only an opaque forward declaration for that struct. That forward declaration exists only when necessary.

### 2.2.19 Generated helper surfaces

Camp source uses `init`, `new`, constructors, destructors, and `delete`. Public C callers use generated helpers.

For a struct `TypeName`, including a fixed struct:

| Helper | Meaning |
|---|---|
| `TypeName_init` | in-place construction |
| `TypeName_destroy` | in-place destruction |

For a class `TypeName`:

| Helper | Meaning |
|---|---|
| `TypeName_op_initnew` | non-allocating typed initialization of existing storage |
| `TypeName_create` | allocation plus construction |
| `TypeName_op_delete` | destruction without deallocation |
| `TypeName_destroy` | destruction plus deallocation |

### 2.2.20 Helper meaning

These helper names are not arbitrary. They reflect the exact lifecycle split Camp preserves.

- `TypeName_init` never allocates; the caller already has storage.
- `TypeName_destroy` for a struct destroys the value in place.
- `TypeName_op_initnew` for a class performs typed initialization of an already-existing instance.
- `TypeName_create` for a class allocates, performs any hidden typed scaffolding, and then constructs.
- `TypeName_op_delete` for a class tears down an existing instance without freeing its storage.
- `TypeName_destroy` for a class performs teardown and then deallocation.

These names matter for ABI reasoning, but ordinary Camp code still writes the language surface:

```camp
Widget local = init Widget();
Widget* heap = new Widget();
delete heap;
```

## 2.3 Virtual and Abstract Types

Virtual dispatch is available in Camp, but it is never implicit.

Camp does not give every class a hidden vtable pointer “just in case.” A class participates in virtual dispatch only when its declaration says so.

### 2.3.1 Opt-in virtual dispatch

There are three relevant declaration forms:

```camp
class FileBuffer
{
	...
}
```

```camp
virtual class Widget
{
	virtual void paint();
}
```

```camp
abstract class Device
{
	abstract int process(byte[] buffer);
}
```

The first form has no virtual dispatch.

The second and third forms do.

A class that derives from a virtual or abstract base must itself be declared `virtual class` or `abstract class`.

### 2.3.2 `_vt` exists only when needed

When a class participates in virtual dispatch, the compiler adds a hidden `_vt` field.

Rules:

- `_vt` is the first field in the class
- it is visible in the private-header layout
- it is not exposed in the public header
- user code may not declare it manually

This gives Camp a predictable dispatch model without burdening ordinary non-virtual classes.

### 2.3.3 `_vt` assignment and construction

`_vt` assignment is part of compiler-generated typed construction scaffolding.

It is:

- not the allocator’s job
- not written by user constructors
- assigned exactly once for the most-derived concrete virtual class before constructor execution begins

Consequences:

- internal construction can assign the most-derived `_vt` and then run constructor chaining
- public creation helpers can do the same
- base constructors do not overwrite `_vt`

This keeps virtual construction explicit and mechanically simple.

### 2.3.4 Abstract classes

An abstract class may:

- declare abstract methods
- declare virtual methods
- declare a constructor
- declare a destructor
- be used through pointers

An abstract class may not:

- be directly instantiated as a concrete object
- expose a public `Type_create` helper

Example:

```camp
abstract class Device
{
	abstract int process(byte[] buffer);
	virtual ~Device();
}
```

An abstract class constructor exists so derived concrete classes can chain to it. It does not make the abstract type directly constructible.

### 2.3.5 Methods are non-virtual by default

An instance method is non-virtual unless it is explicitly declared otherwise.

```camp
class Label
{
	void setText(StringView value);
}
```

That method is ordinary and non-virtual.

Only instance methods may be virtual or abstract. Virtual and abstract methods may not declare their own type parameters.

### 2.3.6 Declaring virtual and abstract methods

```camp
virtual class Widget
{
	virtual void paint();
}
```

```camp
abstract class Device
{
	abstract int read(byte[] buffer);
}
```

A virtual method provides a dispatch slot that may have a body.

An abstract method provides a dispatch slot that must be implemented by a concrete derived type.

### 2.3.7 Overriding

A derived implementation must be marked `override`.

```camp
virtual class Button: Widget
{
	override void paint()
	{
		drawButtonFace();
	}
}
```

Camp requires the marker rather than inferring override intent. That makes slot participation explicit and helps prevent accidental shadowing.

### 2.3.8 Sealed overrides

An override may be marked `sealed`.

```camp
virtual class FancyButton: Button
{
	sealed override void paint()
	{
		drawFancyButtonFace();
	}
}
```

A sealed override ends further overriding for that slot in derived classes.

### 2.3.9 Virtual slots and export

The exported callable virtual surface belongs to the original slot declaration.

If a method is first declared `export virtual` or `export abstract`, the compiler generates the exported dispatching invoker for that slot. Later overrides do not create separate exported slot surfaces.

This keeps the ABI story slot-based rather than override-based.

### 2.3.10 Destructor slots in virtual hierarchies

If a class hierarchy participates in virtual dispatch and also has a destructor, the destructor follows the same slot discipline.

Rules:

- the destructor slot is declared on the ultimate base class
- if that base is `virtual class` or `abstract class`, the destructor must be virtual there
- derived classes participate by `override`
- a derived class may not establish a new independent lifecycle destructor slot later in the hierarchy
- if the ultimate base has no destructor, a derived class may not establish one later as the hierarchy’s lifecycle destructor

Example:

```camp
export abstract class Device
{
	abstract int process(byte[] buffer);
	virtual ~Device();
}

export virtual class FileDevice: Device
{
	nint handle;

	override int process(byte[] buffer)
	{
		return readHandle(this.handle, buffer);
	}

	override ~FileDevice()
	{
		if (this.handle != 0)
			closeHandle(this.handle);
	}
}
```

### 2.3.11 Public ABI restrictions

Across the public ABI:

- class layout remains opaque
- `_vt` remains private
- deriving from abstract or virtual classes across the public ABI is not supported

This is an intentional trade-off in favor of a simpler and more predictable exported model.

Within a Camp module, the compiler has the full private-header layout and helper surfaces it needs. Across the public C ABI, Camp prefers a smaller, more stable story.

## 2.4 Interfaces

Interfaces in Camp are explicit ABI-visible contracts. They are not hidden runtime objects, they are not just structural method matching, and they are not delegate-like grouped `(context, call)` values.

They form their own representation category.

### 2.4.1 What an interface is

An interface declaration defines a nominal vtable shape.

```camp
interface IRef
{
	void retain();
	void release();
}
```

The important design choice is that Camp distinguishes two related interface forms:

- `IFoo`
- `IFoo*`

### 2.4.2 Bare `IFoo` versus `IFoo*`

| Form | Meaning |
|---|---|
| `IFoo` | pointer to the interface vtable |
| `IFoo*` | pointer to an interface-instance slot whose stored pointer acts as the context |

Most ordinary code uses `IFoo*`.

- `IFoo` is the explicit vtable form
- `IFoo*` is the ordinary callable interface-instance form

This keeps the representation simple while still exposing the real calling convention.

### 2.4.3 Conceptual lowering model

Given:

```camp
interface IRef
{
	void retain();
	void release();
}
```

Camp conceptually lowers the interface to a vtable whose entries take an explicit context parameter first.

Conceptually:

```camp
struct IRefVTable<U>
{
	@mustinit fn void(U* ctx) retain;
	@mustinit fn void(U* ctx) release;
}
```

The exact generated spelling is less important than the rule:

- each interface entry is a function pointer
- the function pointer receives context first
- `IFoo*` method syntax supplies that context automatically

### 2.4.4 Calling through bare `IFoo`

Calling a method on bare `IFoo` requires supplying context explicitly.

Conceptually:

```camp
IRef vt = SomeType_IRef;
SomeType* obj = ...;

vt.retain(obj);
vt.release(obj);
```

This form is useful when code wants to traffic in the vtable itself.

### 2.4.5 Calling through `IFoo*`

Calling a method on `IFoo*` inserts context automatically.

```camp
IRef* value = obj;
value.retain();
value.release();
```

This is the ordinary ergonomic surface.

### 2.4.6 Nominal conformance

Interface conformance is nominal only.

A type implements an interface because it explicitly declares that interface in its declaration. Structural similarity is not enough. Required interface members are declared in the type body.

```camp
interface ICalculator
{
	int add(int a, int b);
	int subtract(int a, int b);
}

class ExtraCalculator: ICalculator
{
	int extra;

	ExtraCalculator(int extra)
	{
		this.extra = extra;
	}

	int add(int a, int b)
	{
		return this.extra + a + b;
	}

	int subtract(int a, int b)
	{
		return this.extra + (a - b);
	}
}
```

Camp does not infer implementation merely because method names and signatures happen to line up.

### 2.4.7 Every declared interface entry is required in v1

Camp keeps the v1 interface model simple: every declared entry is required.

That means:

- missing required members in the type body are a compile error
- conformance does not silently fill in null entries
- inherited interface entries remain required
- constructor and destructor entries, when declared, are also required

Optional-entry designs were considered, but v1 does not use them.

### 2.4.8 Lifetime annotations are part of interface method contracts

Receiver lifetime annotations and ordinary parameter lifetime annotations are part of an interface method's contract.

That means:

- an implementation method must satisfy the same lifetime contract as the interface method
- changing receiver lifetime requirements changes the callable contract
- an implementation that does not match the interface method's lifetime annotations does not implement that member

This applies equally to an in-scope explicit `this` parameter and to ordinary annotated parameters.

### 2.4.9 Interface inheritance

Interfaces may inherit from other interfaces.

```camp
interface A
{
	void a();
}

interface B: A
{
	void b();
}

interface C: A
{
	void c();
}

interface D: B, C
{
	void d();
}
```

Camp treats interface inheritance as nominal and conceptually flattened:

- base interfaces appear once
- derived interface vtables contain inherited entries plus their own
- diamond shapes do not duplicate the same base contract repeatedly

This keeps the interface layout predictable.

### 2.4.10 Interface casting and ambiguity

Upcasting to a base interface is allowed.

Depending on the layout involved, it may be:

- a no-op
- or an adjusted interface conversion

Downcasting is never implicit. Camp does not provide a general safe runtime downcast for interfaces. Any such cast is explicit and unsafe.

If inherited method names would make an interface call ambiguous, the call must be disambiguated with an explicit cast. Camp does not silently choose one inherited method set.

### 2.4.11 Class implementation of interfaces

When a class implements an interface, the compiler adds one hidden field per **declared** implemented interface:

- `_vt_InterfaceName`

That hidden field stores the fixup vtable pointer for the class/interface pair.

Layout rules:

- `_vt` comes first when the class is virtual or abstract
- hidden interface fields are placed after the ordinary object/base fields of the most-derived class
- interface fields appear in declared interface order
- a class does not get separate hidden fields for every inherited base interface automatically

For each implemented interface, the compiler also generates a standalone non-fixup vtable object:

- `TypeName_InterfaceName`

That vtable may point either:

- directly to concrete implementations
- or to virtual-dispatch invoker thunks when the implementing methods are virtual

### 2.4.12 Class-to-interface conversion

A class instance converts naturally to an interface-instance pointer.

```camp
ExtraCalculator* calc = new ExtraCalculator(5);
ICalculator* iface = calc;

int sum = iface.add(4, 6);
```

No heap boxing is introduced. The resulting interface pointer refers to the class’s stored interface-specific vtable-pointer field.

Calls through `IFoo*` may use fixup thunks that:

1. recover the class instance pointer from the interface field address
2. invoke the real implementation path

### 2.4.13 Virtual overrides and inherited interface implementation

If a base class implements an interface and derived classes are meant to customize that behavior, the intended pattern is:

- the base class declares the interface implementation
- the implementing methods are virtual when customization is intended
- derived classes override those methods

Re-implementing an already-implemented interface again in a derived class is discouraged.

### 2.4.14 Struct implementation of interfaces

Structs do not gain hidden interface fields.

Instead, a pointer to a struct value that implements an interface may convert to an ordinary interface pointer by creating a scoped indirect interface pointer. This applies to both copyable structs and fixed structs.

An indirect interface pointer is compiler-created adapter storage. It is not a type that user code can name directly. The adapter contains:

- first a vtable pointer
- then a pointer to the original struct value

The resulting interface pointer points at the adapter’s vtable-pointer slot. Calls through that pointer use a fixup vtable whose entries use the stored pointer-to-data as the implementation receiver.

Example:

```camp
interface IByteReader
{
	nuint read(byte[] buffer);
}

struct SliceReader: IByteReader
{
	const byte* data;
	nuint length;
	nuint position;

	nuint read(byte[] buffer)
	{
		nuint remaining = this.length - this.position;
		nuint copied = remaining < buffer.length ? remaining : buffer.length;
		this.position += copied;
		return copied;
	}
}
```

This design preserves pointer identity for explicit pointer-based interface use:

- the callee works with the original struct storage
- there is no copied struct value
- there is no hidden heap boxing

### 2.4.15 Scoped-only automatic struct conversion and escaped interfaces

Automatic struct-to-interface conversion is allowed only where the indirect interface pointer’s adapter storage remains valid for the duration of the use.

Typical valid cases:

- passing a pointer to a struct value to a scoped interface-pointer parameter
- storing the interface pointer in a newly initialized local in the current declaration-scope

An indirect interface pointer may exist as a temporary only for scoped interface-pointer arguments. An unscoped interface-pointer argument may not receive such a temporary; the caller must first bind the interface pointer to a newly initialized local in the current declaration-scope.

An indirect interface pointer may not be assigned to an array element, caller-provided argument, field, component of an aggregate, already-initialized local, or any other storage location outside the current declaration-scope initialization.

The adapter is scoped stack storage. It does not implicitly satisfy escaped interface-pointer requirements.

For an `escaped interface`, automatic struct-to-interface conversion is forbidden because that automatic conversion path uses scoped adapter storage.

### 2.4.16 Interface constructors and destructors

Interfaces may declare constructor and destructor requirements as part of the contract.

Example:

```camp
interface ICounterStore
{
	ICounterStore(within allocator);
	~ICounterStore(within allocator);
	void add(nint value);
	nint getTotal();
}
```

#### Constructor entry

An interface constructor lowers to a vtable entry conceptually named `create`.

Rules:

- it has no context parameter
- it conceptually returns a pointer to the implementing instance or context
- it is not called through ordinary instance-style dot syntax on `IFoo*`
- it must explicitly declare `within allocator` as its last parameter

An interface with a constructor is implementable only by a sealed class or by a struct.

#### Destructor entry

An interface destructor lowers to a vtable entry conceptually named `destroy`.

Rules:

- it receives the implementing context pointer
- it must explicitly declare `within allocator` as its last parameter
- the instance itself may be deallocated after destruction completes

For both constructor and destructors, concrete implementations do not need to spell `within allocator` unless they actually use that parameter. The generated interface thunk forwards it only when needed.

An interface constructor entry does not make the interface type itself directly instantiable. This is still invalid:

```camp
new ICounterStore()
```

### 2.4.17 Generic interfaces and generic interface methods

Interfaces themselves may be generic, and interface methods may also be generic.

```camp
interface IComparer<T: any>
{
	int compare(in T left, in T right);
}
```

```camp
interface ITransformer
{
	TResult transform<TSource: any, TResult: any>(in TSource value);
}
```

These are ordinary ABI-visible interface surfaces. The generic rules are defined later; the important point here is that interfaces are not excluded from the normal language surface.

### 2.4.18 Exported interface implementation relationships

Interfaces are part of the ABI surface in their own right. They are not opaque in the same way classes are opaque.

If a struct or class exports an interface implementation relationship, the compiler may expose the appropriate public projection surface for that relationship.

For classes, that public story is tied to the exported non-fixup vtable object for the implementation pair.

Conceptually:

- `TypeName_InterfaceName`

may become part of the exported ABI surface.

Exported class opacity still remains in force. Hidden `_vt_InterfaceName` fields are private-header details, not public class layout.

### 2.4.19 Interface cost model

| Case | Cost |
|---|---|
| bare `IFoo` | one pointer to a vtable |
| `IFoo*` | one pointer to an interface-instance slot |
| class implementation | one hidden stored vtable-pointer field per declared implemented interface |
| struct conversion | a scoped indirect interface pointer adapter may be created |
| interface call | one explicit indirection-based call |

There is no hidden runtime registry, hidden metadata lookup, or heap boxing requirement for ordinary class-backed interface calls.

### 2.4.20 Mental model

The right mental model is simple:

- an interface declaration defines a nominal vtable shape
- bare `IFoo` is a vtable pointer
- `IFoo*` is the ordinary callable interface-instance form
- class implementations store hidden interface-specific vtable-pointer fields
- struct implementations use scoped indirect interface pointer adapters

That model is explicit, ABI-visible, and consistent with the rest of Camp’s design.
# 3. Statements and Expressions

This section defines the executable core of Camp: the statements that control evaluation, the ordinary operators used in expressions, the rules for member and property access, grouped multi-value results, and the syntax used for iteration, slicing, and cleanup.

Camp intentionally keeps most day-to-day statement and operator syntax familiar to C-family programmers. The important work here is therefore not to restate C in full, but to make Camp-specific rules precise: boolean conditions are explicit, member access uses one uniform surface, grouped values participate directly in results and calls, property syntax is a rewrite over methods, and cleanup remains explicit.

## 3.1 Basic Statements

Camp provides the ordinary control-flow statements expected in an imperative language:

- blocks: `{ ... }`
- expression statements
- `if`
- `while`
- `do`
- `for`
- `switch`
- `break`
- `continue`
- `return`

Their everyday use is intentionally conventional. Only Camp-specific differences are called out here.

### 3.1.1 Boolean conditions are explicit

The controlling expression of:

- `if`
- `while`
- `do` / `while`
- the condition part of `for`
- the conditional operator `?:`

must be a `bool`.

Camp does not treat integers, pointers, or enums as implicit truth values.

| Expression | Valid as a condition? | Reason |
|---|---:|---|
| `ready` where `ready: bool` | yes | already `bool` |
| `count > 0` | yes | comparison produces `bool` |
| `ptr != null` | yes | comparison produces `bool` |
| `count` where `count: int` | no | integers do not implicitly become `bool` |
| `ptr` where `ptr: Widget*` | no | pointers do not implicitly become `bool` |

Examples:

```camp
if (count > 0)
	log("items available");

if (socket != null)
	socket.close();

if (count)        // ERROR
	...

while (node)      // ERROR
	...
```

This rule keeps control flow visually explicit and prevents the old C habit of using non-boolean values as conditions.

### 3.1.2 `if`, `while`, `do`, `for`, and `switch`

These statements behave in the expected C-family way.

Examples:

```camp
if (score >= passingScore)
	result = PASS;
else
	result = FAIL;
```

```camp
while (remaining > 0)
{
	consumeOne();
	remaining--;
}
```

```camp
do
{
	poll();
}
while (!done);
```

```camp
for (nuint i = 0; i < values.length; i++)
	total += values[i];
```

```camp
switch (mode)
{
case READ:
	openReader();
	break;
case WRITE:
	openWriter();
	break;
default:
	throw E_INVALID_MODE;
}
```

### 3.1.3 `break` and `continue`

`break` exits the nearest enclosing loop or `switch`.

`continue` advances to the next iteration of the nearest enclosing loop.

These statements also interact with Camp cleanup in the ordinary way: if the scope being exited has `finally` work pending, that cleanup runs before control leaves the scope.

### 3.1.4 `return`

`return` exits the current function.

An ordinary single-value return looks as expected:

```camp
int abs(int x)
{
	if (x < 0)
		return -x;
	return x;
}
```

Camp also allows grouped multi-value returns. Those are described in §3.6.

## 3.2 Operators

Camp provides the usual arithmetic, comparison, logical, bitwise, assignment, and pointer operators familiar from C.

The language deliberately avoids redefining the ordinary meanings of these operators unless there is a strong Camp-specific reason. This section therefore calls out only the differences and the rules that materially affect reading or writing Camp code.

### 3.2.1 Operator families

| Family | Examples | Notes |
|---|---|---|
| arithmetic | `+ - * / %` | ordinary numeric meaning |
| comparison | `== != < <= > >=` | produce `bool` |
| logical | `! && ||` | operate on `bool` |
| bitwise | `~ & \| ^ << >>` | ordinary bitwise meaning |
| assignment | `= += -= *= /= %= &= \|= ^= <<= >>=` | ordinary assignment / compound assignment |
| pointer/address | `&` | ordinary address-of meaning |
| conditional | `?:` | condition must be `bool` |
| member / call / index | `.`, `()`, `[]` | defined separately below |

### 3.2.2 No implicit truthiness

The most important operator-level difference from C is that logical and conditional operations are genuinely boolean.

Examples:

```camp
bool ok = (count != 0) && (ptr != null);
```

```camp
auto result = ready ? primary : fallback;
```

These are invalid:

```camp
bool ok = count && ptr;              // ERROR
auto result = ptr ? a : b;          // ERROR
```

Use an explicit comparison instead:

```camp
bool ok = (count != 0) && (ptr != null);
auto result = (ptr != null) ? a : b;
```

### 3.2.3 Equality and grouped values

When an operator is applied to a grouped `params` value, the shared grouped-value rules apply. In particular, equality compares componentwise.

```camp
params Point(int x, int y);

Point a = (x: 1, y: 2);
auto b = 1, 2;
auto c = 1, 3;

bool same = (a == b);    // true
bool diff = (b != c);    // true
```

The grouped-value model itself was defined earlier. Here it matters only because grouped values appear naturally inside ordinary expressions.

### 3.2.4 Member access is not `->`

Camp does not use the C `->` operator.

Member access is always written with `.` whether the receiver is:

- a value
- a pointer
- a class instance
- a class pointer
- a grouped value with named components

Examples:

```camp
point.x
window.resize(800, 600)
windowPtr.resize(800, 600)
result.remainder
```

This is a surface simplification only. It does not erase the underlying distinction between values and pointers.

### 3.2.5 Precedence

Camp follows ordinary C-family operator precedence closely enough that experienced readers should find expressions unsurprising.

When an expression becomes hard to read, parenthesize it. This is especially advisable when combining:

- grouped-return deconstruction or multi-step call results
- nested property/indexer access
- `await` with chained member access
- range-like slice expressions

## 3.3 Type Inference and Target Typing

Camp supports local type inference with `auto` and destination-driven typing for a number of expression forms.

The design goal is convenience without hidden dynamic behavior. Inference determines a static type. It does not delay typing until runtime.

### 3.3.1 `auto`

`auto` infers the type of a local from its initializer.

```camp
auto count = 0;                    // int
auto title = "Report";             // target-appropriate string type
auto pair = 10, 20;                // structural params(int, int)
auto point = (x: 5, y: 10);        // structural named params
```

`auto` is often the clearest choice when the initializer already makes the type obvious or when the exact grouped form is more verbose than helpful.

### 3.3.2 `auto` in deconstruction

`auto` may be used in deconstruction:

```camp
auto (x, y) = getOrigin();
auto (min, max) = getMinMax(values);
```

Each declared local is inferred independently from the corresponding component.

### 3.3.3 Destination-driven typing

Some expressions are typed from context rather than from their own surface form alone.

Common cases include:

- `default`
- grouped structural literals assigned to nominal grouped types
- initializer forms
- omitted trailing `out` values captured into locals
- some generic arguments and method-group conversions

Examples:

```camp
Point p = (1, 2);
```

Here the structural grouped expression is target-typed to `Point`.

```camp
int? count = default;
```

Here `default` is typed as `int?` by the destination.

```camp
Rect r = { .x = 10, .y = 20, .width = 80, .height = 40 };
```

Here the initializer is typed by the declared destination.

### 3.3.4 Inference remains local and explicit

Camp does not use hidden flow-sensitive retargeting to change the type of an existing local.

A variable declared with `auto` still has one fixed static type after its initializer is analyzed.

```camp
auto value = 0;
value = 10;        // OK
value = "hello";   // ERROR
```

### 3.3.5 Prefer explicit types when the destination matters semantically

Inference is convenient, but explicit spelling is often clearer when one of these is true:

- the distinction between structural and nominal grouped values matters
- a narrow nominal type is intended
- a view type versus owning type matters
- a pointer lifetime annotation matters
- the exact numeric type matters

Examples:

```camp
Point p = (1, 2);              // clearer than auto if nominal identity matters
StringView title = request.Path;
escaped byte* data = getBuffer();
```

## 3.4 Member Access

Member access is written with `.` and is shared across fields, methods, grouped components, and static members.

This uniform surface is one of Camp's most visible simplifications.

### 3.4.1 Instance members

An instance member is accessed through a value or pointer receiver:

```camp
rect.width
rect.area()
window.resize(640, 480)
windowPtr.resize(640, 480)
```

The same surface works for both values and pointers.

### 3.4.2 Static members

A static member is accessed through type-name dot syntax when the type name is visible.

```camp
Theme.Default
Console.writeLine("hello")
FileHandle.openRead(path)
```

The canonical symbol name is also in scope when visible:

```camp
Theme_Default
Console_writeLine("hello")
FileHandle_openRead(path)
```

For static methods declared outside the type body, the declaration uses the canonical symbol name. The compiler recognizes visible no-receiver symbols with a `TypeName_` prefix as candidates for `TypeName.` static lookup. Namespace qualification may qualify the type name before the `.`.

### 3.4.3 Named grouped components

Named grouped values also use `.` for component access:

```camp
params DivResult(int quotient, int remainder);

DivResult r = divideInt(10, 3);
log(r.quotient);
log(r.remainder);
```

Likewise for built-in grouped forms such as optionals:

```camp
if (count.specified)
	log(count.value);
```

### 3.4.4 Method references

A method name used without `()` refers to the method itself rather than calling it. That ordinary member-binding behavior remains available even when a method is also eligible for property syntax.

```camp
delegate void(StringView) writer = Console.writeLine;
writer("hello");
```

Property syntax never removes the underlying method symbol.

### 3.4.5 No hidden dereference syntax

Camp intentionally avoids separate pointer-member syntax. This keeps member access visually uniform while leaving pointer formation and pointer types explicit elsewhere.

```camp
Counter local = init Counter(5);
Counter* p = &local;

local.increment();
p.increment();
```

## 3.5 Property Accessors

Camp provides property accessor syntax for eligible methods. This is purely syntax sugar over ordinary method binding and invocation.

A property access does not declare or require a separate runtime property concept. It does not create hidden backing storage. It does not introduce alternate ABI rules. It simply rewrites to an existing method when the naming and shape rules allow it.

### 3.5.1 Surface forms

| Surface form | Rewritten form |
|---|---|
| `obj.Text` | `obj.getText()` |
| `obj.Text = value` | `obj.setText(value)` |
| `obj.Child[index]` | `obj.getChild(index)` |
| `obj.Child[index] = value` | `obj.setChild(index, value)` |
| `obj.[index]` | `obj.get(index)` |
| `obj.[index] = value` | `obj.set(index, value)` |
| `await obj.Result` | `await obj.getResultAsync()` |

Examples:

```camp
widget.Text = "Hello";
log(widget.Text);
```

```camp
image.Pixel[x, y] = color;
auto c = image.Pixel[x, y];
```

```camp
headers.["accept"] = "application/json";
auto accept = headers.["accept"];
```

### 3.5.2 Eligibility

A method is property-eligible only when its name and signature match the accessor rules.

#### Named accessors

Named accessors begin with `get` or `set` followed by an uppercase letter:

- `getText`
- `setText`
- `getURL`
- `setChildResultAsync`

The property name is obtained by removing the `get` or `set` prefix and, for async accessors, removing the `Async` suffix.

#### Nameless indexer accessors

The exact method names `get` and `set` are also special:

- `get(...)` may be used through `obj.[...]`
- `set(..., value)` may be used through `obj.[...] = value`

There is no zero-argument `obj.[]` form.

### 3.5.3 Getter and setter shapes

`out` and `thrown` parameters do not participate in the argument list produced by property syntax.

For a getter:

- ordinary parameters determine the property or indexer argument list
- `out` parameters become part of the result set
- `thrown` parameters are handled by ordinary propagation or `catch`

For a setter:

- ordinary parameters determine the property or indexer argument list
- the final ordinary parameter is the assigned value
- any `out` parameter makes the setter ineligible
- `thrown` parameters are allowed and do not contribute to the argument list

| Accessor kind | Required ordinary shape |
|---|---|
| plain getter | `getX()` |
| indexed getter | `getX(a, b, ...)` |
| plain setter | `setX(value)` |
| indexed setter | `setX(a, b, ..., value)` |
| nameless getter | `get(a, b, ...)` |
| nameless setter | `set(a, b, ..., value)` |

Additional rules:

- a getter may be generic and/or `async`
- a setter may be generic and/or `async`
- a setter must return `void`
- a getter used via property syntax may not be an iterator
- a nameless getter still needs at least one ordinary parameter; `get(thrown E)` would imply `obj.[]`, which is not allowed

A method that fails these requirements is still a valid ordinary method. It is simply not eligible for property syntax.

### 3.5.4 Property syntax is semantically identical to method syntax

After rewriting, the compiler analyzes the expression exactly as though the explicit method form had been written.

That means property syntax has the same behavior as the method form for:

- type checking
- thrown-parameter checking
- delegate conversion
- async warnings and errors
- assignment validity
- overload-independent generic checking

Examples:

```camp
auto text = widget.Text;
auto text = widget.getText();
```

```camp
auto status = await worker.Status;
auto status = await worker.getStatusAsync();
```

If the method call would fail, the property use fails the same way. If the method call would require `await`, the property form also requires `await`.

A getter with `out` values returns them the same way the method would. A getter with `thrown` values propagates or is caught the same way the method would. If that is not the desired behavior, call the method directly.

### 3.5.5 Binding precedence

Property rewriting is a fallback, not the first lookup rule.

If an ordinary visible member named `Text` exists, that member wins over a possible rewrite to `getText()`.

If rewriting was also possible, the compiler should issue a warning.

```camp
class Widget
{
	String Text;
	String getText() => this.Text;
}

auto x = widget.Text;   // binds to the field, not the getter
```

Visibility matters. A hidden implementation detail does not block rewriting for callers that cannot see it.

### 3.5.6 Get-only and set-only properties

A getter without a setter is a read-only property.

A setter without a getter is a write-only property.

```camp
class Widget
{
	uint getChildCount() => ...;
}

auto count = widget.ChildCount;   // OK
widget.ChildCount = 0;            // ERROR
```

```camp
class Door
{
	void setLocked(bool value) => ...;
}

door.Locked = true;   // OK
auto x = door.Locked; // ERROR
```

### 3.5.7 Accessors with `thrown`

If the rewritten accessor has a `thrown` parameter, the property operation handles it exactly as the method call would.

```camp
class Config
{
	String getPath(thrown Err) => ...;
	void setPath(String value, thrown Err) => ...;
}

try
{
	config.Path = "/tmp/data";
}
catch (Err e)
{
	logError(e);
}
```

Property syntax does not get special exemption from ordinary error handling.

### 3.5.8 Async accessors

An async getter or setter works through ordinary async rewriting.

```camp
auto result = await worker.Status;
```

rewrites to:

```camp
auto result = await worker.getStatusAsync();
```

Camp does not insert `await` automatically into chained property expressions.

```camp
await widget.Child[1].Result; // ERROR if Child itself is async
```

Write the staging explicitly instead:

```camp
auto child = await widget.Child[1];
auto result = child.Result;
```

or:

```camp
auto result = (await widget.Child[1]).Result;
```

### 3.5.9 Iterator getters are not property-like

A getter returning an iterator is legal as a method, but not as a property.

```camp
iter StringView getLines() => ...;
```

```camp
fileView.Lines        // ERROR
fileView.getLines()   // OK
```

This rule preserves the ordinary expectation that property access is field-like or value-like. Iterator use carries cleanup and control-flow consequences that are better made explicit.

## 3.6 Multiple Return Values

Camp uses the ordinary grouped-value model for multi-value results. It does not introduce a separate tuple subsystem just for return values.

There are two common ways to produce multiple results:

- explicitly returning a grouped value
- omitting trailing `out` parameters at the call site

### 3.6.1 Explicit grouped returns

A function may explicitly return a grouped type.

```camp
params DivResult(int quotient, int remainder);

DivResult divideInt(int a, int b)
{
	return (quotient: a / b, remainder: a % b);
}
```

At the call site:

```camp
DivResult r = divideInt(10, 3);
log(r.quotient);
log(r.remainder);
```

A structural grouped result may also be deconstructed directly:

```camp
auto (q, r) = divideInt(10, 3);
```

### 3.6.2 Omitted trailing `out` values

If a call omits one trailing `out` parameter, the omitted value becomes the ordinary return value.

If a call omits multiple trailing `out` parameters, the omitted values become a grouped structural result.

```camp
void getBounds(out int width, out int height);

auto stats = getBounds();
auto (width, height) = getBounds();
```

This keeps `out`-heavy APIs usable without requiring a separate result-object design for every such function.

### 3.6.3 Deconstruction

Any grouped result may be deconstructed when the arity matches.

```camp
auto (min, max) = getMinMax(values);
auto (x, y) = getOrigin();
```

Deconstruction is usually the clearest way to consume unnamed structural results.

### 3.6.4 Grouped returns still follow grouped rules

Because grouped multi-value results are just grouped values, all ordinary grouped rules still apply:

- named results expose named components
- unnamed structural results do not
- structural results spread automatically in calls
- nominal grouped results spread only where the destination endorses them
- equality remains componentwise

Example:

```camp
void setRange(int start, int end);
auto bounds = getBounds();
setRange(bounds);
```

If `bounds` is a structural grouped value, it spreads positionally. If it is nominal, the ordinary endorsement rules apply.

## 3.7 Error Handling and Cleanup

Camp uses explicit error values rather than exceptions. The control-flow surface still uses familiar keywords, but the underlying model is value-based and ABI-visible.

### 3.7.1 `thrown`

A function that may fail declares a trailing `thrown` parameter:

```camp
int parsePort(StringView text, thrown ParseError)
{
	...
}
```

The Camp success convention is simple:

- the default value of the error or status type means success
- any non-default value means failure

This is why success-style enums typically assign their success member `0`.

```camp
enum ParseError
{
	OK = 0,
	E_INVALID_PORT,
	E_OUT_OF_RANGE
}
```

### 3.7.2 `throw`

`throw e;` exits the current function with the specified error value.

```camp
int parsePort(StringView text, thrown ParseError)
{
	if (text.length == 0)
		throw E_INVALID_PORT;
	...
}
```

### 3.7.3 Propagation, `try`, and `catch`

A thrown result may be handled in three ordinary ways:

- automatic propagation when the current function has a compatible thrown result
- `try` / `catch`
- an explicit `catch` argument at the call site

```camp
int loadPort(Config* config, thrown ParseError)
{
	return parsePort(config.PortText);
}
```

```camp
int loadPortOrDefault(Config* config)
{
	try
	{
		return parsePort(config.PortText);
	}
	catch (ParseError e)
	{
		return 80;
	}
}
```

```camp
parsePort(config.PortText, catch ParseError e);
if (e != default)
	return 80;
```

```camp
auto value = tryParsePort(config.PortText, catch ParseError e);
if (e != default)
	return 80;
```

A `catch` clause binds the caught error value as an ordinary local. When the error is carried by `thrown(E)` return syntax, `catch` still appears in the argument list.

### 3.7.4 `catch` arguments versus `out`

`catch` is distinct from `out` even though both write into caller-provided storage.

That distinction matters because omitted trailing `out` values become results automatically, while thrown results do not.

```camp
extern void doSomething(int option, out SomeError value);
auto a = doSomething(42); // inferred type: SomeError
```

```camp
extern void doSomething(int option, thrown SomeError);
doSomething(42, catch SomeError caught);
```

```camp
extern thrown(SomeError) tryDoSomething(int option, out int value);
auto b = tryDoSomething(42, catch SomeError caught);
```

Use `catch` when the error value should stay an error value rather than becoming part of the ordinary result set.

### 3.7.5 Error translation

`catch` is often used to translate one error type into another.

```camp
void openConfig(StringView path, thrown AppError)
{
	try
	{
		readConfigFile(path);
	}
	catch (IoError e)
	{
		throw E_CONFIG_IO;
	}
}
```

### 3.7.6 `finally`

`finally` schedules cleanup that must run on scope exit.

That includes exit caused by:

- ordinary fallthrough
- `return`
- `break`
- `continue`
- `throw`

Statement form:

```camp
finally close(file);
```

Expression/operator form:

```camp
auto request = new HttpRequest("GET", url) finally delete;
```

Both forms are useful. The statement form is general. The postfix operator form is compact and common for construction and acquisition.

### 3.7.7 `thrown` in callbacks

`thrown` is also used in callback and delegate signatures that carry a normal result together with an error code.

```camp
delegate void(int result, thrown CalcError) complete;
```

The most important uses of this pattern appear in async and callback-oriented APIs.

## 3.8 `foreach`

`foreach` provides the ordinary loop surface for iterating a sequence of values.

In this section only the surface is defined. The iterator protocol itself is described later.

### 3.8.1 Built-in array iteration

Camp has built-in support for iterating arrays with `foreach`.

```camp
int[] values = [10, 20, 30];

foreach (int value in values)
	log(value);
```

`auto` may also be used:

```camp
foreach (auto value in values)
	total += value;
```

Array iteration proceeds in index order.

### 3.8.2 Iterator-shaped sources

`foreach` also works with iterator-producing code.

```camp
foreach (auto line in fileView.getLines())
{
	log(line);
}
```

Here it is enough to know that Camp recognizes iterator-shaped sources and drives them through the `foreach` surface. The detailed iterator model, including cleanup and ABI shape, belongs later.

### 3.8.3 Loop variable binding

The loop variable may be written explicitly or inferred with `auto`:

```camp
foreach (StringView line in fileView.getLines())
	log(line);

foreach (auto line in fileView.getLines())
	log(line);
```

### 3.8.4 `break`, `continue`, and cleanup

Within a `foreach` body, `break` and `continue` behave as expected.

For arrays, no special cleanup is involved.

For iterator-shaped sources, `foreach` is responsible for the ordinary iteration cleanup behavior. The mechanics are described later; the important point here is that `foreach` is the safe, ordinary way to consume such values.

## 3.9 Slice Syntax

Camp supports index-aware and range-like syntax without introducing a hidden slice object model.

The design is deliberately ordinary:

- raw element indexing uses `[]`
- slicing is expressed through ordinary methods such as `slice(...)`
- index-aware surface conveniences come from `@index` and `@range` on those methods or property accessors

### 3.9.1 `@index`

`@index` marks a parameter as index-like.

This allows:

- index-aware diagnostics
- `^n` from-end syntax when a visible `length` or `getLength()` is available at the call site

Examples:

```camp
scoped T* addressOf<T: any>(T[] this, @index nuint index, sizeof(T));
uchar getChar(const StringView this, @index nuint unit);
```

Call-site examples:

```camp
int third = values[2];
int last = values[^1];
uchar ch = text.Char[^2];
```

`@index` does not imply clamping by itself.

### 3.9.2 `@range`

`@range` marks the first parameter of an `index, count` pair.

This enables two call styles for a slice-like method:

| Surface | Meaning |
|---|---|
| `slice(index, count)` | pass raw `index, count` directly |
| `slice(start..end)` | treat written values as boundaries; compiler computes count |

Examples:

```camp
scoped T[] slice<T: any>(const T[] this, @range nuint index = 0, nuint count = ^0, sizeof(T));
scoped StringView slice(const StringView this, @range nuint index = 0, nuint count = ^0);
```

Call-site examples:

```camp
auto middle = values.slice(2, 4);
auto inner = values.slice(2..^2);
auto prefix = text.slice(..^3);
auto suffix = text.slice(^5..);
```

### 3.9.3 Direct comma form versus boundary form

The two forms intentionally mean different things.

#### Direct comma form

```camp
someString.slice(5, 2)
someString.slice(^5, 2)
```

In direct form:

- the arguments are passed as raw `index, count`
- `^n` is allowed for the first argument because it is index-like
- `^n` is not allowed for the direct count argument
- no clamping is implied merely by the presence of `@range`

#### Boundary `..` form

```camp
someString.slice(5..7)
someString.slice(^5..^2)
someString.slice(..^2)
someString.slice(5..)
```

In boundary form:

- the written operands are boundaries, not final arguments
- the compiler resolves and clamps the boundaries first
- the compiler computes `count` from the clamped boundaries
- omitted boundaries are allowed
- a visible `length` field or `getLength()` is required at the call site

This gives Camp a compact range surface while keeping the underlying ABI shape as ordinary `index, count`.

### 3.9.4 Boundary clamping happens before count computation

This rule matters.

In `a..b` form, Camp first clamps the written boundaries and only then computes the count.

So if a boundary goes beyond the source range, the result is the clamped slice implied by the final boundaries, not a negative or nonsensical count.

### 3.9.5 Arrays use both indexing and slicing

Arrays support ordinary element indexing through `[]` and slice-like views through `slice(...)`.

```camp
int[] values = [1, 2, 3, 4, 5, 6];

int first = values[0];
int last = values[^1];
auto middle = values.slice(2..^1);
```

### 3.9.6 Strings and views use methods and property indexers

String-family types do not use raw array-style element access through `addressOf()` in the standard library. Instead they expose index-aware methods and property/indexer-friendly accessors.

```camp
uchar ch = text.Char[2];
uchar last = text.Char[^1];
auto body = text.slice(5..^2);
```

This keeps text indexing explicit about code-unit semantics while still providing a concise surface.

### 3.9.7 Property indexers and slicing compose naturally

Because indexing and property access are ordinary method-based surfaces, they compose with the accessor rewrite rules rather than introducing a second indexing subsystem.

```camp
auto accept = headers.["accept"];
headers.["accept"] = "application/json";
```

```camp
auto pixel = image.Pixel[x, y];
image.Pixel[x, y] = color;
```

The slice surface is designed with the same philosophy: ordinary methods first, convenient syntax second.
# 4. Lifetime

This section defines the rules that constrain pointer validity, stored references, allocator context, and explicit lifetime operations.

Section 2 introduced the ordinary lifecycle surface of user-defined types: constructors, destructors, `init`, `new`, and `delete`. This section does not restate that surface. Instead, it defines the validity rules around it:

- when a value is known to be non-stack
- when one value is known to outlive another
- how pointer-bearing aggregates participate in lifetime checking
- how allocator context is selected and forwarded
- how `new`, `init`, and `delete` interact with `within`

Camp keeps these rules intentionally small. The goal is to prevent common lifetime bugs without introducing a hidden ownership runtime.

## 4.1 Lifetime Annotations

Camp uses a hybrid lifetime model.

- `escaped` is a storage/context fact
- parameters and return parameters default to `scoped`
- `unscoped(...)` removes that `scoped` restriction for the stated relations
- return values use `scoped`, `scoped(...)`, and `escaped` to describe caller-visible lifetime facts

The important question is not "into what type of storage does this pointer point?" The important question is "what values are known to outlive what other values?"

### 4.1.1 Explanatory context vocabulary

The following terms are used in explanatory text and diagnostics:

- **caller-context** describes values that must remain valid after the current call returns
- **function-member** describes values valid for the full execution of the current function or method body, such as parameters and the receiver
- **declaration-scope** describes ordinary locals whose validity is limited to their declaring lexical scope

### 4.1.2 Where lifetime annotations may appear

Lifetime annotations may appear on:

- pointer types
- aggregate or context object types, where they describe the lifetime of the contained pointers
- explicit `this` parameters
- class declarations
- interface declarations

For a delegate-like callable value, lifetime annotations describe the lifetime of the hidden context pointer.

Lifetime annotations are not written on individual fields. Aggregate and context lifetimes are expressed on the containing value instead.

### 4.1.3 `escaped`

`escaped` means the annotated value is not in stack storage.

This is a storage/context fact, not a pairwise relation.

It does not imply process-global lifetime, thread safety, or cross-thread mobility. A value allocated from a custom allocator may still be allocator-bound or arena-bound. `escaped` means only that the value is not in stack storage.

Examples:

```camp
escaped Widget* p
escaped StringView text
escaped delegate void() callback
escaped class Widget
escaped interface IWidget
```

When `escaped` is written on an aggregate or context value, it describes the contained pointers.

### 4.1.4 `scoped`

`scoped` is the default lifetime for parameters, including the receiver, and for pointers contained in aggregate or context parameters.

Within a method or function body, a `scoped` value may not be assigned to any storage location known to outlive the call. That includes caller-context storage and escaped storage. Since `this` is caller-context, this also forbids storing the value into a field of `this`.

A `scoped` value may also not be passed to another routine when that call could place it into longer-lived storage.

From the caller's point of view, any argument lifetime may be supplied to a `scoped` parameter as long as the ordinary type requirements are satisfied.

When an anchor list is written explicitly on an aggregate or context value, `scoped(anchor1, anchor2, ...)` means the contained pointers will not outlive the listed anchors.

### 4.1.5 `unscoped(...)`

`unscoped(...)` removes the ordinary `scoped` restriction.

Within a method or function body, an `unscoped` parameter may be assigned to caller-context storage, including fields of `this`, and may be passed to another routine that may do the same. `unscoped` does not by itself imply escaped storage.

`unscoped(anchor1, anchor2, ...)` removes the `scoped` restriction only when determining lifetime relationships involving the listed anchors.

From the caller's point of view:

- a bare `unscoped` parameter must be known to survive all caller-context pointer arguments of the call, including `this` for instance methods
- an `unscoped(anchor1, anchor2, ...)` parameter must be known to survive the listed anchors

Example:

```camp
void add(unscoped(this) Item* item)
```

`item` may be kept by `this`, but it is not thereby treated as escaped.

### 4.1.6 Bare `scoped` and bare `unscoped`

When no identifier list is provided, the relevant anchor set is implied.

#### Instance methods

For ordinary parameters of instance methods, bare `scoped` simply restates the default. Bare `unscoped` means the argument must be known to survive the caller-context pointer arguments of the call, including `this`.

Constructors and destructors follow the same rule. For a constructor, the relevant lifetime of `this` is the lifetime of the value being initialized. For `new`, that result is `escaped`. For `init` or aggregate initialization of a local value, that result is the inferred lifetime of the initialized local value.

#### Static methods and free functions

For functions without a receiver, bare `scoped` is the default. Bare `unscoped` means the argument must be known to survive all caller-context pointer arguments of the call.

#### Local values

For a local aggregate or context value, bare `scoped` means the value may not outlive its declaration-scope.

This is the ordinary form used for local delegates and similar local context objects.

### 4.1.7 Bare `scoped` on return values

For return values, bare `scoped` means the result points to caller-context. From the caller's point of view, the result may be as narrow as the narrowest argument lifetime and as wide as the widest argument lifetime, including `this` for instance methods.

Example:

```camp
scoped Player* choose(Roster this, Player* a, Player* b)
{
	return setting ? a : b;
}
```

The returned value is caller-context and may range between the lifetimes of `a` and `b`.

### 4.1.8 Return annotations

An unannotated return value is not a separate lifetime kind. By default, the result is assumed to be either caller-context or escaped, with a minimum lifetime equal to the narrowest lifetime of all arguments, including `this`.

A return value may also be annotated explicitly:

- `scoped` means the result points to caller-context
- `scoped(anchor)` means the result has the lifetime of `anchor`
- `escaped` means the result is escaped

`unscoped` is not used on return types.

Construction expressions use analogous result-lifetime checking even though constructors do not declare ordinary return values. The construction-specific rules are defined with `init` and `new`.

### 4.1.9 Aggregate and container rule

Pointer-bearing contents are treated as `unscoped` relative to their containing value by default.

This applies to:

- struct fields
- class fields
- `params` components
- materialized `struct(T)` storage
- array elements when the element type contains pointers
- delegate context storage
- compiler-generated iterator frames
- compiler-generated async frames
- compiler-generated postponed-operation contexts

This rule is recursive through materialized storage.

A value therefore does not need to itself be a pointer in order to participate in lifetime checking. A stack value containing pointers is checked through the pointers it contains.

This is what makes containers of `StringView`, `Span<T>`, delegate values, and other pointer-bearing values meaningful under the lifetime system.

A pointer-bearing aggregate or context value has one lifetime for its contained pointers. Individual fields do not acquire separate lifetime annotations, even when those fields are nested inside other fields.

Assignment into a field or nested field is therefore checked as assignment into the containing value. A value may not be assigned into a field if that value is too narrow for the containing aggregate's current lifetime.

This rule applies equally to structs, classes, `params` values, materialized `struct(T)` storage, arrays whose elements contain pointers, optionals whose payload contains pointers, delegates, and compiler-generated context objects.

## 4.2 Return Defaults and Declaration-Site Defaults

### 4.2.1 Ordinary defaults

Ordinary parameter and return-parameter pointer positions default to `scoped` unless some other rule applies.

This affects:

- parameters
- the implied receiver
- `out` parameters and other caller-provided result storage
- aggregate and context parameters through their contained pointers

Unannotated return values follow the ordinary return rule from section 4.1.8.

The main exception is declaration-site `escaped` defaults for specific nominal types.

### 4.2.2 Specific result relations remain explicit

An unannotated return already has a minimum relation to the full argument set. When a routine returns a pointer or pointer-bearing value tied to a specific input, the return type should say so explicitly.

```camp
scoped(values) int* elementAt(int[] values, nuint index)
{
	return values.addressOf(index);
}
```

Likewise for aggregate results:

```camp
struct Slice<T>
{
	T* items;
	nuint length;
}

scoped(source) Slice<T> firstHalf<T>(T[] source)
{
	return {
		.items = source.addressOf(0),
		.length = source.length / 2
	};
}
```

The type makes the returned relation visible through the value itself rather than through field-local annotations.

### 4.2.3 `escaped class`

An `escaped class` is a class whose instance methods default to `escaped this`, and whose pointers default to `escaped` unless explicitly narrowed.

Example:

```camp
escaped class Widget
{
	Widget* parent;
	Widget* firstChild;

	void appendChild(Widget* child)
	{
		this.firstChild = child;
		child.parent = this;
	}
}
```

Effects:

- the implied receiver of instance members, constructors, and destructors is `escaped this` by default
- unannotated pointers of type `Widget*` default to `escaped Widget*`

This does not mean the compiler forbids raw allocation, copying, or movement of such values. The rule is enforced when an operation actually requires an escaped receiver or escaped pointer.

That means:

- constructing an `escaped class` in stack storage using a constructor call fails at the constructor call, because the constructor has `escaped this`
- copying a value from one storage location to another is not itself policed by a dedicated lifetime rule
- a method call fails when the receiver does not satisfy the receiver contract at the point of use

An `escaped class` is therefore not defined as "must use `new`." It means methods and pointers of that type assume a non-stack instance by default.

### 4.2.4 `escaped interface`

An `escaped interface` behaves analogously:

- interface pointers default to `escaped`
- interface methods default to `escaped this`

This is useful for interfaces intended only for long-lived object identities.

## 4.3 Allocators and `within`

Camp uses explicit allocators rather than a hidden global allocator policy.

The language supports two common styles:

- **stored allocator style**: the object stores the allocator it uses internally
- **threaded allocator style**: the allocator is passed through the call graph

The `within` mechanism supports both.

### 4.3.1 The allocator interface

Allocators are ordinary classes. The minimal virtual surface is untyped.

```camp
export abstract class Allocator
{
	abstract void* allocUntyped(nuint size, thrown MemoryError);
	abstract void* reallocUntyped(void* ptr, nuint newSize, thrown MemoryError);
	abstract void free(escaped void* ptr);
}
```

Generic helpers are layered on top:

```camp
export T* alloc<T: any>(Allocator* this, nuint len = 1, sizeof(T), thrown MemoryError);
export T* realloc<T: any>(Allocator* this, T* ptr, nuint newLen, sizeof(T), thrown MemoryError);
```

The generic surface uses element counts, not byte counts.

### 4.3.2 Generic allocator semantics

At the generic layer:

- `alloc<T>(n)` allocates space for `n` elements of `T`
- in erased-generic code, this means the materialized storage form of `T`
- alignment is the allocator’s responsibility
- `free` accepts any pointer previously returned by the allocator

This keeps generic allocation explicit without inventing a separate allocator sublanguage.

### 4.3.3 The default allocator

The standard library exposes a process-wide default allocator:

```camp
export const Allocator* defaultAllocator;
```

If no allocator is supplied for an allocation path, `Std::defaultAllocator` is used.

### 4.3.4 Lexical allocator context

`within` establishes the current allocator context lexically.

```camp
within(arena)
{
	auto node = new Node(10);
}
```

Inside that block, allocator-backed operations use `arena` unless something more specific overrides it.

Nested `within` blocks are allowed. The innermost one wins.

```camp
within(a)
{
	within(b)
	{
		new T(...);   // uses b
	}
}
```

### 4.3.5 Expression-form override

A specific operation may override the surrounding context.

```camp
within(a)
{
	auto r = within(b) new Rect(10, 20);
}
```

This changes only that operation.

### 4.3.6 What `within` affects

`within` affects allocator-backed behavior.

In ordinary source code, that means primarily:

- `new`
- pointer-form `delete`
- constructor or destructor logic that accepts a `within` parameter
- interface-based construction and destruction paths that thread allocators

A plain `init` does not allocate outer instance storage. However, if the constructor selected by `init` takes `within`, the current allocator context is still forwarded into that constructor.

### 4.3.7 `within` parameters

A function, constructor, or destructor may declare a `within` parameter.

```camp
class StringBuilder
{
	Allocator* arena;
	char* buffer;

	StringBuilder(within arena)
	{
		this.arena = arena ?? Std::defaultAllocator;
	}
}
```

A `within` parameter is shorthand for:

```camp
within unscoped Allocator* arena = null
```

Defaults:

| Property | Meaning |
|---|---|
| Type | `Allocator*` |
| Lifetime | `unscoped` |
| Default value | `null` |

Only one `within` parameter is allowed per routine.

### 4.3.8 Implicit forwarding

If a routine declares a `within` parameter and the caller does not supply it explicitly, the compiler supplies one automatically.

Rules:

1. if the caller is inside `within(...)`, that allocator is passed
2. otherwise, `null` is passed

Example:

```camp
class BufferOwner
{
	byte* data;
	Allocator* arena;

	BufferOwner(nuint length, within arena)
	{
		this.arena = arena ?? Std::defaultAllocator;
		this.data = this.arena.alloc<byte>(length);
	}
}

within(tempArena)
{
	auto x = new BufferOwner(1024);
}
```

The constructor receives `tempArena` without the call having to mention it explicitly.

### 4.3.9 Inside the routine

Inside a routine that declares `within`, that parameter itself establishes the current allocator context.

```camp
void add(Item x, within arena)
{
	auto node = new Node(x);
}
```

Here `new Node(x)` uses `arena ?? Std::defaultAllocator` unless overridden again with a nested `within(...)`.

### 4.3.10 Stored allocator style

A type may accept a `within` parameter and store the allocator for later internal allocations.

```camp
class StringBuilder
{
	Allocator* arena;
	char* buffer;

	StringBuilder(within arena)
	{
		this.arena = arena ?? Std::defaultAllocator;
	}
}
```

This style is useful when many related allocations should remain tied to the object.

### 4.3.11 Threaded allocator style

A routine may instead accept `within` and simply use it during the call.

```camp
void push(Value v, within arena)
{
	auto node = new Node(v);
}
```

This style keeps the object model simpler and leaves allocator choice to the call chain.

### 4.3.12 `within` and async frames

If an async routine declares `within`, that allocator may also be used to allocate the async frame.

The detailed async model is defined later. The important point here is that allocator context applies not only to explicit `new`, but also to compiler-generated storage when the feature requires it.

## 4.4 `new`, `init`, and `delete`

Section 2 already defined what these operators mean at the type-lifecycle level. This section defines their allocator and lifetime behavior.

### 4.4.1 `init`

`init` constructs in storage that already exists.

That means:

- it does not allocate outer instance storage
- it therefore does not itself choose an allocator for that outer storage
- it may still forward allocator context into the selected constructor

```camp
within(tempArena)
{
	HttpRequest request = init HttpRequest("GET", url);
}
```

Here `init` does not allocate `request` itself. But if `HttpRequest(...)` declares `within`, `tempArena` is forwarded into that constructor.

The same rule applies to stack arrays:

```camp
auto rects = init Rect[50];
```

This creates non-heap storage. It does not allocate via an allocator and does not implicitly construct each element.

For pointer-bearing local values, `init` participates in the constructor result lifetime rule described below.

### 4.4.2 `new`

`new` allocates storage and then constructs.

The allocator used for the outer allocation is chosen in this order:

1. an explicit `within(...)` attached to the `new` expression
2. the current surrounding `within(...)` context
3. `Std::defaultAllocator`

```camp
auto a = new Node(1);                  // default allocator, unless inside within(...)
auto b = within(arena) new Node(2);    // explicit override
```

If the selected constructor declares `within`, that same allocator is also forwarded into the constructor unless the call supplies a different allocator explicitly.

For result lifetime checking, `new` participates in the constructor result lifetime rule described below.

### 4.4.3 Constructor result lifetimes

Although a constructor does not return an ordinary source-level value, a construction expression produces a value whose lifetime is checked like a result.

A `new` expression always produces an `escaped` result. This is true even when the allocation comes from an explicit allocator or arena. `escaped` means the result is not stack storage; it does not imply process-global lifetime or allocator-independent ownership.

An `init` expression constructs a value in storage that already exists. When that storage is a local value and the initialized type is pointer-bearing, the local value receives a fixed lifetime when it is first initialized.

The fixed lifetime of a pointer-bearing local aggregate or context value is the narrowest lifetime among the pointer-bearing values retained by the initialized value. Values are retained by the initialized value when they are assigned into `this` by the constructor, assigned by aggregate initializer syntax, or assigned by trailing initializer syntax attached to the construction expression.

Scoped constructor parameters that are only used during construction and are not retained by the initialized value do not contribute to the result lifetime.

If no constructor is called and no pointer-bearing field is assigned during initialization, the local value's lifetime is its declaration-scope.

Once a local value's lifetime has been fixed, later assignments to its fields do not change that lifetime. Later assignments are checked against the existing lifetime of the containing value.

```camp
struct Matrix3x2
{
	Matrix3x2(unscoped double[] first, unscoped double[] second)
	{
		this.first = first;
		this.second = second;
	}

	double[] first;
	double[] second;
}

scoped Matrix3x2 makeMatrix(double[] arg1, double[] arg2)
{
	double[] outer = [1.0, 2.0, 3.0];

	Matrix3x2 outerLocal = default;
	Matrix3x2 callerLocal = { arg1, arg2 };

	if (testSomething())
	{
		double[] inner = [4.0, 5.0, 6.0];

		Matrix3x2 innerMatrix = { outer, inner };
		outerLocal = innerMatrix;      // ERROR: `innerMatrix` is limited to the inner scope

		Matrix3x2 outerMatrix = { outer, outer };
		outerLocal = outerMatrix;      // OK

		callerLocal.first = inner;     // ERROR: `inner` is too narrow for `callerLocal`
		callerLocal.first = outer;     // ERROR: `outer` is too narrow for `callerLocal`
	}

	return outerLocal;                 // ERROR: `outerLocal` is limited to its declaration-scope

	outerLocal.first = arg1;
	outerLocal.second = arg2;

	return outerLocal;                 // ERROR: later assignments do not change the fixed lifetime

	return callerLocal;                // OK
	return { arg1, arg2 };             // OK
}
```

Constructor parameters contribute according to whether they are retained by the initialized value and what lifetime the constructor requires from the caller.

```camp
struct Test
{
	Test(TestOptions* options, unscoped TestQuestion[] questions, escaped QuestionChecker* checker)
	{
		checker.initializeFromOptions(options);
		this.questions = questions;
		this.checker = checker;
	}

	TestQuestion[] questions;
	QuestionChecker* checker;
}

QuestionChecker* globalQuestionChecker = new QuestionChecker();

scoped Test beginTest(TestQuestion[] questions)
{
	Test test = init Test(
		&{ .allowBlanks = true },
		questions,
		globalQuestionChecker);

	return test;                       // OK: `test` is caller-context
}
```

In this example, `options` is `scoped` and is only used during construction, so it does not contribute to the lifetime of `test`. `questions` is `unscoped` because it is retained by `test`, so the lifetime of `test` is limited by `questions`. `checker` is `escaped`, so retaining it does not narrow the result.

### 4.4.4 `new` and raw allocation helpers

For raw storage with no constructor call, direct allocator helpers may be clearer than `new`.

```camp
auto p = arena.alloc<Point>();
```

This is especially useful for struct types with no constructor arguments, since Camp does not invent hidden parameterless struct constructors.

### 4.4.5 `delete` on values

Value-form `delete` runs destructor logic only.

```camp
Utf8Buffer buffer = init Utf8Buffer(256);
delete buffer;
```

This does not deallocate the storage of `buffer` itself. It only performs teardown of the value.

If the destructor declares `within`, the current allocator context is forwarded into that destructor exactly as with any other `within` parameter.

### 4.4.6 `delete` on pointers

Pointer-form `delete` performs:

1. destruction of the pointed-to instance
2. deallocation of the pointed-to storage

```camp
Window* dialog = new Window(800, 600);
delete dialog;
```

The allocator used for deallocation is chosen by the same `within` rules:

1. explicit `within(...)` attached to the delete expression
2. current surrounding `within(...)` context
3. `Std::defaultAllocator`

```camp
Window* dialog = within(arena) new Window(800, 600);
within(arena) delete dialog;
```

If the destructor itself declares `within`, that allocator is also forwarded into the destructor call.

### 4.4.7 Pointer-form `delete` requires an escaped pointer

A pointer operand for deallocating pointer-form `delete` must be `escaped`.

```camp
Window local = init Window(800, 600);
Window* p = &local;

delete p;   // ERROR
```

The pointer does not denote escaped storage, so pointer-form `delete` is invalid even if the pointed-to type has a destructor.

By contrast, deleting the value itself is valid:

```camp
delete local;   // destructor only
```

### 4.4.8 `finally delete`

`finally delete` is often the most concise way to pair construction with cleanup.

```camp
auto request = new HttpRequest("GET", url) finally delete;
auto buffer = init Utf8Buffer(256) finally delete;
```

This preserves the ordinary distinction:

- value-form cleanup destroys only the value
- pointer-form cleanup destroys and deallocates the pointed-to storage

### 4.4.9 A common `within` pitfall

The following is a compile error:

```camp
within(arena) finally delete dialog;
```

This would otherwise mean:

```camp
within(arena)
{
	finally delete dialog;
}
```

which schedules the deletion at the end of that implicit block — immediately.

Camp rejects this form because it is almost always an accidental bug.

### 4.4.10 Interaction summary

| Operation | Outer storage allocated? | Uses allocator context for outer storage? | May forward allocator into ctor/dtor? |
|---|---:|---:|---:|
| `init T(...)` | no | no | yes |
| `new T(...)` | yes | yes | yes |
| `delete value` | no | no | yes |
| `delete pointer` | yes, for deallocation | yes | yes |

This table captures the main distinction for this section:

- `init` and value-form `delete` do not manage outer storage
- `new` and pointer-form `delete` do
- allocator context may still be relevant to any of them if constructor or destructor logic accepts `within`

# 5. Advanced Features

This section defines the larger control-flow and modularity features that build on Camp’s core type, statement, and lifetime rules. The unifying design choice is the same throughout: Camp favors compact source-level syntax, but the underlying model remains explicit, ABI-visible, and interoperable with ordinary C-style code.

## 5.1 Modules, Export, and Namespaces

Camp keeps its module surface deliberately small. Each Camp source file is a self-contained module.

- symbols are private by default
- exported declarations form the public ABI surface
- namespaces are an import-site naming aid, not a runtime feature
- the visible organization of Camp code is compiler-driven rather than header-driven

The goal is to make visibility explicit in source and keep generated public and private surfaces predictable.

### 5.1.1 Default visibility

A declaration is internal to its defining source file unless it is marked `export`.

```camp
struct Point
{
	int x;
	int y;
}

bool isOrigin(in Point this)
{
	return this.x == 0 && this.y == 0;
}
```

None of the declarations above are public outside the defining module.

Public declarations are written explicitly:

```camp
export struct Point
{
	int x;
	int y;
}

export bool isOrigin(in Point this)
{
	return this.x == 0 && this.y == 0;
}
```

This rule applies uniformly to:

- types
- functions
- methods
- delegates
- enums
- `newtype`s
- other exported callable or data surfaces

### 5.1.2 `export` is about ABI surface, not only name lookup

An exported declaration is part of the module’s public ABI story.

That means `export` affects more than ordinary visibility. It also affects what must appear in generated public headers and what outside code is allowed to name or call.

For example, an exported struct remains layout-visible in the public header, while an exported class remains opaque. Those type-specific consequences were defined earlier. The important point here is that `export` is the switch that places a declaration on the public boundary at all.

An exported declaration may also name a metadata output:

```camp
export("api") void process(StringView value);
```

The declaration is still exported normally. In addition, its definition is emitted to the named metadata file. That metadata can be transformed into a C header or another external declaration surface as a separate compilation step.

### 5.1.3 Exporting members of otherwise non-exported types

A member may be exported even when its enclosing type is not exported.

In that case, the type becomes opaque where that is meaningful across the public ABI.

```camp
class Counter
{
	int value;
}

export void increment(Counter* this)
{
	this.value++;
}
```

Outside the defining module, the exported callable surface may still exist, but the full type layout does not automatically become public just because one member was exported.

### 5.1.4 Namespace declaration

A file or module may declare the namespace it exports under using `export as`:

```camp
export as Std;
```

Namespaces in Camp are a source-level naming and import convenience. They are erased during compilation. Camp does not treat them as runtime objects or as a second abstraction layer with their own execution semantics.

### 5.1.5 Import-site namespace use

The ordinary import-site surface is `using`:

```camp
using Std;

printLine("hello");
```

A `using` declaration imports all exported symbols from the namespace into the current file's scope.

A namespace may also be imported using an alias:

```camp
using Std as S;

S::Console.writeLine("hello");
```

When an alias is declared, the alias must be used instead of the original namespace name in the current file.

A `using` declaration may import only selected symbols:

```camp
using Std { List, Map };

List<int> values = new List<int>();
Std::Console.writeLine("hello");
```

Symbols not listed remain accessible by qualified name.

When a selected symbol names a type, the type name and method symbols with that type-name prefix are imported. For example:

```camp
using MyModule { Order, Invoice };
```

imports `Order`, `Invoice`, `Order_*`, and `Invoice_*`. Other symbols exported from `MyModule` still require namespace qualification.

A method that requires namespace qualification may not be invoked using receiver syntax. It must be called by its qualified symbol name:

```camp
MyModule::CustomerArray_processOrders(customers);
```

Array receiver prefixes are imported using their generated receiver-name form:

```camp
using MyModule { CustomerArray, Array };
```

Here `CustomerArray` imports methods with a `Customer[]` receiver, and `Array` imports methods with a generic `T[]` receiver.

A typical pattern is:

```camp
export as Std;

export void printLine(StringView text)
{
	Console.writeLine(text);
}
```

and then, elsewhere:

```camp
using Std;

printLine("hello");
```

This keeps naming light in ordinary code without requiring a runtime namespace mechanism.

### 5.1.6 Foreign definitions

`extern` declares a symbol whose implementation is supplied outside Camp and linked with the current library.

```camp
extern void nativeInit();
```

`extern("library")` declares a symbol supplied by a dynamic library with the given name.

```camp
extern("kernel32") nuint RtlMoveMemory(const void* src, void* dst, nuint len);
```

An `extern` function, method, or variable is implemented outside Camp. An `extern` `class`, `struct`, `params`, or `newtype` declaration describes a type whose member implementations are supplied outside Camp.

### 5.1.7 Public versus private generated views

Camp’s visibility rules are reflected in two generated surfaces:

- a **public header** for exported declarations
- a **private header** for full module-internal details

This distinction was introduced earlier for data structures. At the module level, the key point is simpler:

- `export` decides what belongs in the public view
- everything else remains private to the module

This gives Camp a direct source-level replacement for the traditional C pattern of manually splitting declarations across headers and implementation files.

### 5.1.8 Foreign import direction in v1

Camp keeps foreign-import convenience intentionally modest in v1. The current direction is that parsing C headers and generating Camp declarations is primarily a tooling concern rather than a large built-in language subsystem.

So the language focuses on making exported and imported ABI shapes explicit and regular, while import convenience can be layered on by compiler tools.

---

## 5.2 Iterators

Camp iterators are explicit state machines with a small protocol surface. The language provides `iter`, `yield`, and `foreach` because they are convenient and readable, but the underlying model remains ordinary storage plus ordinary functions.

### 5.2.1 Overview

A Camp iterator has three core parts:

- a state container
- a `next(...)` function that advances that state
- deterministic cleanup

This model is used both for pure Camp code and for ABI-facing reasoning. `foreach` is syntax sugar over that model; it is not a separate hidden runtime mechanism.

### 5.2.2 Iterator values and generator declarations

`iter T` is the ordinary iterator callable type. A function may return an iterator value without being a generator:

```camp
iter int getValues(int first, int last)
{
	return createRangeIterator(first, last);
}
```

That function does not use `yield`. It runs normally when called and returns an iterator value.

A function body may use `yield` only when the declaration chooses a generated iterator state container:

```camp
struct iter int range(int first, int last)
{
	for (int i = first; i <= last; i++)
		yield i;
}
```

```camp
class iter int powersOfTwo(int count)
{
	int value = 1;

	for (int i = 0; i < count; i++)
	{
		yield value;
		value *= 2;
	}
}
```

The prefix selects the generated state container:

| Form | Generated state container | Typical use |
|---|---|---|
| `struct iter T` | generated `fixed struct` | zero-allocation local iteration |
| `class iter T` | generated class | stored, returned, or exported iteration |

A `struct iter` generator produces a fixed struct state type. A `class iter` generator produces a class state type. Both generated types are real Camp-visible types.

### 5.2.3 Failing iterators

Iterator failure is part of the iterator type, not the generator parameter list:

```camp
struct iter(int, thrown RangeError) range(int first, int last)
{
	if (last < first)
		throw E_INVALID_RANGE;

	for (int i = first; i <= last; i++)
		yield i;
}
```

The yielded type appears first. The `thrown` type appears inside the `iter(...)` type form.

A generator parameter list may not contain a trailing `thrown` parameter. Calling a generator only creates the initial iterator state; the generator body starts when `next(...)` is called.

An ordinary function returning an iterator may still fail while preparing the iterator:

```camp
export iter char chars(StringView text, within allocator, thrown MemoryError)
{
	return charsOwned(text.toStringCopy(within allocator));
}

class iter char charsOwned(escaped String text)
{
	finally delete text;

	for (nuint i = 0; i < text.length; i++)
		yield text.units[i];
}
```

Here the exported function may copy the input and report allocation failure before returning. The generator itself stores only an escaped value.

### 5.2.4 Generator parameters

Calling a generator stores its arguments in the generated iterator state. The generator body does not run during that initial call.

For that reason, generator parameters are retained state parameters. A generator parameter list may not contain:

- `in` parameters
- `out` parameters
- trailing `thrown` parameters

Default arguments are allowed. An instance generator's receiver is treated as a retained parameter under the same rules.

Pointer-bearing parameters, including aggregate and context parameters, must be valid for the generated state container.

For `struct iter`, retained pointer-bearing parameters constrain the lifetime of the generated fixed-struct state. The iterator state may not outlive the values it stores.

For `class iter`, retained pointer-bearing parameters must be `escaped`, because the generated class state is escaped. This requirement includes `this`. Therefore, a `class iter` member function must either be a member of an `escaped class` or have an explicit `escaped this` parameter.

### 5.2.5 Generated type names

The generated iterator state type is named by appending `Iter` to the generator name.

```camp
struct iter int range(int first, int last)
{
	for (int i = first; i <= last; i++)
		yield i;
}
```

This produces a generated fixed struct named `rangeIter`.

For an instance generator, the generated type is nested in the containing type. Its C-facing name is prefixed with the containing type name.

```camp
class MainSystem
{
	struct iter int range(int first, int last)
	{
		for (int i = first; i <= last; i++)
			yield i;
	}
}
```

The generated type is `MainSystem.rangeIter` in Camp and is represented with a name such as `MainSystem_rangeIter` in C-facing output.

### 5.2.6 High-level lowering model

A `struct iter` generator lowers to a generated fixed struct plus an initializer-like function that fills caller-provided state:

```c
typedef struct _rangeIter {
	int32_t first;
	int32_t last;
	int32_t state_i;
} rangeIter;

void   range(int32_t first, int32_t last, rangeIter *state);
bool_t rangeIter_next(rangeIter *state, int32_t *current);
void   rangeIter_destroy(rangeIter *state);
```

A `class iter` generator lowers to an opaque generated state type and creation/destruction helpers:

```c
typedef struct powersOfTwoIter powersOfTwoIter;

powersOfTwoIter *powersOfTwo(int32_t count);
bool_t           powersOfTwoIter_next(powersOfTwoIter *state, int32_t *current);
void             powersOfTwoIter_destroy(powersOfTwoIter *state);
```

A plain function returning `iter T` lowers as an ordinary function returning an iterator callable value. It does not produce a generated state type merely because its return type is `iter T`.

### 5.2.7 The `next(...)` protocol

For a generated iterator type `Y` yielding `T`, the basic protocol is:

```camp
bool next(Y* this, T* current);
```

Where:

- `this` points at the iterator state
- `current` points at caller-provided storage for the next yielded value
- the boolean result means “a value was produced”

If the iterator may fail, the error output appears after `current` as a `thrown` parameter:

```camp
bool next(Y* this, T* current, thrown E);
```

As elsewhere in Camp, the default value of the error type means success.

### 5.2.8 Yielding `params` types

If the yielded type is a `params` type, the `current` storage follows the ordinary shared grouped-value rules.

That means `T* current` expands componentwise after substitution.

Conceptually, if `T` expands into `F1, F2, ...`, then:

```camp
bool next(Y* this, F1* current1, F2* current2, ...);
```

This is not an iterator-specific exception. It is the same grouped-value rule used elsewhere in the language.

### 5.2.9 Cleanup

Iterators are cleaned up explicitly and deterministically.

For ordinary non-`params` yields, cleanup of an abstract iterator value is requested by calling `next(...)` with `null` current storage:

```camp
seq(null);
```

When the concrete generated iterator type is known, ordinary lifecycle syntax may also be used:

```camp
rangeIter state = range(1, 5) finally delete;
```

A `finally` statement inside a generator body participates in that generated iterator cleanup path. It runs when iteration finishes naturally, when the iterator is cleaned up early, or when the concrete generated state is deleted.

If the yielded type is params-based and `current` has exploded into multiple pointer parameters, the first exploded `current` component is the cleanup signal. In practice, compiler-generated cleanup passes the full exploded default group.

### 5.2.10 `foreach` lowering

Section 3 introduced `foreach` as surface syntax. The iterator-specific point is that `foreach` is only a driver for the ordinary protocol.

For a `struct iter` result, the conceptual lowering uses caller-owned state:

```camp
{
	rangeIter state = range(1, 5);
	int current;

	try
	{
		while (state.next(&current))
		{
			...
		}
	}
	finally
	{
		delete state;
	}
}
```

For a `class iter` result, the conceptual lowering uses the generated creation and destruction path.

### 5.2.11 Manual iteration

The protocol is also usable directly:

```camp
struct iter int range(int start, int count)
{
	for (int i = 0; i < count; i++)
		yield start + i;
}

void sample()
{
	rangeIter seq = range(5, 3) finally delete;
	int value = 0;

	while (seq.next(&value))
	{
		log(value);
	}
}
```

The exact helper spelling may vary by context, but the model does not: iteration is manual `next(...)` plus explicit cleanup.

### 5.2.12 Iterator values as ordinary callables

When used as expressions, iterators may be viewed through an ordinary callable surface whose call target ultimately drives `next(...)` and whose context refers to the iterator state.

That matters for two reasons:

- iterators compose cleanly with the rest of Camp's callable model
- no separate hidden runtime iterator-object protocol is needed

A manually written function that matches the lowered iterator shape does not automatically participate in iterator semantics. An explicit `(iter)` cast blesses it when that behavior is intended.

A function type returning an iterator is written with `fn` or `delegate` before the iterator return type:

```camp
fn iter(int, thrown RangeError)(int first, int last) getRangeFunc = getRange;
```

Calling that function produces an iterator value:

```camp
iter(int, thrown RangeError) values = getRangeFunc(1, 5);
```

### 5.2.13 Generic and erased-generic iterators

Iterator-specific generic rules follow the ordinary generic rules.

For `T: any`, `T* current` means pointer to the materialized storage form of `T`.

So in erased-generic iterator code:

- the logical yielded type is `T`
- the storage contract for `current` is `struct(T)*`

That is exactly the kind of context where an explicit pointer-to-storage contract is appropriate.

### 5.2.14 Design summary

The important practical rules are:

- `iter T` is the ordinary iterator callable type
- `struct iter T` and `class iter T` declare generators and enable `yield`
- generator arguments are stored in the generated iterator state
- a generator parameter list may not contain `in`, `out`, or trailing `thrown` parameters
- a `struct iter` generator produces a generated fixed struct state type
- a `class iter` generator produces a generated class state type
- `yield` writes the next logical value through caller-provided storage
- `next(...)` is the protocol beneath both manual iteration and `foreach`
- cleanup is explicit and deterministic
- grouped yields and failing iterators follow the ordinary shared `params` and `thrown` rules

## 5.3 Async Functions

Camp’s async model is first-class at the source level and ordinary at the ABI level. An async function is still just a function whose final parameter has a completion-callback shape the compiler knows how to work with.

### 5.3.1 Overview

Camp async uses a few ordinary features together:

- `async`
- `await`
- `once`
- `postpone`
- ordinary lifetime analysis

The design goal is to provide convenient source syntax while lowering to explicit continuation-based functions and explicit async frames.

### 5.3.2 Where `async` may appear

`async` is a callable specifier.

It may be used on:

- functions
- methods
- callable types
- iterators
- property accessors
- interface methods

If `export` is also present, `async` appears after `export` and before the rest of the signature.

### 5.3.3 Signature rewrite

At the source level, an async function appears to return a value in the ordinary way:

```camp
async int addAsync(int a, int b)
{
	return a + b;
}
```

At the ABI level, `async` rewrites the signature to `void` and adds a final completion parameter.

| Source form | Rewritten final parameter |
|---|---|
| `async void f(...)` | `once void() complete` |
| `async TResult f(...)` | `once void(TResult result) complete` |
| `async void f(..., thrown E)` | `once void(thrown E) complete` |
| `async TResult f(..., thrown E)` | `once void(TResult result, thrown E) complete` |

An async function may not have `out` parameters.

### 5.3.4 Async state machines

An async function produces a state machine. Lifted locals are stored in an async frame rather than on the ordinary stack.

The source-level `return` and `throw` statements rewrite to calls to the completion callback.

Conceptually:

- `return value;` becomes `complete(value, default); return;` when the function has a `thrown` parameter
- `throw error;` becomes `complete(default, error); return;`

If the function has no `thrown` parameter, the rewritten completion call simply omits the error argument.

### 5.3.5 `await`

`await` is a prefix operator used when calling functions.

It may be used only inside functions or lambdas declared `async`.

To be awaitable, the last parameter of the called function must:

1. be a `once` callable
2. return `void`
3. have no `out` parameters
4. optionally include one parameter marked `thrown`
5. be omitted at the call site when `await` is used

The awaited final parameter does not need a special nominal type name. Camp’s async model is structural. What matters is the shape of the final callback parameter.

### 5.3.6 Awaited result values

The value produced by an awaited call is the non-`thrown` parameter list of the completion callback.

That means:

- one completion value becomes one ordinary result
- multiple completion values form a grouped result
- a `thrown` completion parameter is rethrown automatically inside the awaiting function

Examples:

```camp
async int addAsync(int x, int y, thrown CalcError)
{
	return x + y;
}

async int sample(thrown CalcError)
{
	return await addAsync(3, 4);
}
```

Conceptually, the awaited call consumes a completion shape like:

```camp
once void(int result, thrown CalcError) complete
```

and yields the non-error part.

### 5.3.7 Calling async functions without `await`

An async function may still be called explicitly by supplying the final completion callback yourself:

```camp
calculator.addAsync(3, 4, (result, error) =>
{
	if (error != default)
		logError(error);
	else
		log(result);
});
```

This is not a second async subsystem. It is the same function, used through its explicit continuation surface.

### 5.3.8 `once`

A `once` callable frees its context immediately when it completes and may therefore be called only once.

This is an ordinary callable category, not an async-only invention. Async uses it because a completion callback naturally has one-shot lifetime.

### 5.3.9 `postpone`

Camp uses `postpone` for postponed invocation:

```camp
async int calculateAsync(int x, int y);

async int sample()
{
	auto addLater = postpone calculateAsync(3, 4);
	return await addLater();
}
```

`postpone f(args...)` does not call `f` immediately.

Instead it:

- evaluates each supplied argument immediately
- captures those argument values into callable context storage
- returns a callable postponed operation

Later invocation reuses the captured values.

The postponed-operation context is an ordinary container. If that context is escaped, anything captured into it must satisfy the ordinary container rule for escaped storage. If the postponed operation is used only in a narrower scoped context, scoped references may remain tied to that narrower scope.

### 5.3.10 Lifetimes across suspension

The detailed lifetime rules were defined earlier. The async-specific surface rule is simple:

> After a suspension point, a value may still be used only if it is escaped or is otherwise known, through `unscoped(...)`, to outlive the async frame that now contains it.

A local remains usable after suspension only if it is liftable into the async frame under the ordinary container rule.

Examples:

```camp
async void process(scoped char* text)
{
	use(text);
	await flushAsync();
}
```

Valid, because the scoped pointer is used only before suspension.

```camp
async void process(scoped char* text)
{
	auto copy = text;
	await flushAsync();
	use(copy);          // ERROR
}
```

Invalid, because the scoped pointer would survive into the lifted async frame without a relationship proving that it outlives that frame.

### 5.3.11 Structural async and interop

When exported, async functions are designed to be callable from C through their rewritten callback shape. The function signature matters, not a hidden runtime task type.

Conceptually:

```camp
export async int addAsync(Calculator* this, int x, int y, thrown CalcError)
{
	return x + y;
}
```

may export a C surface like:

```c
void Calculator_addAsync(
	Calculator* self,
	int32_t x,
	int32_t y,
	void* context,
	void (*complete)(void* context, int32_t result, CalcError error)
);
```

This is also why imported or external APIs can participate in Camp async as long as their final callback parameter matches the required structural rules.

## 5.4 `await foreach`

`await foreach` composes async control flow with the iterator model. An async iterator is still an iterator; it simply adds one extra surface: a readiness callback that says another call to `next(...)` may make progress.

### 5.4.1 Async iterators extend ordinary iterators

An async generator is declared with `class async iter`. There is no `struct async iter` form:

```camp
class async iter int getNumbersSlowly()
{
	yield 1;
	await delayAsync(1000);
	yield 2;
	await delayAsync(1000);
	yield 3;
}
```

The inherited iterator properties remain the same:

- there is still a frame
- there is still a `next(...)` step function
- values are still written through caller-provided storage
- cleanup is still deterministic

The async extension adds a readiness callback parameter to the lowered `next(...)` protocol.

### 5.4.2 `await foreach` consumption

Async iteration is consumed with `await foreach`:

```camp
await foreach (auto value in getNumbersSlowly())
{
	log(value);
}
```

Inside the loop surface, the compiler supplies the readiness callback automatically.

### 5.4.3 Blessed eligibility

A value may be consumed by `await foreach` when it is an `async iter` value.

That includes:

- a `class async iter` generator result
- a matching shape explicitly blessed with `(async)` or `(iter)` as appropriate
- a compatible external value explicitly cast into the blessed form

Matching lowered shape alone does not automatically participate in async-iterator semantics.

### 5.4.4 Meaning of the readiness callback

The readiness callback means only:

> another call to `next(...)` may make progress

It does not mean:

- a value is definitely ready
- iteration is complete
- an error occurred
- the callback carries the produced item

The consumer still learns those things only by calling `next(...)` again.

### 5.4.5 Latest-call semantics

The callback is associated with the most recent `next(...)` call.

The intended rule is deliberately weak:

- it may be invoked zero or one time
- the iterator is not required to invoke it
- once superseded by a later call, an older callback must not fire
- after cleanup, no callback may occur

This keeps the callback a readiness hint rather than a second result channel.

### 5.4.6 Synchronous callback invocation

An async iterator may invoke the readiness callback synchronously from within `next(...)`, provided it has already reached a stable re-entrant state.

That means an advanced consumer may immediately re-enter `next(...)` from inside the callback if it wants to.

### 5.4.7 Why `await foreach` needs more than ordinary `foreach`

An ordinary iterator step has two outcomes:

- it produced a value
- iteration ended

An async iterator adds a third case:

- iteration is not done, but this step produced no logical value yet

For stream-like iteration, this maps naturally onto a default count such as `0`. For general iterators, the yielded type may itself legitimately use its default value. Camp therefore needs one more rule.

### 5.4.8 Default skipping

In `await foreach`, an all-default yielded value is skipped and the loop body does not run.

This rule is mechanical:

- `0` is skipped for integer results
- `false` is skipped for `bool`
- `null` is skipped for pointer-like results
- a params-based result is skipped only if every lowered component is default

So `await foreach` uses the ordinary yielded value itself as the “no logical value this step” channel. It does not add a hidden `hasResult` flag.

### 5.4.9 `T?` as the escape hatch

If an async iterator must carry a real payload whose value may itself be default, it uses `T?`.

```camp
class async iter int? getScoresAsync()
{
	yield 0;
	await delayAsync(500);
	yield 5;
	await delayAsync(500);
	yield 10;
}
```

Here the logical `0` is carried as a specified optional payload rather than as `default(int?)`, so it is not skipped.

This is why Camp reuses ordinary optionals instead of inventing a hidden async-only presence channel.

### 5.4.10 Streams and count-like iterators

Stream-style async iterators fit this model especially well because `0` already naturally means “no logical yield this step.”

That is why the stream APIs use count-like iteration surfaces such as `nuint` results. They do not usually need `T?` at all.

### 5.4.11 `auto` autolift for optionals

When `await foreach` uses `auto` and the yielded type is `T?`, Camp autolifts the loop variable to `T`.

```camp
await foreach (auto score in getScoresAsync())
{
	log(score);
}
```

Here `score` is inferred as `int`, not `int?`.

This is a convenience rule only for `auto`. Explicit loop variable types remain allowed:

```camp
await foreach (int value in getScoresAsync())
{
	log(value);
}
```

```camp
await foreach (int? value in getScoresAsync())
{
	log(value.value);
}
```

In all cases, skipped/default steps remain filtered out before the loop body runs.

### 5.4.12 Manual iteration sees raw values

The skip rule belongs to `await foreach`, not to the raw async-iterator protocol.

A caller that drives an async iterator manually sees raw yielded values and must interpret defaults itself.

That split is intentional:

- `await foreach` is the convenient driver
- manual iteration remains the low-level ABI-facing protocol

### 5.4.13 Cleanup still follows the iterator rules

Async iteration does not replace the ordinary cleanup conventions.

At a high level:

- abstract async iterator values clean up through the ordinary `next(..., null, ...)` convention
- generated `class async iter` state cleans up through its destruction entry point
- `await foreach` remains responsible for deterministic cleanup on early exit

If the yielded type is params-based, cleanup still uses the ordinary first-component cleanup signal rule.

### 5.4.14 Design summary

The practical rules are:

- `class async iter T` declares an async generator
- the extra callback is a readiness hint only
- `await foreach` operates on blessed async-iterator values
- all-default yielded values are skipped by design
- real default-valued payloads should use `T?`
- `await foreach (auto ...)` autolifts `T?` to `T`
- manual driving still sees raw values and still performs explicit cleanup

## 5.5 Lambdas and Captured Context

Section 1 defined structural callable types. This section defines lambda expressions and the capture rules that let Camp build callable values without introducing a separate closure runtime.

### 5.5.1 Lambda expressions and target typing

A lambda expression target-types to the callable form required by its destination.

```camp
delegate bool(int value) predicate = value => (value % 2) == 0;
```

If the target is a non-capturing plain function type, the lambda becomes an `fn`.

If the target requires a delegate and the lambda does not need context, Camp forms a delegate whose context is `null`.

### 5.5.2 `auto` with lambdas

A lambda may also be assigned to `auto`.

For a non-capturing lambda or plain method reference, `auto` infers the plain `fn` form unless the expression is explicitly blessed.

```camp
auto increment = x => x + 1;
auto asDelegate = (delegate) x => x + 1;
```

This keeps the distinction between plain functions and context-carrying callables visible when no target type is present.

### 5.5.3 Capturing lambdas

A lambda that needs context target-types to a delegate-like callable form.

```camp
void sample()
{
	int baseValue = 100;
	auto addBase = (int value) => baseValue + value;
	log(addBase(23));
}
```

The resulting value has the ordinary grouped callable shape:

- a `context` component
- a `call` component

The call target receives the context first.

### 5.5.4 No separate capture-mode syntax

Camp does not have a separate capture list or capture-mode syntax.

Capture behavior is determined entirely by the lifetime of the callable value being created.

| Callable lifetime | Capture behavior |
|---|---|
| `scoped delegate` | accessed declaration-scope locals may be captured by reference |
| escaped delegate-like form | values are copied into escaped context storage |

This keeps lambda syntax small and pushes the important rule into the existing lifetime system rather than into a second closure-specific subsystem.

### 5.5.5 Scoped capture

A `scoped` delegate may access local state directly:

```camp
void runNow(delegate void() action)
{
	action();
}

void sample()
{
	int count = 0;

	scoped delegate void() action = () => count++;
	runNow(action);

	log(count);
}
```

This is valid because the delegate context does not outlive the declaration-scope containing `count`.

### 5.5.6 Escaped capture

An escaped delegate captures values when the delegate context is created:

```camp
void registerPredicate(escaped delegate bool(int value) predicate);

void sample()
{
	int limit = 10;
	registerPredicate((int value) => value > limit);
}
```

Here the escaped closure sees a copied capture value, not a direct reference to the original local.

### 5.5.7 Non-escaped references may not escape through lambdas

A non-escaped reference may not be captured into escaped delegate context storage unless the ordinary container rule proves that the escaped context is valid for it.

```camp
void registerLater(escaped delegate void() action);
void useText(char* text);

void demo(char* localText)
{
	registerLater(() => useText(localText));   // ERROR
}
```

This is rejected because the lambda would need to carry a non-escaped reference into escaped context storage without a relationship proving that the escaped context is valid for it.

### 5.5.8 Escaped captures are read-only

Values copied into escaped delegate context storage are read-only from the point of view of the closure state.

```camp
void registerLater(escaped delegate void() action);

void sample()
{
	int count = 0;

	registerLater(() => log(count + 1));   // OK
	registerLater(() => count++);          // ERROR
}
```

The escaped closure may observe the captured value, but it may not mutate the captured copy in place.

### 5.5.9 Method references and bound receivers

A callable value may also be formed from an existing callable target such as an instance method.

In that case, the callable context carries whatever receiver is needed by the lowered call target.

If an anonymous delegate type is produced from a member method, qualifiers written on an explicit `this` parameter persist onto the resulting hidden context parameter.

So Camp does not need a separate runtime category for bound methods. They are ordinary callable values whose context happens to contain a receiver.

### 5.5.10 Lambdas with async and iterator-style APIs

Because lambdas produce ordinary callable values, they compose naturally with both async and iterator-related APIs.

In practice this means:

- a lambda may be used as a completion callback for an async function
- a lambda may be used where a helper API expects a callable while driving an iterator or stream-like operation
- no special closure representation is required just because the surrounding API is async or iterator-oriented

Async composition is especially direct because Camp’s async model is structural and already defined in terms of ordinary callback shapes.

### 5.5.11 `postpone` and lambda-style capture reasoning

`postpone` is not spelled as a lambda, but it follows the same basic capture story:

- argument expressions are evaluated immediately
- the captured values are stored in callable context storage
- later invocation reuses those values
- the context obeys the same ordinary container rule as any other delegate-like context object

So `postpone` is best understood as deferred invocation built on ordinary callable capture, not as a second independent async feature.

### 5.5.12 Design summary

The important practical rules are:

- a lambda expression target-types to a callable form
- non-capturing lambdas and plain method references target-type to `fn`
- the same non-capturing lambda may become a delegate implicitly when the target requires one
- explicit blessing such as `(delegate)` can force a blessed callable form for `auto`
- there is no separate capture list feature in v1
- capture mode follows callable lifetime
- scoped delegates may access locals directly within the valid scope
- escaped delegates capture values into escaped context storage, and those captured values are read-only
- method references, async callbacks, and postponed invocation all reuse the same ordinary callable model

# 6. Generics

Camp generics are part of the ordinary language surface. They are not a separate compile-time world, and they do not depend on template monomorphization. A generic declaration exists in callable, exportable form before any particular substitution is chosen. The language therefore treats generic transport, storage, interface dispatch, and construction as ordinary ABI-visible mechanisms rather than as hidden compiler magic.

Camp uses an erased generic model with explicit rules for:

- constraints
- definition-time validation
- value transport
- materialized storage
- interface capability
- construction and destruction through interface contracts

The central design goal is simple:

> Generic code must obey the same ABI-first rules as the rest of Camp.

This section defines the generic model itself. It assumes the ordinary rules for `params`, storage materialization, interfaces, lifetimes, `within`, constructors, and destructors that were defined earlier.

## 6.1 Generic Constraints

Camp uses inline generic constraints.

```camp
struct CounterMap<T: nint>
{
	T key;
	nint count;
}

TResult map<TSource: any, TResult: any>(in TSource value)
{
	...
}

newtype delegate bool Predicate<T: any>(in T value);

interface IMap<TKey: any, TValue: any>
{
	void add(in TKey key, in TValue value);
}
```

The same inline form is used on:

- generic structs and classes
- generic methods
- generic delegates
- generic interfaces
- generic interface methods

Each generic type parameter has exactly one constraint.

If no constraint is written, the constraint is `nint`.

### 6.1.1 Definition-time validation

Generic definitions are validated eagerly.

That includes:

- every generic member
- unused generic code
- code paths that are not yet instantiated with a concrete argument

Camp does not use a deferred-template-error model. A generic body must already be valid under its declared constraint.

So generic checking usually happens twice:

1. when the generic definition itself is compiled
2. again after concrete substitution, when the resulting program is validated normally

This makes generic code more stable under refactoring and prevents invalid bodies from hiding behind future instantiations.

### 6.1.2 Constraint categories

Camp generic constraints fall into three categories.

| Kind | Example | Meaning |
|---|---|---|
| integer representation constraint | `T: int` | inside the body, `T` behaves as the chosen integer representation |
| erased value constraint | `T: any` | `T` may be any type supported by the erased model |
| nominal interface capability | `T: implements IRef` | `T` must explicitly implement the named interface |

A constraint describes what the generic body may assume. It does not create some separate runtime kind.

### 6.1.3 Integer representation constraints

Other than `any` and `implements`, Camp uses only integer-type constraints.

Valid integer-type constraints are:

- `byte`
- `ushort`
- `short`
- `uint`
- `int`
- `ulong`
- `long`
- `nuint`
- `nint`

Inside the generic body, `T` behaves as the chosen constrained representation.

```camp
struct CounterMap<T: uint>
{
	T key;
	uint count;

	void add(T key, uint amount = 1)
	{
		if (this.key == key)
			this.count += amount;
	}
}
```

This model is suitable for small numeric abstractions whose operations are intentionally representation-driven.

### 6.1.4 Valid type arguments for integer constraints

For an integer-type constraint other than `nint` or `nuint`, valid type arguments are:

- any integer type of equal or lesser width
- any enum whose underlying type has equal or lesser width
- any `newtype` whose underlying type has equal or lesser width

Examples:

```camp
struct Table<T: uint>
{
	T value;
}

enum SmallCode: byte { A, B, C }
newtype UserId: uint;
newtype LocalIndex: ushort;

Table<byte> a;       // OK
Table<ushort> b;     // OK
Table<uint> c;       // OK
Table<SmallCode> d;  // OK
Table<UserId> e;     // OK
Table<LocalIndex> f; // OK

Table<ulong> g;      // ERROR: wider than uint
```

The point of the rule is to let the body rely on one known representation width while still accepting narrower nominal or intrinsic arguments.

### 6.1.5 The special role of `nint` and `nuint`

`nint` is both:

- an ordinary integer constraint
- the default constraint when no other constraint is written

`nint` and `nuint` are special because they may also represent pointer-sized values.

For a constraint of `nint` or `nuint`, valid type arguments are:

- any integer type of width 32 bits or less
- any enum whose underlying type is 32 bits or less
- any `newtype` whose underlying type is 32 bits or less
- any pointer type

Examples:

```camp
struct Slot<T>
{
	T value;
}

enum Token: uint { A, B }
newtype Handle: nint;

Slot<int> a;       // OK
Slot<uint> b;      // OK
Slot<Token> c;     // OK
Slot<byte*> d;     // OK
Slot<void*> e;     // OK
Slot<Handle> f;    // OK

Slot<long> g;      // ERROR
Slot<ulong> h;     // ERROR
```

This is why the default generic constraint is useful for low-level code: it naturally covers common machine-sized integers, many nominal wrappers, and pointer-shaped tokens.

### 6.1.6 `implements IFoo`

`implements IFoo` is nominal only.

```camp
interface IRef
{
	void retain();
	void release();
}

class RefHolder<T: implements IRef>
{
	T* value;

	RefHolder(T* value, vtableof(T: IRef))
	{
		this.value = value;
	}
}
```

This means:

- the concrete type must explicitly implement `IFoo`
- structural similarity is not enough
- the type parameter remains the concrete type, not an interface value

## 6.2 `T: any` and Materialization

Camp has two broad generic models:

- representation generics
- erased value generics

The difference matters because it determines how values are transported, stored, and addressed inside the generic body.

### 6.2.1 Erased value generics

`T: any` is the erased model for types that do not share one known machine representation.

```camp
class AnyList<T: any>
{
	T* items;
	nuint capacity;
	nuint count;
	Allocator* allocator;

	AnyList(sizeof(T), within allocator)
	{
		this.allocator = allocator;
	}
}
```

`T: any` may represent:

- scalars
- pointers
- copyable structs
- fixed structs
- classes
- materializable `params`-based values
- other forms supported by the language

In this model, the generic body does not rely on one fixed source-level representation for every `T`. Instead, it relies on the erased transport and storage rules below.

### 6.2.2 `in` is transport, not pointer semantics

In erased generic code, `in` becomes especially important.

An `in T` parameter:

- behaves like a by-value parameter in source
- is passed as a hidden pointer in the ABI
- does not expose pointer mechanics at the call site
- does not by itself change the lifetime category of the logical value

```camp
void append<T: any>(AnyList<T>* list, in T item)
{
	list.add(item);
}
```

This is the usual preferred style for erased generic input parameters.

The address of an `in T` parameter refers to the address of the local transport image for the call, and therefore has an implicit scoped lifetime.

Use `T*` instead of `in T` only when the API truly needs one of these:

- stable shared storage
- mutation through that storage
- explicit address identity
- a pointer that may be kept or returned

### 6.2.3 `out T` and return `T`

The same transport logic applies to:

- `out T`
- return `T`

For copyable types, these forms may materialize as needed because they describe value transport, not shared pointer identity. For fixed structs and classes, the same forms are valid only where the corresponding non-copying class-like operation is valid.

That is why erased generic APIs should usually prefer:

- `in T`
- `out T`
- return `T`

and use `T*` only deliberately when address identity is part of the contract.

### 6.2.4 Grouped values and storage

Camp distinguishes grouped values from real one-address storage.

At the type level:

- `params(T)` is the grouped-value form
- `struct(T)` is the materialized storage form

At the value level:

- `(params)expr` converts storage to grouped form
- `(struct)expr` materializes a grouped value into storage

This distinction matters sharply in erased generics because `T` may denote a grouped value that does **not** already exist as one storage object.

### 6.2.5 The meaning of `T*` in erased generic code

Outside erased substitution, pointer rules are ordinary.

Inside erased substitution of `<T: any>`, `T*` means a pointer to the materialized storage form of `T` for grouped or otherwise materialized copyable types. For fixed structs and classes, it means a pointer to the fixed instance.

So for a grouped or otherwise materialized copyable type in this context:

> `T*` means `struct(T)*`.

```camp
void consumePointPtr<T: any>(T* value)
{
	...
}

params Point(int x, int y);

Point groupedPoint = (x: 5, y: 10);
auto temp = (struct)groupedPoint;
consumePointPtr(&temp);
```

A grouped value does not automatically provide the right pointer type. If code needs a real pointer to the value as a whole, it must materialize storage first.

### 6.2.6 When materialization is automatic

Materialization may occur automatically for copyable types in transport positions:

- `in T`
- `out T`
- return `T`

That is safe because these positions describe temporary value transport. It is not a permission to copy fixed structs or classes.

Materialization is **not** automatic for:

- `T*`
- array element identity
- other positions where one real address matters semantically

There, the generic code is asking for actual storage, not just transport.

### 6.2.7 Arrays and optionals in generic code

Arrays and optionals remain compiler-reserved built-in type forms. They are not ordinary library generics.

#### Arrays

An array type `T[]` remains a nominal built-in `params` form with canonical components such as:

- `elements`
- `length`

In a `T: any` context, if erased generic storage requires materialization for a copyable type, the effective storage model becomes:

```camp
struct(T)[]
```

even though the surface syntax remains `T[]`. For fixed structs and classes, array elements follow class-like copyability rules.

```camp
void appendAll<T: any>(AnyList<T>* list, T[] items)
{
	for (nuint i = 0; i < items.length; i++)
		list.add(items[i]);
}
```

This matters because ordinary grouped arrays and materialized arrays are not the same type. For example, `String[]` and `struct(String)[]` are distinct concepts.

#### Optionals

`T?` remains a compiler-reserved nominal `params` form with the usual optional semantics. Generic code should treat it as a built-in type transformation rather than as user-defined generic sugar. If `T` is a fixed type, `T?` is not valid; use `T*?` when an optional pointer is intended.

### 6.2.8 Delegates and strings in generic code

The same general principle applies to other compiler-reserved nominal grouped forms.

- delegates are grouped `(context, call)` values
- strings and string views are nominal `params` forms
- arrays and optionals remain compiler-reserved forms

Generic code may use these types normally, but it should not assume that all of them share one identical storage model. When pointer identity for the whole value matters, the grouped-versus-materialized distinction still controls.

### 6.2.9 Lifetimes and async

The erased generic model does not replace Camp’s lifetime rules.

`escaped`, default `scoped`, `unscoped(...)`, return annotations such as `scoped(...)`, and declaration-site `escaped` defaults still apply after substitution in the ordinary way. Camp does not add a second generic-only lifetime system on top.

In particular:

- `in` changes transport, not lifetime category
- generic async code follows the ordinary async-frame rules
- container rules still apply after substitution
- there is no generic-only exception for delegates, arrays, or interfaces involving `T`

## 6.3 `sizeof(T)` and `vtableof(T: Interface)`

Camp does not hide runtime generic support behind metadata objects. When generic code needs type-size or interface-vtable information at runtime, it requests those values explicitly.

### 6.3.1 `sizeof(T)`

A generic method or generic type constructor may request `sizeof(T)`.

```camp
class Buffer<T: any>
{
	byte* data;
	nuint count;

	Buffer(sizeof(T))
	{
	}
}
```

The compiler supplies the concrete size at the call site. Across the ABI, it is just an ordinary explicit parameter.

For representation generics, `sizeof(T)` is the size of the chosen representation.

For `T: any`, `sizeof(T)` is the size of the concrete substituted type’s materialized storage form when such storage exists. For fixed structs and classes, `sizeof(T)` is not supplied automatically merely because an operation might copy; generic code must request it explicitly when it truly needs size-based storage or allocation.

This is why `sizeof(T)` composes naturally with the rule that erased `T*` means `struct(T)*` for materialized storage.

### 6.3.2 `vtableof(T: IFoo)`

A generic method or generic class constructor may request the non-fixup interface vtable for a constrained type.

```camp
void retainTwice<T: implements IRef>(T* value, vtableof(T: IRef))
{
	value.retain();
	value.retain();
}
```

The compiler supplies that vtable explicitly at the call site. Again, this is just an ordinary ABI parameter.

The requested value is the relevant non-fixup interface vtable for the concrete implementation relationship `T : IFoo`.

### 6.3.3 Why these values are explicit

Camp considered more implicit metadata-style mechanisms, but explicit special parameters fit the language better.

They keep the model:

- ABI-visible
- mechanically simple
- usable from foreign code
- understandable without a hidden runtime type system

Generic code is therefore explicit about what runtime support it needs.

### 6.3.4 Placement rules

Both `sizeof(T)` and `vtableof(T: IFoo)` may refer to type parameters declared on:

- the current method
- the containing generic type

Constructors do not declare their own generic parameter lists, but they may refer to the generic parameters of their containing type.

### 6.3.5 Storing these values for later use

When `sizeof(T)` or `vtableof(T: IFoo)` is requested in a generic class constructor, the compiler may retain the supplied value in hidden instance state for later instance-method use.

That means a generic class may receive the capability once during construction and then use it later without repeating the full explicit parameter list on every method.

```camp
class RefHolder<T: implements IRef>
{
	T* value;

	RefHolder(T* value, vtableof(T: IRef))
	{
		this.value = value;
	}

	void retain()
	{
		this.value.retain();
	}
}
```

### 6.3.6 Example: size-aware erased storage

```camp
class AnyList<T: any>
{
	T* items;
	nuint count;
	nuint capacity;
	Allocator* allocator;

	AnyList(sizeof(T), within allocator)
	{
		this.allocator = allocator;
	}

	void ensureCapacity(nuint minimum)
	{
		if (minimum <= this.capacity)
			return;

		nuint newCapacity = this.capacity == 0 ? 4 : this.capacity * 2;
		if (newCapacity < minimum)
			newCapacity = minimum;

		this.items = allocator.reallocUntyped(this.items, sizeof(T) * newCapacity);
		this.capacity = newCapacity;
	}
}
```

The generic class asks for the storage size explicitly and uses it through ordinary allocator operations.

## 6.4 Generics and Interfaces

Interfaces participate naturally in Camp generics, but they do so through nominal capability and explicit vtable passing, not through hidden runtime adaptation.

### 6.4.1 `implements IFoo` means capability, not representation

`T: implements IFoo` means the concrete type must explicitly implement `IFoo`, and generic code still operates on `T` rather than on an interface value.

```camp
interface IRef
{
	void retain();
	void release();
}

class RefHolder<T: implements IRef>
{
	T* value;

	RefHolder(T* value, vtableof(T: IRef))
	{
		this.value = value;
		this.value.retain();
	}

	~RefHolder()
	{
		this.value.release();
	}
}
```

### 6.4.2 Generic interfaces

Interfaces themselves may be generic.

```camp
interface IComparer<T: any>
{
	int compare(in T left, in T right);
}
```

A generic interface is just another generic declaration category. Its entries remain part of the ordinary ABI-visible interface surface.

### 6.4.3 Generic interface methods

Interface methods may also declare their own generic parameters.

```camp
interface ITransformer
{
	TResult transform<TSource: any, TResult: any>(in TSource value);
}
```

This is allowed under the ordinary erased generic model. Generic interface methods are part of the normal language surface.

### 6.4.4 Interface-constrained generic dispatch

When generic code wants to dispatch through an interface contract, it requests the needed vtable explicitly and then uses the ordinary member syntax on the constrained concrete type.

```camp
void retainTwice<T: implements IRef>(T* value, vtableof(T: IRef))
{
	value.retain();
	value.retain();
}
```

This expresses two distinct facts:

- `implements IRef` gives nominal capability
- `vtableof(T: IRef)` supplies the runtime interface information needed by the erased generic body

### 6.4.5 Struct and class implementations remain distinct

The interface constraint model does not erase the underlying implementation strategy.

A constrained type may still be:

- a class that implements the interface through hidden interface-vtable fields
- a struct that implements the interface through scoped indirect interface pointer conversion when such conversion is used

The generic constraint states only that the required nominal interface contract exists.

If the relevant interface is an `escaped interface`, generic code may still rely on the receiver contract declared by that interface, but automatic struct-to-interface conversion remains unavailable for that interface.

## 6.5 Generic Construction and Destruction

Generic code sometimes needs more than transport and dispatch. It may need to create values, allocate objects, or tear them down.

Camp keeps that model explicit.

### 6.5.1 Construction through interface contracts

When generic code needs constructor or destructor capability, it obtains that capability through an interface contract.

```camp
interface ITextBuilder
{
	ITextBuilder(within allocator);
	~ITextBuilder(within allocator);
	void append(String text);
	String takeString();
}
```

In this model:

- the interface constructor lowers to a `create` vtable entry
- the interface destructor lowers to a `destroy` vtable entry
- the allocator is part of the construction and destruction contract

This gives erased generic code a fully explicit way to construct and destroy constrained concrete types.

### 6.5.2 Example: interface-constrained generic construction

```camp
class LineWriter<T: implements ITextBuilder>
{
	T* builder;

	LineWriter(vtableof(T: ITextBuilder), within allocator)
	{
		this.builder = new T();
	}

	~LineWriter(within allocator)
	{
		delete this.builder;
	}

	void writeLine(String text)
	{
		this.builder.append(text);
		this.builder.append("\n");
	}

	String finish()
	{
		return this.builder.takeString();
	}
}
```

Important points:

- the constructor requests `vtableof(T: ITextBuilder)` explicitly
- `new T()` is valid here because the interface contract supplies constructor capability
- `delete this.builder` is valid here because the interface contract supplies destructor capability
- the allocator is supplied through the active `within` context or explicitly by the caller

### 6.5.3 Direct and indirect construction are different

This distinction is important:

```camp
new T();            // may be valid in constrained generic code
new ITextBuilder(); // always invalid
```

The first form constructs a concrete type parameter whose contract grants the needed capability.

The second form attempts to instantiate a bare interface type, which is not a concrete object type.

### 6.5.4 Allocator threading in generic construction

Generic construction follows the same allocator model as ordinary Camp code.

If the relevant constructor or destructor path uses `within allocator`, the allocator is supplied:

- explicitly by argument
- or implicitly through an active `within` context

A concrete implementation that satisfies an interface constructor or destructor contract does not need to declare `within allocator` unless it actually uses that parameter. The generated thunk forwards the allocator only when the concrete implementation uses it.

### 6.5.5 Erased storage allocation

Generic code that allocates raw storage rather than invoking constructors uses the ordinary erased-generic tools:

- `sizeof(T)`
- `T*` meaning `struct(T)*`
- allocator APIs

```camp
class AnyList<T: any>
{
	T* items;
	nuint capacity;
	nuint count;
	Allocator* allocator;

	AnyList(sizeof(T), within allocator)
	{
		this.allocator = allocator;
	}

	void ensureCapacity(nuint minimum)
	{
		if (minimum <= this.capacity)
			return;

		nuint newCapacity = this.capacity == 0 ? 4 : this.capacity * 2;
		if (newCapacity < minimum)
			newCapacity = minimum;

		this.items = allocator.realloc<T>(this.items, newCapacity, sizeof(T));
		this.capacity = newCapacity;
	}
}
```

This is ordinary erased storage management, not a separate generic allocation subsystem.

### 6.5.6 Destruction remains explicit

Camp generics do not introduce hidden destruction behavior.

If generic code needs destruction, it uses ordinary source-level operations:

- `delete value`
- interface destructor contracts
- explicit destructor-capable APIs

A generic type that owns a `T*` should request and use the destruction capability it actually depends on.

### 6.5.7 No hidden constructor synthesis

The generic model does not create hidden constructor paths that do not already exist.

If a type has no valid construction path under the declared contract, generic code may not pretend otherwise. Likewise, representation-level storage does not imply constructor semantics.
# 7. Standard Library

This section documents the standard library surfaces that are central enough to belong in the main reading path of the language reference.

The goal of the Camp standard library is not to hide the language behind large object frameworks. Its central APIs are small, explicit, and shaped to match the language's ABI-visible model.

Two ideas appear repeatedly:

- lightweight value forms are preferred over hidden runtime objects
- allocation is explicit, and APIs that allocate usually advertise that fact in their names

The library surfaces described here build directly on the language rules already defined for arrays, strings, `params`, iterators, async functions, property accessors, and allocators. This section focuses on the public library shape and intended usage.

## 7.1 Arrays and Strings

Camp treats arrays and string-family types as core library-facing value forms rather than as heavy runtime containers.

Arrays, strings, and string views are all lightweight nominal grouped types. They may share similar physical shapes, but they are still distinct types with distinct semantics and method sets.

### 7.1.1 Arrays

An array value is a built-in nominal form with two canonical components:

- `elements`
- `length`

That underlying model was defined earlier. What matters here is the standard API surface attached to arrays.

#### 7.1.1.1 Array API design

The standard array API follows a few simple rules:

- mutating operations return the array itself when chaining is useful
- allocating operations are explicit
- indexing and slicing are ordinary methods with index-aware parameter annotations
- generic array operations use `T: any` plus `sizeof(T)`

The core generic array methods are:

```camp
// Search and comparison.
nint indexOf<T: any>(const T[] this, in T match, sizeof(T));
nint indexOfAny<T: any>(const T[] this, const T[] matches, sizeof(T));
nint lastIndexOf<T: any>(const T[] this, in T match, sizeof(T));
nint lastIndexOfAny<T: any>(const T[] this, const T[] matches, sizeof(T));
bool contentsEqualTo<T: any>(const T[] this, const T[] other, sizeof(T));

// In-place mutation.
scoped T[] fill<T: any>(T[] this, in T value, sizeof(T));
scoped T[] overwrite<T: any>(T[] this, const T[] source, sizeof(T));
scoped T[] update<T: any>(T[] this, delegate T(in T element) updater, sizeof(T));
scoped T[] sort<T: any>(T[] this, delegate int(in T a, in T b) comparer, sizeof(T));
scoped T[] reverse<T: any>(T[] this, sizeof(T));

// Borrowing operations.
scoped T[] slice<T: any>(const T[] this, @range nuint index = 0, nuint count = ^0, sizeof(T));
scoped T* addressOf<T: any>(T[] this, @index nuint index, sizeof(T));

// Allocating operations.
T[] copy<T: any>(const T[] this, within allocator, sizeof(T));
TResult[] mapCopy<T: any, TResult: any>(const T[] this, delegate TResult(in T element) mapper, sizeof(T), within allocator);
T[] filterCopy<T: any>(const T[] this, delegate bool(in T element) predicate, within allocator, sizeof(T));
```

These methods are intentionally small and conventional. Camp does not try to make arrays into a deep collection hierarchy.

#### 7.1.1.2 Searching, comparison, and mutation

The search methods return the usual index or `-1` result:

```camp
int[] values = [3, 5, 8, 13, 21];

nint a = values.indexOf(8);
nint b = values.lastIndexOfAny([5, 21]);
bool same = values.contentsEqualTo([3, 5, 8, 13, 21]);
```

In-place methods return the array itself so that common pipelines remain short:

```camp
int[] values = new int[5] finally delete;

values
	.fill(1)
	.update(x => x * 2)
	.reverse();
```

`sort(...)` uses an ordinary comparer delegate:

```camp
values.sort((left, right) => left - right);
```

Delegate parameters are `scoped` by default, so array callbacks follow the ordinary delegate lifetime rules.

#### 7.1.1.3 Indexing and slicing

Arrays support ordinary element indexing syntax through `addressOf()`.

That means this library declaration:

```camp
scoped T* addressOf<T: any>(T[] this, @index nuint index, sizeof(T));
```

supports code such as:

```camp
int[] values = [10, 20, 30, 40, 50];

int first = values[0];
int last = values[^1];
int* p = &values[2];
```

Borrowed slicing is provided by `slice(...)`:

```camp
auto middle = values.slice(1, 3);
auto inner = values.slice(1..^1);
```

The `@index` and `@range` annotations allow the compiler to understand from-end syntax such as `^1` and range syntax such as `1..^1` while still keeping the ABI surface as ordinary methods.

#### 7.1.1.4 Allocating array operations

Methods that allocate storage say so clearly.

```camp
int[] original = [1, 2, 3, 4];

within(tempArena)
{
	auto copied = original.copy() finally delete;
	auto doubled = original.mapCopy(x => x * 2) finally delete;
	auto even = original.filterCopy(x => x % 2 == 0) finally delete;
}
```

This is the standard pattern throughout the Camp library:

- borrowed operations return views or scoped values
- allocating operations take `within allocator` and produce new owned results

#### 7.1.1.5 Arrays of `params` types

The generic array API is written for `T: any`, but an important practical rule applies when the element type is a non-materialized grouped type.

The array operations documented here work for arrays of ordinary materialized elements. For grouped `params` types, that usually means using the materialized form.

For example:

```camp
struct(String)[] items;
```

is an ordinary materialized array element type for generic array algorithms, while:

```camp
String[] items;
```

remains a distinct grouped array type with different semantics.

Camp may grow more direct operations for non-materialized grouped arrays, but that is separate from the API surface documented here.

### 7.1.2 Strings and Views

Camp distinguishes ordinary string types from borrowed string-view types.

The three built-in families are:

| Family | Borrowed view | Ordinary string | Units |
|---|---|---|---|
| UTF-8 | `StringView` | `String` | `char` |
| UTF-16 | `WStringView` | `WString` | `wchar` |
| ASCII / system code page | `AStringView` | `AString` | `achar` |

The canonical declarations are:

```camp
params StringView(@nosuffix char* units, nuint length);
params WStringView(@nosuffix wchar* units, nuint length);
params AStringView(@nosuffix achar* asciichars, nuint length);

params String: StringView;
params WString: WStringView;
params AString: AStringView;

~String(within allocator);
~WString(within allocator);
~AString(within allocator);
```

The narrowing declarations mean that the owning types are distinct nominal types that widen implicitly to their corresponding view types.

#### 7.1.2.1 Owning versus borrowed text

The basic rule is simple:

- `StringView` is a borrowed view into text data
- `String` is the ordinary UTF-8 string type
- the same distinction applies to the UTF-16 and ASCII families

A `String` may be passed where a `StringView` is expected:

```camp
void printLine(StringView text)
{
	Console.writeLine(text);
}

String text = "hello";
printLine(text);
```

The reverse conversion is not implicit.

Camp intentionally keeps borrowing visible. Operations such as trimming and slicing therefore return views rather than automatically allocating new strings.

#### 7.1.2.2 Deletion and null termination

Owning strings may define destructors and therefore may support `delete`.
View types do not.

```camp
auto upper = someText.uppercaseCopy(within arena) finally delete;
```

The widening relationship does not carry delete support with it: a deletable `String` does not make `StringView` deletable.

Only string literals are automatically null-terminated. Ordinary `String` values are not assumed to preserve a general null-termination guarantee unless a specific API says so.

#### 7.1.2.3 Character encodings and scalar conversions

The library uses these character types:

| Type | Meaning |
|---|---|
| `char` | UTF-8 code unit |
| `wchar` | UTF-16 code unit |
| `uchar` | Unicode code point |
| `achar` | ASCII / system-code-page character |

The intended widening chain is:

```text
achar -> char -> wchar -> uchar
```

Narrowing casts exist in the opposite direction. These are ordinary value conversions, not checked transcoding APIs.

#### 7.1.2.4 String indexing and slicing are code-unit based

String indexing and slicing use code-unit positions, not Unicode character positions.

This is deliberate. It keeps the core API cheap and predictable while still allowing Unicode-aware operations where they are actually needed.

Examples:

```camp
StringView text = "héllo";

nint i = text.indexOf("ll");
uchar ch = text.Char[2];
nuint width = text.CharUnits[^1];
auto tail = text.slice(2..);
```

Important consequences:

- `indexOf(...)`, `slice(...)`, and related methods speak in units
- `getChar(...)` decodes the code point beginning at a code-unit position
- `countChars()` is a decoding operation and may therefore be `O(n)`

#### 7.1.2.5 Common view operations

The string-view API is intentionally broad enough for ordinary text work but still small enough to remember.

The common methods are conceptually duplicated across the UTF-8, UTF-16, and ASCII view families.

##### Searching and comparison

```camp
nint indexOf(const StringView this, const StringView match, bool caseInsensitive = false);
nint indexOfChar(const StringView this, uchar match, bool caseInsensitive = false);
nint indexOfAnyChar(const StringView this, const uchar[] matches, bool caseInsensitive = false);

nint lastIndexOf(const StringView this, const StringView match, bool caseInsensitive = false);
nint lastIndexOfChar(const StringView this, uchar match, bool caseInsensitive = false);
nint lastIndexOfAnyChar(const StringView this, const uchar[] matches, bool caseInsensitive = false);

bool startsWith(const StringView this, const StringView match, bool caseInsensitive = false);
bool startsWithChar(const StringView this, uchar match, bool caseInsensitive = false);
bool endsWith(const StringView this, const StringView match, bool caseInsensitive = false);
bool endsWithChar(const StringView this, uchar match, bool caseInsensitive = false);

nint compareTo(const StringView this, const StringView other, bool caseInsensitive = false);
```

These methods operate on code-unit sequences. Case-insensitive behavior is part of the individual method contract rather than a separate comparer framework.

##### Unicode-aware access

```camp
struct iter uchar chars(const StringView this);
nuint countChars(const StringView this);
uchar getChar(const StringView this, @index nuint unit);
nuint getCharUnits(const StringView this, @index nuint unit);
```

Typical usage:

```camp
foreach (uchar ch in text.chars())
{
	processCodePoint(ch);
}

uchar first = text.Char[0];
nuint lastWidth = text.CharUnits[^1];
```

Property syntax keeps unit-based indexing readable without exposing raw `addressOf()` on strings.

##### Borrowing transformations

```camp
StringView trim(const StringView this);
StringView trimStart(const StringView this);
StringView trimEnd(const StringView this);
scoped StringView slice(const StringView this, @range nuint index = 0, nuint count = ^0);
```

These operations return borrowed views rather than allocating new storage.

```camp
StringView line = input.trim();
StringView stem = line.slice(..^1);
```

##### Copy-producing transformations

```camp
String uppercaseCopy(const StringView this, within allocator);
String lowercaseCopy(const StringView this, within allocator);

String StringView_concatCopy(const StringView[] strings, within allocator);
String StringView_joinCopy(const StringView separator, const StringView[] strings, within allocator);

(scoped StringView)[] splitCopy(const StringView this, const StringView separator, within allocator);
```

The naming is intentional:

- `uppercaseCopy(...)` and `lowercaseCopy(...)` allocate
- `concatCopy(...)` and `joinCopy(...)` allocate
- `splitCopy(...)` allocates only the outer array, not the text segments themselves

Example:

```camp
within(tempArena)
{
	auto words = sentence.splitCopy(" ") finally delete;
	auto loud = sentence.uppercaseCopy() finally delete;
	auto combined = StringView_joinCopy(", ", words) finally delete;
}
```

In the `splitCopy(...)` case, the returned array owns only the array storage. The individual `StringView` elements still borrow the original text storage.

#### 7.1.2.6 Family-specific conversions and reinterpretations

Each string family also has a few type-specific helpers.

##### UTF-8 family

```camp
String toStringCopy(const StringView this, within allocator);
WString convertToWStringCopy(const StringView this, within allocator);
AString convertToAStringCopy(const StringView this, within allocator);
scoped char[] asArray(const StringView this);
scoped AStringView assumeAStringView(const StringView this);
```

##### UTF-16 family

```camp
WString toWStringCopy(const WStringView this, within allocator);
String convertToStringCopy(const WStringView this, within allocator);
AString convertToAStringCopy(const WStringView this, within allocator);
scoped wchar[] asArray(const WStringView this);
```

##### ASCII family

```camp
AString toAStringCopy(const AStringView this, within allocator);
String convertToStringCopy(const AStringView this, within allocator);
WString convertToWStringCopy(const AStringView this, within allocator);
scoped achar[] asArray(const AStringView this);
void makeUppercase(AStringView this);
void makeLowercase(AStringView this);
```

The ASCII in-place case operations intentionally return `void`, so mutating an `AStringView` does not suggest that a new owning string was created.

#### 7.1.2.7 Array-to-view reinterpretation helpers

The standard library also provides direct view reinterpretation from raw character arrays:

```camp
StringView asView(const char[] this);
WStringView asView(const wchar[] this);
AStringView asView(const achar[] this);
```

These helpers are especially useful when interoperating with lower-level APIs:

```camp
char[] buffer = init char[256];
StringView text = buffer.asView();
```

#### 7.1.2.8 Strings do not expose raw element addressing

Built-in arrays expose `addressOf()` and therefore ordinary `[]` element access in the standard library.
Strings and string views intentionally do not.

Character access goes through:

- `getChar(...)`
- `getCharUnits(...)`
- property syntax such as `text.Char[index]`

This makes it clearer that strings are text-oriented types, not raw mutable buffers.

If code actually wants a raw unit array, it can use `asArray()` and then work with the resulting array view explicitly.

### 7.1.3 `StringBuilder`

`StringBuilder` remains a provisional part of the standard library design, but its intended role is clear enough to note here.

It is the mutation-heavy text-construction utility for cases where repeated string concatenation would be awkward or wasteful.

Its design goals are:

- explicit allocator usage
- efficient incremental growth
- a guaranteed null terminator in the backing buffer
- a clear distinction between borrowed access and ownership transfer

A representative sketch looks like this:

```camp
class StringBuilder
{
	StringBuilder(within allocator);
	~StringBuilder();

	void clear();
	void ensureCapacity(nuint minimum, thrown MemoryError);
	void trimExcess(thrown MemoryError);

	nuint getLength();
	nuint getCapacity();
	StringView asView();
	String asString();

	void append(uchar ch, thrown MemoryError);
	void append(StringView text, thrown MemoryError);
	void append(String text, thrown MemoryError);
	void appendBool(bool value, thrown MemoryError);
	void appendInt(nint value, thrown MemoryError);
	void appendUInt(nuint value, thrown MemoryError);
	void appendUppercase(StringView text, thrown MemoryError);
	void appendLowercase(StringView text, thrown MemoryError);
	void appendLine(thrown MemoryError);
	void appendLine(StringView text, thrown MemoryError);
}
```

The exact finalized method set may still change. The important point is conceptual: builders are where mutation-heavy string construction belongs.

## 7.2 Streams and I/O

Camp models streaming I/O using a small core abstraction:

- a reader fills a caller-provided buffer and yields how many elements were produced
- a writer consumes a caller-provided buffer and yields how many elements were accepted

This stream model is intentionally compatible with the iterator model introduced earlier and with the async model introduced later. It does not introduce a separate hidden runtime stream object system.

### 7.2.1 Core stream families

The library defines separate nominal stream delegate types for each element family:

```camp
export as Std::IO;

export newtype iter nuint ByteReader(byte[] buffer);
export newtype iter nuint ByteWriter(const byte[] buffer);
export newtype async iter nuint AsyncByteReader(byte[] buffer);
export newtype async iter nuint AsyncByteWriter(const byte[] buffer);

export newtype iter nuint CharReader(char[] buffer);
export newtype iter nuint CharWriter(const char[] buffer);
export newtype async iter nuint AsyncCharReader(char[] buffer);
export newtype async iter nuint AsyncCharWriter(const char[] buffer);

export newtype iter nuint WCharReader(wchar[] buffer);
export newtype iter nuint WCharWriter(const wchar[] buffer);
export newtype async iter nuint AsyncWCharReader(wchar[] buffer);
export newtype async iter nuint AsyncWCharWriter(const wchar[] buffer);

export newtype iter nuint ACharReader(achar[] buffer);
export newtype iter nuint ACharWriter(const achar[] buffer);
export newtype async iter nuint AsyncACharReader(achar[] buffer);
export newtype async iter nuint AsyncACharWriter(const achar[] buffer);
```

The meaning is straightforward:

- for readers, the caller provides a writable buffer
- for writers, the caller provides a readable buffer
- each iteration step yields the number of elements transferred

The result type is always `nuint`.

That choice matters because it makes progress reporting uniform and gives async streams a natural default "no progress yet" value of `0`.

### 7.2.2 Layering

The stream library follows a deliberate three-layer structure:

1. core stream delegates
2. adapters between byte and text stream families
3. helper methods for common high-level tasks such as writing strings and iterating lines

This keeps the core small while still allowing ergonomic APIs above it.

### 7.2.3 In-memory sources and sinks

The standard library can expose readers and writers for common in-memory types.

For byte arrays:

```camp
export ByteReader reader(byte[] this);
export AsyncByteReader asyncReader(byte[] this);
export ByteWriter writer(byte[] this);
export AsyncByteWriter asyncWriter(byte[] this);
```

For string views:

```camp
export CharReader reader(StringView this);
export AsyncCharReader asyncReader(StringView this);

export WCharReader reader(WStringView this);
export AsyncWCharReader asyncReader(WStringView this);

export ACharReader reader(AStringView this);
export AsyncACharReader asyncReader(AStringView this);
```

These are useful for:

- testing stream code
- adapting existing in-memory data to helper APIs
- connecting stream algorithms without special cases

### 7.2.4 `FileHandle`

`FileHandle` is the library's full file-handle type. It represents the open handle itself, not the abstract file on disk.

The core API divides file use into two paths:

- stream-first convenience helpers for sequential I/O
- full handle access for seeking, querying length, and mixed read/write operations

```camp
export enum FileAccess
{
	READ,
	WRITE,
	READ_WRITE
}

export enum FileMode
{
	OPEN_EXISTING,
	CREATE,
	CREATE_OR_TRUNCATE,
	APPEND
}

export struct FileOptions
{
	bool sequential;
	bool randomAccess;
	bool writeThrough;
	bool allowSharedRead;
	bool allowSharedWrite;
}

export class FileHandle
{
	static ByteReader openRead(StringView path, FileMode mode = OPEN_EXISTING, FileOptions options = default, thrown IoError);
	static AsyncByteReader openReadAsync(StringView path, FileMode mode = OPEN_EXISTING, FileOptions options = default, thrown IoError);

	static ByteWriter openWrite(StringView path, FileMode mode = CREATE_OR_TRUNCATE, FileOptions options = default, thrown IoError);
	static AsyncByteWriter openWriteAsync(StringView path, FileMode mode = CREATE_OR_TRUNCATE, FileOptions options = default, thrown IoError);

	static FileHandle* open(StringView path, FileAccess access, FileMode mode, FileOptions options = default, thrown IoError);
	static async FileHandle* openAsync(StringView path, FileAccess access, FileMode mode, FileOptions options = default, thrown IoError);

	~FileHandle(thrown IoError);

	bool getOpen();
	bool getReadable();
	bool getWritable();
	bool getEndOfFile();

	ulong getLength(thrown IoError);
	ulong getPosition(thrown IoError);
	void setPosition(ulong value, thrown IoError);

	ByteReader getReader();
	AsyncByteReader getAsyncReader();
	ByteWriter getWriter();
	AsyncByteWriter getAsyncWriter();
}
```

The convenience methods `openRead(...)` and `openWrite(...)` return streams directly because that is the common case for sequential I/O. Callers that need handle-level state use `open(...)` or `openAsync(...)` instead.

### 7.2.5 `Console`

The console is character-oriented by default.

```camp
export class Console
{
	export static extern CharReader getReader();
	export static extern AsyncCharReader getAsyncReader();

	export static extern CharWriter getWriter();
	export static extern AsyncCharWriter getAsyncWriter();

	export static extern CharWriter getError();
	export static extern AsyncCharWriter getAsyncError();

	export static extern thrown(IoError) writeString(StringView value);
	export static extern thrown(IoError) writeLine(StringView value = default);

	export static extern async void writeStringAsync(StringView value, thrown IoError);
	export static extern async void writeLineAsync(StringView value = default, thrown IoError);
}
```

This means:

- `Console.getReader()` and `Console.getWriter()` are `char`-based
- text helpers live directly on `Console`
- error output is a separate writer surface

### 7.2.6 Stream adapters

Byte streams can be adapted into text streams with ordinary getter-style methods.

Reader adapters:

```camp
export CharReader getCharReader(ByteReader this);
export AsyncCharReader getCharReader(AsyncByteReader this);

export WCharReader getWCharReader(ByteReader this);
export AsyncWCharReader getWCharReader(AsyncByteReader this);

export ACharReader getACharReader(ByteReader this);
export AsyncACharReader getACharReader(AsyncByteReader this);
```

Writer adapters:

```camp
export CharWriter getCharWriter(ByteWriter this);
export AsyncCharWriter getCharWriter(AsyncByteWriter this);

export WCharWriter getWCharWriter(ByteWriter this);
export AsyncWCharWriter getWCharWriter(AsyncByteWriter this);

export ACharWriter getACharWriter(ByteWriter this);
export AsyncACharWriter getACharWriter(AsyncByteWriter this);
```

These are intended to work naturally with property syntax:

```camp
reader.CharReader
writer.CharWriter
```

The result is a nominal stream delegate value, which means helper methods can be layered on top without awkward structural-method-group issues.

### 7.2.7 Helper APIs

The helper layer keeps common I/O code compact.

#### Byte helpers

```camp
export thrown(IoError) writeAll(ByteWriter this, const byte[] value);
export async void writeAllAsync(AsyncByteWriter this, const byte[] value, thrown IoError);
```

#### Text-writing helpers

UTF-8 forms:

```camp
export thrown(IoError) writeChar(CharWriter this, uchar value);
export thrown(IoError) writeString(CharWriter this, StringView value);
export thrown(IoError) writeLine(CharWriter this, StringView value = default);

export thrown(IoError) writeBool(CharWriter this, bool value);
export thrown(IoError) writeInt(CharWriter this, int value);
export thrown(IoError) writeLong(CharWriter this, long value);
export thrown(IoError) writeUInt(CharWriter this, uint value);
export thrown(IoError) writeULong(CharWriter this, ulong value);
export thrown(IoError) writeFloat(CharWriter this, float value);
export thrown(IoError) writeDouble(CharWriter this, double value);

export async void writeStringAsync(AsyncCharWriter this, StringView value, thrown IoError);
export async void writeLineAsync(AsyncCharWriter this, StringView value = default, thrown IoError);
```

Equivalent `WChar*` and `AChar*` helper families exist for UTF-16 and ASCII/system-code-page streams.

#### Text-reading helpers

UTF-8 forms:

```camp
export String readAllCopy(CharReader this, within allocator, thrown IoError);
export async String readAllCopyAsync(AsyncCharReader this, within allocator, thrown IoError);

export iter nuint readLines(CharReader this, char[] buffer, thrown IoError);
export async iter nuint readLinesAsync(AsyncCharReader this, char[] buffer, thrown IoError);

export iter StringView iterateLines(CharReader this, char[] buffer, thrown IoError);
export async iter StringView? iterateLinesAsync(AsyncCharReader this, char[] buffer, thrown IoError);
```

Equivalent `WChar*` and `AChar*` reading helper families exist for UTF-16 and ASCII/system-code-page streams.

The distinction between the two line-reading styles is intentional:

- `readLines(...)` yields counts and stays closer to the core protocol
- `iterateLines(...)` yields line views directly and is more ergonomic

### 7.2.8 Example patterns

#### Reading a binary file

```camp
using Std::IO;

void dumpBytes(StringView path, thrown IoError)
{
	auto reader = FileHandle.openRead(path) finally delete;
	byte[] buffer = init byte[4096];

	foreach (nuint count in reader(buffer))
	{
		processBytes(buffer.slice(0, count));
	}
}
```

Async form:

```camp
using Std::IO;

async void dumpBytesAsync(StringView path, thrown IoError)
{
	auto reader = FileHandle.openReadAsync(path) finally delete;
	auto buffer = new byte[4096] finally delete;

	await foreach (nuint count in reader(buffer))
	{
		processBytes(buffer.slice(0, count));
	}
}
```

#### Writing a binary file

```camp
using Std::IO;

void writeData(StringView path, const byte[] data, thrown IoError)
{
	auto writer = FileHandle.openWrite(path) finally delete;
	writer.writeAll(data);
}
```

Manual form without helpers:

```camp
using Std::IO;

void writeDataManually(StringView path, const byte[] data, thrown IoError)
{
	auto writer = FileHandle.openWrite(path) finally delete;
	auto remaining = data;

	foreach (nuint written in writer(remaining))
	{
		remaining = remaining.slice(written);

		if (remaining.length == 0)
			break;
	}
}
```

#### Reading a text file

```camp
using Std::IO;

void printTextFile(StringView path, thrown IoError)
{
	auto reader = FileHandle.openRead(path) finally delete;
	char[] lineBuffer = init char[512];

	foreach (StringView line in reader.CharReader.iterateLines(lineBuffer))
	{
		if (line.startsWith("#"))
			continue;

		Console.writeLine(line);
	}
}
```

#### Writing a text file

```camp
using Std::IO;

void writeTextFile(StringView path, thrown IoError)
{
	auto writer = FileHandle.openWrite(path) finally delete;

	writer.CharWriter.writeLine("hello, world");
	writer.CharWriter.writeString("The answer is ");
	writer.CharWriter.writeInt(42);
	writer.CharWriter.writeLine();
}
```

#### Reading lines from the console

```camp
using Std::IO;

void repl()
{
	char[] lineBuffer = init char[256];

	foreach (StringView line in Console.getReader().iterateLines(lineBuffer))
	{
		if (line == "quit")
			break;

		Console.writeLine("you typed:");
		Console.writeLine(line);
	}
}
```

### 7.2.9 What the stream contract does not include

The core stream contract does not include a required `flush()` operation.

If a concrete type later needs an explicit flush step because it maintains its own buffered state, that operation should live on the concrete type rather than on the core reader or writer delegate families.

This keeps the core stream shape small and predictable.
