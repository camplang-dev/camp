
# 1. Basic Types

Camp keeps its core types small, explicit, and ABI-visible.

This section defines the type forms that appear everywhere else in the language:

- primitive scalar types
- pointer types
- plain function types
- ordinary function declarations and calls
- compiler-expanded forms such as span arrays, optionals, and delegates
- fixed-size array storage
- multi-output binding through `out`, async completion, and iterator protocols
- primitive string types and character-array text views
- enum types
- `newtype`
- the conversion rules that connect these forms

The design goal is not to make types magical. The design goal is to make them predictable. A Camp type should tell the reader what kind of value exists, how that value travels through calls, and what shape the value has at the ABI boundary.

Several later language features build directly on the material in this section. In particular:

- span arrays, optionals, and delegates define the small closed set of compiler-expanded value forms
- fixed-size arrays define explicit inline storage with array-like views
- `fn`, `delegate`, and ordinary function declarations explain Camp's callable surface before async, iterators, and lambdas add further structure
- multi-output binding explains how several caller-provided result slots can be consumed ergonomically without creating a general tuple or user-defined expansion system
- strings explain the distinction between zero-terminated text pointers and counted character arrays

Camp favors explicit surface distinctions where those distinctions help prevent mistakes:

- a zero-terminated string pointer and a counted character array are different types
- a span array and fixed-size array storage are different types
- `newtype` introduces a real nominal boundary even when the machine representation stays the same
- compiler-expanded forms expose visible dot-access components while preserving ABI component symbols
- `struct(...)` materializes an expanded form when one-address storage is required
- `in` changes transport, not source-level meaning
- `out` is explicit caller-provided result storage, not a hidden reference category

That explicitness keeps the language compatible with the C ABI without forcing programmers to work at raw C's level of ceremony.

## 1.1 Primitive Types

Primitive types are the indivisible built-in types of the language.

Camp defines a fixed set of primitive names. Their meaning does not drift by platform. That stability is important for header generation, ABI predictability, foreign interop, and low-level work on small or unusual targets.

### 1.1.1 Primitive overview

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
| `string` | zero-terminated UTF-8 string pointer to const data |
| `wstring` | zero-terminated UTF-16 string pointer to const data |
| `astring` | zero-terminated ASCII or system-code-page string pointer to const data |
| `untyped` | opaque pointee for `untyped*` |
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
bool has(const char[] name);
bool remove(const char[] name);
```

Like the other scalar primitives, `bool` participates directly in delegates, arrays, optionals, and materialized storage forms.

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
void logLine(const char[] text);
delegate void(const char[] text) logger;
```

`void` is not a storable payload type in the ordinary sense. It exists to describe absence of a result.

### 1.1.10 Primitive defaults

Many later features depend on the default value of a type. Camp’s ordinary success-code convention, optional values, and async iteration all rely on this idea.

The important practical defaults in this section are:

- numeric zero for numeric primitives
- `false` for `bool`
- `null` for pointer-like values
- the type’s ordinary all-default form for aggregate, array, optional, and delegate storage

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
byte[32]* block;
void* raw;
```

The language does not blur the distinction between a value and a pointer to that value.

That matters especially for object types later in the language. A class instance is not an implicit reference. A pointer to a class instance is still written explicitly with `*`. The same syntax is used uniformly for all pointed-to types.

### 1.2.1 Ordinary pointer meaning

For an ordinary non-expanded form `T`, `T*` means a pointer to one `T`.

Examples:

```camp
int* valuePtr;
byte* buffer;
Window* dialog;
byte[32]* block;
void* nativeHandle;
```

This is the familiar C-style meaning.

A pointer to a fixed-size array is written by applying `*` to the fixed-array type:

```camp
byte[32]* block;
```

This means a pointer to one fixed array object containing 32 bytes. It is distinct from `byte*` and from `byte[]`.

### 1.2.2 `void*`

`void*` remains useful as an untyped pointer form for low-level interop and machine-level state.

Examples from the design space include:

```camp
newtype NativeBufferPtr: void*;
newtype PluginContext: void*;
newtype NativeState: void*;
```

Camp keeps `void*` available because foreign APIs, opaque callbacks, and low-level allocators still need it.

`untyped*` is a more general opaque pointer-like form used when code must carry
either data pointers or function pointers without choosing one family. Ordinary
object pointers and function pointers may convert to `untyped*`; converting
back to a specific pointer form requires an explicit cast.

### 1.2.3 Pointer explicitness

Camp does not use hidden object-reference syntax. If code is passing, storing, or returning an address, that address is spelled as a pointer.

```camp
Window local = init Window(640, 480);
Window* alias = &local;
```

This explicitness keeps value semantics and reference semantics visually separate.

### 1.2.4 Pointers and compiler-expanded forms

For an ordinary type `T`, `T*` means a pointer to one `T`.

Compiler-expanded forms such as arrays, optionals, and delegates do not themselves imply one aggregate address in parameter expansion. For example, a parameter written as `byte[] data` lowers as separate component parameters rather than as a pointer to an anonymous array object.

When a real one-address storage object is required, Camp uses `struct(T)` for the expanded form:

```camp
struct(byte[]) stored;
struct(int?) maybeCount;
```

A pointer to that storage is written in the ordinary way:

```camp
struct(byte[])* storedPtr;
```

This keeps pointer syntax ordinary while making expansion and materialization explicit.

Fixed-size arrays are ordinary fixed storage forms for pointer purposes. A type such as `T[n]*` points at one fixed-size array object. Indexing that pointer indexes fixed-size array objects, not elements:

```camp
int[8]* values;

// values[0] has type int[8]
int first = (*values)[0];
int same = values[0][0];
```

A pointer to a fixed-size array must be explicitly dereferenced before the fixed array's own indexing, slicing, `.elements`, `.length`, or span conversion is used.

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
delegate void(const char[] text)
once void()
iter int()
async int(int left, int right)
async iter char[]?()
```

The plain `fn` form is just a function signature. It does not carry closure state and it is not a expanded value.

The other callable forms are blessed structural types. Matching shape alone is not enough to participate in delegate, iterator, or async semantics. When code constructs a matching shape without using the keyworded form, an explicit cast such as `(delegate)`, `(iter)`, or `(async)` blesses it.

Context-carrying callable signatures may spell an explicit `this` parameter first in order to qualify the hidden context parameter. The explicit `this` parameter is not an ordinary callable argument. It describes the call context that is supplied by a bound receiver, closure, iterator frame, async frame, or other context-bearing callable value.

Examples:

```camp
delegate void(scoped this, const char[] text)
delegate nuint(const this, char[] buffer)
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

The qualifiers on an explicit callable `this` parameter are part of the callable type. When a bound receiver method reference is converted to a context-carrying callable type, those qualifiers are enforced on the referenced method and on the receiver expression used as the callable context. For example, a target callable with `const this` requires a method callable on a const receiver, and a target callable with `escaped this` requires the bound receiver context to satisfy the escaped receiver requirement.

Lifetime annotations written on a context-carrying callable value apply to the hidden context pointer. An explicit callable `this` parameter is the signature-local way to describe that hidden context's qualifiers.

### 1.3.1 `fn` versus context-carrying callables

This distinction is central:

| Form | Meaning |
|---|---|
| `fn` | plain callable target |
| `delegate` / `once` / `iter` / `async` / `async iter` | callable value with compiler-recognized semantics |

A context-carrying callable can represent:

- a lambda with captured state
- a bound instance method
- an escaped copied closure
- a scoped closure that uses surrounding state directly
- an iterator frame
- an async frame

A plain `fn` does not carry that context.

That is why interface vtable entries are modeled conceptually with `fn` targets that take an explicit interface-instance slot parameter:

```camp
struct IRef
{
	@mustinit fn void(IRef** ctx) retain;
	@mustinit fn void(IRef** ctx) release;
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

Camp function declarations are ordinary, explicit signatures. A function name
usually refers to one callable declaration. When a declaration intentionally
participates in an overload family, Camp makes that fact visible on the
distinguishing parameter with the `overload` keyword.

This keeps calls easy to read, easy to lower, and easy to export while still
allowing library surfaces such as `write(int)`, `write(string)`, and
`write(const char[])` to share a programmer-facing name.

### 1.4.1 Basic declaration form

An ordinary function declaration has the familiar shape:

```camp
ReturnType Name(ParameterList)
{
	...
}
```

A declaration may also ascribe a named callable `newtype` after the parameter list:

```camp
ReturnType Name(ParameterList) : CallableNewtype
{
	...
}
```

The same ascription position is used for declarations without bodies and expression-bodied declarations:

```camp
extern ReturnType Name(ParameterList) : CallableNewtype;
ReturnType Name(ParameterList) : CallableNewtype => expression;
```

Examples:

```camp
int add(int left, int right)
{
	return left + right;
}

void printLine(const char[] text)
{
	Console.writeLine(text);
}
```

Camp's syntax is intentionally close to C and C# here. The important differences emerge from the type system and ABI model, not from decorative declaration syntax.

### 1.4.2 Parameters are explicit

Each parameter has:

- a type
- a name
- optionally a default value
- optionally a transport or calling modifier such as `in` or `out`
- optionally `overload`, when that parameter distinguishes an overload family

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
bool has(const char[] name);
nuint count();
char[] getText();
void clear();
```

When a routine needs to produce several result values, it uses `out` parameters. Call-site deconstruction can bind those result slots without introducing a general tuple value.

### 1.4.4 Overload parameters

Camp supports a narrow overload model. A parameter marked `overload` is part of
the declaration's public call name and flattened symbol name. The overload set is
selected from the argument type supplied for that parameter.

```camp
void write(overload int value);
void write(overload string value);
void write(overload const char[] value);
```

The keyword belongs on the parameter, not on the function. This makes the
overload discriminator explicit at the declaration site.

Overloaded functions still lower to distinct ABI symbols. For receiver methods,
the receiver type remains part of the symbol as usual, and the overloaded
parameter contributes its type name:

```camp
export extern void write(CharWriter this, overload int value, thrown IoError error);
export extern void write(CharWriter this, overload const char[] value, thrown IoError error);
```

These declarations produce symbols such as `CharWriter_writeInt` and
`CharWriter_writeCharArray`, while Camp callers can write:

```camp
writer.write(123);
writer.write("hello");
```

If a string argument is used and there is no exact `string`, `wstring`, or
`astring` overload, Camp may select the corresponding character-array overload:
`const char[]`, `const wchar[]`, or `const achar[]`. This follows the same
one-way string-to-span conversion used by ordinary calls.

Overload resolution is intentionally smaller than in C++ or C#. It is based on
the explicit overload parameter and Camp's ordinary conversion rules. APIs that
need optional behavior should still prefer:

- default arguments
- options objects
- distinct method names
- explicit result or parameter structs

### 1.4.5 Default arguments

Parameters may have default values:

```camp
void logLine(const char[] text = default)
{
	...
}

void connect(const char[] host, ushort port = 80)
{
	...
}
```

Default arguments keep the surface compact without introducing constructor-like overload families or ad hoc helper wrappers.

Default values are inserted according to the static signature used by the call. When a callable value has a named callable `newtype`, defaults declared on that callable `newtype` are used for calls through that callable value. Defaults declared on the original function or method are used for direct calls to that function or method. The two signatures do not need to declare the same defaults.

### 1.4.6 Calls

An ordinary call uses positional arguments:

```camp
int sum = add(3, 4);
printLine("hello");
```

Calls are matched left to right. When the target name refers to an overload
family, the overload parameter is used to select the declaration before ordinary
argument validation continues.

### 1.4.7 Named arguments

Camp also supports named arguments.

Example:

```camp
void setSize(int width, int height);

setSize(width: 640, height: 480);
```

Named arguments are especially useful when:

- later parameters have default values
- multiple parameters have the same primitive type
- a call supplies explicit `out` or `catch` storage
- a parameter is a compiler-expanded form whose component names are visible in the ABI

### 1.4.8 Mixing positional and named arguments

Positional and named arguments may be mixed, but no parameter or expanded component may be supplied more than once.

Example:

```camp
void fill(byte[] buffer, byte value);

fill(buffer: data, buffer_length: 100, value: 0); // OK
fill(data, buffer_length: 100, value: 0);         // ERROR: `buffer_length` already supplied by `data`
```

Camp rejects duplicate supply rather than guessing what the programmer intended.

### 1.4.9 Compiler-expanded parameters

Camp has a small closed set of compiler-expanded forms:

| Source form | Source component access | Parameter expansion |
|---|---|---|
| `T[] name` | `name.elements`, `name.length` | `T* name, nuint name_length` |
| `T? name` | `name.value`, `name.specified` | `T name, bool name_specified` |
| `delegate R(...) name` | `name.call`, `name.context` | call target, context pointer |
| `once R(...) name` | `name.call`, `name.context` | call target, context pointer |
| `async R(...) name` | `name.call`, `name.context` | call target, context pointer |

The first ABI component keeps the declared parameter name. Additional ABI components use the declared name plus a fixed suffix.

Example:

```camp
void write(byte[] data);
```

has the ABI-facing shape:

```camp
void write(byte* data, nuint data_length);
```

A delegate parameter keeps the callable target under the declared name:

```camp
void filter(delegate bool(int value) predicate);
```

has a call target named `predicate`, a context component named `predicate_context`, and source component access through `predicate.call` and `predicate.context`. The call target receives the context as its first argument.

These expansions are compiler-defined. User code does not declare new expanded forms.

### 1.4.10 Expanded component symbols

An expanded binding introduces all of its ABI component symbols into the containing scope, and exposes those components through member-access syntax.

```camp
void f(byte[] arr, int? opt, delegate void() del)
{
	log(arr.length);

	if (opt.specified)
		log(opt.value);

	del.call(del.context);
}
```

The introduced ABI component symbols are ordinary bindings for redeclaration purposes:

```camp
void f(byte[] arr)
{
	nuint arr_length = 0; // ERROR: `arr_length` is already introduced by `arr`
}
```

This rule is part of the source language, not merely a C backend detail. The ABI component names are predictable, visible, and ABI-aligned.

Component access uses `.`. The underscore names remain the ABI component symbols and remain in scope for collision and named-argument purposes.

Fixed-size arrays also expose `.elements` and `.length` through member access, but they are not compiler-expanded values and do not introduce additional ABI component names into the containing scope.

### 1.4.11 `in` parameters

`in` is a transport feature.

An `in T` parameter:

- behaves like a by-value parameter in source
- is passed as a hidden pointer in the ABI
- does not expose pointer mechanics at the call site
- does not by itself change the lifetime of the logical value

An `in T` parameter is read-only within the callee. Direct mutation of the parameter is invalid, and taking its address produces `const T*`. ABI lowering passes the parameter as a hidden pointer to const storage.

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
Point p = { .x = 10, .y = 20 };
printPoint(p);
```

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

Trailing `out` parameters may also be consumed through deconstruction at the call site.

### 1.4.14 Deconstructing trailing `out` values

If a call omits trailing `out` parameters and the call expression is immediately bound through deconstruction, the omitted result slots are bound to the declared locals.

```camp
void getPoint(out int x, out int y)
{
	...
}

auto (x, y) = getPoint();
```

This lowers directly to caller-provided storage:

```camp
int x;
int y;
getPoint(out x, out y);
```

The omitted values do not form a general-purpose anonymous value. They may be bound by immediate deconstruction, returned through an async or iterator protocol that explicitly supports multiple output slots, or handled by explicit `out` arguments.

If only one trailing `out` parameter is omitted, the same rule may bind a single local:

```camp
void getCount(out int count);

auto count = getCount();
```

A `thrown` parameter is not an `out` parameter and does not participate in deconstruction.

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

Camp's ordinary convention is:

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

When `thrown(E)` is the return type, `catch` still appears in the argument list. In that call form, the thrown return is handled as though it were an additional final argument, while omitted trailing `out` values still follow the ordinary result-binding rule.

A `thrown` parameter is not an `out` parameter.

### 1.4.16 Callable `newtype` ascription

A function-like declaration may ascribe one named callable `newtype` after its parameter list. The ascription gives the declaration's natural callable reference form a nominal callable type.

```camp
newtype fn bool IntParser(const char[] text, out int value);

bool tryParseInt(const char[] text, out int value) : IntParser
{
	...
}
```

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

struct Date
{
	nuint format(char[] buffer = default) : CharFormatter
	{
		...
	}
}
```

The target of the ascription must resolve to a named callable `newtype`. It may not be an anonymous callable type, a value `newtype`, a primitive type, an aggregate type, an interface, an enum, an array, an optional, a materialized `struct(...)` form, or any other non-callable type. A concrete declaration has at most one callable ascription.

Callable ascription is checked against the declaration's source-level callable reference family. A receiverless declaration may ascribe only a callable `newtype` whose underlying form is `fn`. Receiverless declarations are free functions and static methods.

```camp
newtype fn void LineWriter(const char[] text);

class Console
{
	static void writeLine(const char[] text) : LineWriter
	{
		...
	}
}
```

A receiver-bearing declaration may ascribe only a context-carrying callable `newtype` accepted by callable ascription. In this version of Camp, those accepted context-carrying forms are `delegate` and `iter`. Receiver-bearing declarations include instance methods declared inside a type body, instance methods with an explicit in-scope `this` parameter, and out-of-scope receiver methods whose first parameter is named exactly `this`.

```camp
newtype iter char CharReader();

struct TextView
{
	iter char chars() : CharReader
	{
		...
	}
}
```

```camp
struct Date
{
	int day;
}

nuint format(const Date* this, char[] buffer = default) : CharFormatter
{
	...
}
```

These declaration-family rules are enforced at the declaration site:

```camp
newtype delegate bool Parser(const char[] text, out int value);
bool tryParseInt(const char[] text, out int value) : Parser { ... } // ERROR
```

```camp
newtype fn nuint DateFormatFn(char[] buffer = default);
struct DateFormatExample
{
	nuint format(char[] buffer = default) : DateFormatFn { ... } // ERROR
}
```

The callable forms `once`, `async`, and `async iter` remain valid callable forms and valid callable `newtype` underlyings, but they are not accepted as callable-ascription targets in this version.

When the ascribed context-carrying callable `newtype` has an explicit callable `this` parameter, its qualifiers are part of the ascribed method contract. If the method omits an explicit `this` parameter, those callable `this` qualifiers become the method receiver's effective qualifiers for declaration checking and body analysis.

```camp
newtype delegate nuint ConstFormatter(const this, char[] buffer = default);

struct Date
{
	int year;

	nuint format(char[] buffer = default) : ConstFormatter
	{
		return (nuint)this.year;
	}
}
```

In the example above, `format` is analyzed as a const receiver method because `ConstFormatter` declares `const this`. The implementation is not required to repeat `const this` in the method declaration.

If an ascribed method declares an explicit `this` parameter and the ascribed callable `newtype` also declares an explicit callable `this` parameter, the normalized qualifier sets must match.

```camp
struct Date
{
	nuint format(const this, char[] buffer = default) : ConstFormatter
	{
		return 0;
	}
}
```

```camp
struct Date
{
	nuint format(escaped this, char[] buffer = default) : ConstFormatter // ERROR
	{
		return 0;
	}
}
```

An out-of-scope receiver method follows the same rule through its explicit receiver parameter. For example, a method ascribed to `ConstFormatter` declares a const receiver:

```camp
nuint format(const Date* this, char[] buffer = default) : ConstFormatter
{
	return 0;
}
```

If the ascribed callable `newtype` has no explicit callable `this` parameter, the method may still declare its own explicit receiver qualifiers. Those qualifiers apply to the method itself, but they are not part of the nominal callable contract unless the callable `newtype` declares them.

An `escaped class` defaults instance receivers to `escaped this`. For an instance method in an `escaped class` that ascribes a context-carrying callable `newtype`, the escaped receiver requirement must be explicit either on the callable `newtype`'s callable `this` parameter or on the method's explicit `this` parameter.

```camp
newtype delegate void EscapedCallback(escaped this);

escaped class Service
{
	void run() : EscapedCallback
	{
	}
}
```

```camp
newtype delegate void Callback();

escaped class Service
{
	void run(escaped this) : Callback
	{
	}
}
```

```camp
escaped class Service
{
	void run() : Callback // ERROR
	{
	}
}
```

The last declaration is invalid because the method's `escaped this` default would otherwise be lost from the ascribed callable contract without appearing explicitly on either the callable `newtype` or the method declaration. If an `escaped class` method ascribes a callable `newtype` whose explicit callable `this` parameter does not include `escaped`, omitting the method's explicit `escaped this` is invalid; explicitly writing a different receiver qualifier is also invalid when it conflicts with the callable `this` qualifier matching rule.

The conformance check uses ordinary callable compatibility rules. For a receiverless declaration, the check is the same check that would be performed for assigning the function reference to the ascribed `fn` newtype:

```camp
IntParser parser = tryParseInt;
```

For a receiver-bearing declaration, the check is the same check that would be performed for assigning a bound method reference to the ascribed context-carrying newtype:

```camp
const Date date = ...;
CharFormatter formatter = date.format;
```

For receiver-bearing methods, the receiver is not part of the ordinary callable argument list. The receiver is carried by the callable context. Explicit callable `this` qualifiers and lifetime annotations participate in compatibility checking through the same hidden-context rules used by ordinary bound method references. If the target callable `newtype` requires `const this`, a mutable-only method is not compatible. If it requires `escaped this`, the receiver expression used to form the bound method reference must satisfy the escaped receiver requirement.

Callable ascription does not generate wrappers, adapter functions, thunks, allocation, closure objects, or null contexts merely to make a declaration fit a target type. If the declaration's natural callable reference form is not compatible with the ascribed callable `newtype`, the declaration is invalid.

Ascription affects inference for the matching natural callable reference form.

```camp
auto parser = tryParseInt; // IntParser
auto format = date.format; // CharFormatter
```

For a receiver-bearing method, the ascription applies to the bound method reference. Unbound method references and canonical flattened function symbols retain their ordinary anonymous `fn` type.

```camp
auto a = date.format; // CharFormatter
auto b = Date.format; // anonymous fn
auto c = Date_format; // anonymous fn
```

Direct calls remain ordinary calls to the declared function or method. Callable ascription does not alter direct invocation, overload resolution, virtual dispatch, interface conformance, default-argument insertion, ABI representation, generated C symbols, or callable lowering.

```camp
date.format(buffer); // ordinary direct method call
```

Defaults follow the static signature being called. A callable `newtype` and the ascribed function or method may declare different defaults, or only one of the two may declare a default.

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

struct Date
{
	nuint format(char[] buffer) : CharFormatter
	{
		return 1;
	}
}

void sample(Date date)
{
	auto formatter = date.format;
	auto needed = formatter(); // OK: `CharFormatter` supplies the default
	date.format();             // ERROR: the direct method signature has no default
}
```

Because the matching reference form has the nominal callable type, ordinary receiver-method lookup also sees methods whose receiver is that callable `newtype`.

```camp
string copyString(CharFormatter this, within allocator)
{
	auto buf = new char[this()];
	this(buf);
	return buf.elements;
}

string text = date.format.copyString() finally delete;
```

Callable ascription belongs to one concrete declaration. It does not make overload groups convertible to callable values and does not select an overload from a group.

### 1.4.17 Functions, methods, and callable values

This section defines ordinary function declarations and calls.

Later sections add:

- method-like invocation
- delegates and lambdas
- iterators
- async functions

But those later surfaces still build on the ordinary signature model defined here. Camp does not introduce a disconnected callable subsystem for each feature.

## 1.5 Compiler-Expanded Forms

Camp has three compiler-expanded value forms:

- arrays
- optionals
- delegates and delegate-like callable values

These forms have multiple ABI-visible components, but they are not user-defined tuple or expanded forms. The language defines their component names, construction rules, materialized storage forms, and composition limits directly.

User code does not declare new expanded forms.

Earlier drafts experimented with user-defined `params` declarations and
`params(T)` type syntax. Those forms are no longer part of Camp; use the closed
expanded forms above, or `struct(T)` when one-address materialized storage is
needed.

### 1.5.1 Core idea

A compiler-expanded value is a source-level value with a fixed set of source components and ABI component symbols.

For example:

```camp
byte[] data;
int? count;
delegate void() callback;
```

provide source component access like:

```camp
data.elements
data.length

count.value
count.specified

callback.call
callback.context
```

The first ABI component keeps the declared name. Additional ABI components use fixed suffixes:

```camp
data
data_length

count
count_specified

callback
callback_context
```

Assignment between matching expanded values copies all components. Individual components may also be assigned through member-access syntax.

Expanded values are not hidden structs. Component access uses `.` only for the compiler-defined components of the expanded form.

### 1.5.2 `struct(T)` and materialized storage

`struct(T)` materializes a compiler-expanded form into ordinary one-address storage.

Examples:

```camp
struct(byte[]) storedBytes;
struct(int?) storedOptional;
struct(delegate void(int)) storedCallback;
```

The fields of the materialized storage form match the logical component names of the expanded form:

```camp
struct(byte[])
{
	byte* elements;
	nuint length;
}

struct(int?)
{
	int value;
	bool specified;
}
```

For delegate-like forms, the materialized fields are `call` and `context`.

A value of an expanded form may be materialized explicitly:

```camp
auto stored = (struct)items;
```

and materialized storage may be expanded when assigned or passed to the matching expanded form:

```camp
byte[] view = stored;
```

`struct(...)` forms are allowed in fields, locals, arrays, and other ordinary storage positions. They are not used as expanded parameters.

### 1.5.3 `sizeof` for expanded forms

`sizeof(T)` is defined for compiler-expanded forms as the size of their materialized storage form.

```camp
sizeof(byte[]) == sizeof(struct(byte[]))
sizeof(int?) == sizeof(struct(int?))
```

This rule is especially important in erased generic code, where storage and transport use materialized representations.

### 1.5.4 Composition limits

Direct recursive expansion is restricted.

The following direct compositions are invalid:

```camp
int?[]                         // ERROR
byte[][]                       // ERROR
delegate void()[]              // ERROR
int??                          // ERROR
```

The materialized form is the explicit storage escape hatch:

```camp
struct(int?)[] items;
struct(byte[])[] rows;
struct(delegate void())[] callbacks;
```

Optional arrays and optional delegates are allowed:

```camp
byte[]? maybeBytes;
delegate void()? maybeCallback;
```

They expose the ordinary source components plus the optional presence component:

```camp
maybeBytes.elements
maybeBytes.length
maybeBytes.specified

maybeCallback.call
maybeCallback.context
maybeCallback.specified
```

The ABI symbols remain `maybeBytes`, `maybeBytes_length`, `maybeBytes_specified`, `maybeCallback`, `maybeCallback_context`, and `maybeCallback_specified`.

Optional optionals are invalid.

### 1.5.5 Fixed types and materialization

`struct(T)` for compiler-expanded forms creates ordinary copyable storage for the expanded slots.

Fixed structs and classes are not converted to or from expanded form by `struct(T)`. They already have one storage identity of their own.

### 1.5.6 Fixed-size arrays are not expanded forms

A fixed-size array type `T[n]` is not a compiler-expanded form. It is inline element storage with one storage identity.

Fixed-size arrays expose `.elements` and `.length` for array-like use, but those are synthesized from the storage object. They do not introduce expanded ABI component symbols such as `name_length` for the fixed-array binding itself.

A fixed-size array may convert to the matching span array `T[]`. That conversion synthesizes the span components from the fixed storage.

## 1.6 Arrays

Arrays are built into the language as compiler-expanded forms.

### 1.6.1 Shape

An array type `T[]` has two source components:

- `name.elements` — pointer to the first element
- `name.length` — element count

The corresponding ABI component symbols are `name` and `name_length`.

Conceptually, an array value is a lightweight pointer plus count.

A parameter written as:

```camp
void write(byte[] data);
```

has the ABI-facing shape:

```camp
void write(byte* data, nuint data_length);
```

### 1.6.2 Basic use

```camp
byte[] buffer = new byte[256];
nuint len = buffer.length;
```

Array shapes appear throughout the language and standard library:

- raw binary buffers
- counted text code-unit buffers
- arrays used in generic algorithms
- array-based reader and writer APIs

### 1.6.3 Array allocation

Array values are allocated on the stack or on the heap.

```camp
auto stackBytes = init byte[256];
auto heapBytes = new byte[256];
```

The resulting array value carries the element pointer and length component. Element construction rules for constructor-bearing element types are defined with data structures.

### 1.6.4 Arrays of expanded values

An array element type may not be a non-materialized expanded form.

Invalid:

```camp
int?[] values;
byte[][] rows;
delegate void()[] callbacks;
```

Use the materialized form when each element should store the expanded value as one ordinary element:

```camp
struct(int?)[] values;
struct(byte[])[] rows;
struct(delegate void())[] callbacks;
```

This keeps the array ABI conventional. For example, `string[]` is an ordinary array of zero-terminated string pointers, not an array split into string pointers and string lengths.

### 1.6.5 Arrays are not strings

Arrays and string types are distinct nominal forms with different semantics and method surfaces.

A `char[]` is a counted sequence of UTF-8 code units. A `string` is a zero-terminated UTF-8 string pointer. Both are useful, but they represent different contracts.

### 1.6.6 Array APIs

Array APIs use the expanded component model directly.

Representative examples:

```camp
scoped T[] slice<T: any>(const T[] this, @range nuint index = 0, nuint count = ^0, sizeof(T));
scoped T* addressOf<T: any>(T[] this, @index nuint index, sizeof(T));
T[] copy<T: copyable>(const T[] this, within allocator, sizeof(T));
```

Copy-producing array APIs require copyable element types and `sizeof(T)`. They do not copy fixed-size arrays, fixed structs, or class elements by value.

### 1.6.7 Array indexing and slicing shape

Raw array indexing is expressed with `[]`.

Slicing is expressed through ordinary methods and index-aware parameters rather than through a separate hidden slicing operator category.

This keeps the call surface ordinary while still allowing convenient syntax through method and property rewriting.

### 1.6.8 Fixed-size array storage

A fixed-size array type is written:

```camp
T[n]
```

It denotes inline storage for exactly `n` elements of `T`. The length expression `n` must be a compile-time integer constant whose value is greater than or equal to zero.

`T[n]` is not the same type as `T[]`. `T[]` is a span value with `elements` and `length` components. `T[n]` is fixed inline storage with storage identity.

```camp
byte[] span;
fixed byte[32] buffer;
```

A declaration that creates fixed-size array storage must use `fixed`:

```camp
fixed byte[32] local;

struct Packet
{
	fixed byte[256] payload;
}

export fixed byte[16] GlobalKey;
```

These declarations are invalid because they create fixed-size array storage without the `fixed` marker:

```camp
byte[32] local; // ERROR

struct Packet
{
	byte[256] payload; // ERROR
}
```

`fixed` is not used merely to name a fixed-size array type beneath a pointer:

```camp
byte[32]* block;
byte[32]* getBlock();
void useBlock(byte[32]* block);
```

Direct fixed-size array storage declarations are allowed only for locals, globals, and struct or class fields. A direct fixed-size array value is not an ordinary parameter type, return type, callable parameter slot, callable result slot, or value `newtype` underlying type.

```camp
int sum(int[8] values);          // ERROR
int[8] getValues();              // ERROR
fn int[8]();                     // ERROR
newtype Values: int[8];          // ERROR

int sum(int[8]* values);         // OK
int[8]* getValues();             // OK
fn int[8]*();                    // OK
```

A fixed-size array is a fixed value. It may not be copied, returned, passed, assigned from another fixed-size array, or extracted by value.

```camp
fixed byte[32] a;
fixed byte[32] b;

b = a;             // ERROR
auto copy = a;     // ERROR
```

A fixed-size array may be initialized or overwritten from a target-typed initializer expression, a compatible string literal, or `default`. This writes an initializer pattern into known fixed storage; it is not fixed-array value copying. Too many initializer elements is an error; missing elements default-fill.

```camp
fixed byte[8] bytes = [1, 2, 3];
bytes = [4, 5];     // remaining elements default-fill
bytes = default;    // all elements default-fill

fixed byte[2] small = [1, 2, 3]; // ERROR

fixed char[8] name = "cat";
name = "dog";
```

If the fixed array has const elements, initialization is allowed but later element mutation and whole-array overwrite are invalid.

```camp
fixed const byte[4] magic = [1, 2, 3, 4];
magic[0] = 9;       // ERROR
magic = [4, 3, 2, 1]; // ERROR
```

For character fixed arrays, string literals may target `char[n]`, `wchar[n]`, and `achar[n]`. If the literal exactly fills the destination, no terminator beyond capacity is required. If space remains, remaining elements are zero-filled.

```camp
fixed char[3] a = "cat";   // OK, no extra terminator
fixed char[4] b = "cat";   // OK: c a t 0
fixed char[2] c = "cat";   // ERROR

fixed achar[4] d = "cat";  // OK
fixed wchar[4] e = "cat";  // OK
```

A fixed-size array exposes array-like operations:

```camp
fixed int[8] values;

nuint count = values.length; // 8
int* elements = values.elements;
values[0] = 10;
int first = values[0];
int[] span = values;
int[] prefix = values[0..4];
int[8]* whole = &values;
```

For `T[n]`, `.length` is the compile-time constant `n` exposed as `nuint`. `.elements` is a `T*` pointer to the first element. There is no backing length field.

A fixed-size array converts to `T[]`. The reverse conversion is not implicit:

```camp
fixed byte[32] buffer;
byte[] view = buffer;    // OK

byte[] span;
buffer = span;           // ERROR
```

Fixed-size arrays participate in receiver-method lookup through the `T[]` span view. A method whose receiver is `T[]` is a candidate for a fixed-size array receiver. A method whose receiver is `T*` or `T[n]*` is not selected by instance syntax from a fixed-size array value; use `.elements` or `&value` explicitly.

Nested fixed-size arrays compose normally:

```camp
fixed byte[8][8] matrix;      // 8 by 8 byte matrix
byte[8][8]* matrixPtr;        // pointer to such a matrix
fixed byte[8]*[8] rowPtrs;    // fixed array of pointers to byte[8]
fixed byte*[8][8] ptrMatrix;  // 8 by 8 matrix of byte pointers
```

For nested fixed arrays, indexing may produce another fixed-size array lvalue:

```camp
fixed byte[8][8] matrix;
byte[] row = matrix[0];       // OK: row view
byte[8]* rowPtr = &matrix[0]; // OK
byte first = matrix[0][0];    // OK
fixed byte[8] copy = matrix[0]; // ERROR
```

A span whose element type is a fixed-size array is a span of fixed-size array objects. Its `elements` component is a pointer to fixed-size array storage.

```camp
byte[8][] rows;       // elements: byte[8]*, length: nuint
byte[] first = rows[0];
byte[8]* firstPtr = &rows[0];
rows[0] = rows[1];    // ERROR
rows[0] = [1, 2, 3];  // OK: initializer write into fixed storage
```

`sizeof(T[n])` is valid and equals `n * sizeof(T)`, including padding or representation details required by the target for `T` elements.

```camp
nuint bytes = sizeof(int[8]); // 32
```

`T[0]` is valid only as the final field of a struct. It represents flexible trailing storage. Its `.length` is `0`; a separate field usually stores the runtime count.

```camp
struct Packet
{
	nuint length;
	fixed byte[0] data;
}
```

Fixed-size array storage lowers to inline C array storage where C is the selected backend representation. A pointer to fixed-size array storage lowers to a C pointer-to-array type where the target supports that spelling.

## 1.7 Optional Values

Optional values are built into Camp with the `?` suffix.

### 1.7.1 Core model

A type `T?` is an expanded form derived from `T`. `T` may not be a fixed type, including a fixed-size array type. Use `T*?` when the optional payload should be a nullable pointer to a fixed value.

An optional value exposes:

- `name.value` — the payload slot
- `name.specified` — the presence flag

The corresponding ABI component symbols are `name` and `name_specified`.

For example:

```camp
int? count;
```

has source components `count.value` and `count.specified`, and ABI symbols `count` and `count_specified`.

### 1.7.2 Construction

A value of type `T` implicitly converts to `T?`:

```camp
int? x = 5;
```

The default value of `T?` is the unspecified state:

```camp
int? missing = default;
```

Conceptually:

- `missing.specified == false`
- `missing.value` contains the default payload value for `T`

A default payload may be specified explicitly:

```camp
SomeStruct? x = default(SomeStruct);
```

Here `x.specified` is true and the payload is the default `SomeStruct` value.

### 1.7.3 Access

Presence is checked through the presence component:

```camp
if (x.specified)
{
	log(x.value);
}
```

Reading the payload performs no automatic safety check. If the presence flag is false, the payload slot still contains whatever payload value is stored there.

### 1.7.4 Semantics

Optional values add no hidden semantics beyond their expanded representation.

In particular, Camp does **not** add:

- automatic unwrapping
- flow-sensitive “definitely present” analysis
- null-based special cases
- hidden propagation

There is no implicit conversion from `T?` to `T`.

### 1.7.5 Composition

Optionals compose with ordinary type forms:

```camp
int? value;
string? text;
byte[]? maybeBuffer;
delegate void()? maybeCallback;
```

Direct optional-optionals are invalid:

```camp
int?? nested; // ERROR
```

When code needs an optional as an element, field subobject, or addressable value, it uses the materialized form:

```camp
struct(int?) stored;
struct(int?)[] values;
```

### 1.7.6 Why optionals matter elsewhere

Optionals distinguish “no logical value” from a payload that happens to be the default value of its type.

That is why async iterators use `T?` as the explicit presence channel when `0`, `false`, or `null` must be carried as real data.

## 1.8 Delegates

Delegates are compiler-expanded callable values.

### 1.8.1 Shape

A delegate value consists of:

- `call`
- `context`

For a binding named `del`, the source components are:

```camp
del.call
del.context
```

The corresponding ABI component symbols are `del` and `del_context`. The call target receives the context first.

Conceptually:

```camp
delegate bool(int value) del;
```

has the raw component invocation shape:

```camp
del.call(del.context, value);
```

### 1.8.2 Using delegate types

A delegate type may be written anywhere a type is needed:

```camp
delegate void(const char[] text) logger;
delegate bool(int value) predicate;
delegate int(int left, int right) comparer;
```

Lifetime annotations on a delegate value describe the lifetime of the hidden context pointer.

For example:

- `escaped delegate bool(int value)` means the delegate context is non-stack
- `scoped(owner) delegate void(Node* owner)` means the delegate context will not outlive `owner`

Delegate signatures may also spell an explicit `this` parameter first in order to qualify the hidden context parameter:

```camp
delegate void(scoped this, const char[] text) logger;
delegate nuint(const this, char[] buffer) formatter;
```

Those qualifiers are part of the delegate type and are enforced when a bound method reference is converted to the delegate type. Later sections define lambdas, method references, `once`, `iter`, and `async`. This section only defines the delegate value model itself.

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

Ordinary code calls the delegate value directly. The component form describes its ABI and raw binding shape.

### 1.8.4 What delegates are for

Delegates cover several cases with one uniform model:

- lambdas
- bound instance methods
- callbacks with context
- postponed operations
- escaped copied closures
- scoped closures that use surrounding state directly

Camp does not need a separate runtime closure object category for each of these cases. The expanded delegate model is enough.

### 1.8.5 Delegates versus plain `fn`

Use `fn` for a plain callable target with no context.

Use `delegate` when code needs:

- captured state
- a bound receiver
- a call-plus-context ABI shape
- a first-class callback surface that may carry data with it

### 1.8.6 Deletion

Delegates are not deletable by default.

A delegate value does not imply ownership of some magical closure object. If the target or captured state needs destruction, that ownership belongs to the actual storage or object model involved, not to the delegate type as such.

## 1.9 Strings and Counted Text

Camp has two basic text representations:

- zero-terminated string pointer types
- counted character arrays

The string pointer types are compiler-blessed primitive keywords. Counted text is represented with ordinary character arrays.

### 1.9.1 Core string types

The core string types are primitive keywords:

```camp
string
wstring
astring
```

| Type | Meaning |
|---|---|
| `string` | zero-terminated UTF-8 string pointer to const data |
| `wstring` | zero-terminated UTF-16 string pointer to const data |
| `astring` | zero-terminated ASCII or system-code-page string pointer to const data |

The primitive string types are pointer-shaped, but they are not mutable string
buffers. A `string` has the same ABI representation as `const char*`, but the
type carries the additional contract that the pointed-to sequence is
zero-terminated. A plain `char*` or `const char*` does not carry that contract.
Passing a raw pointer where `string` is expected requires an explicit cast.

There is no implicit conversion between `string`, `wstring`, and `astring`. Converting between text families requires an explicit library operation that performs the required transcoding or validation.

### 1.9.2 Counted text

Counted text uses arrays:

| Type | Meaning |
|---|---|
| `char[]` | counted UTF-8 code-unit sequence |
| `wchar[]` | counted UTF-16 code-unit sequence |
| `achar[]` | counted ASCII or system-code-page code-unit sequence |

A counted array is the appropriate representation when code needs a known length, slicing, embedded zero code units, or bounded processing.

### 1.9.3 Null termination and length

The core string types are zero-terminated and do not carry a length component.

A string length property such as `str.Length` calls a method such as `getLength()` and computes the length by scanning for the terminator.

A character array length is a direct expanded component:

```camp
string str = "hello";
nuint a = str.Length;      // computed

const char[] view = str.asArray();
nuint b = view.length;     // direct count
```

### 1.9.4 Ownership

The `string`, `wstring`, and `astring` type names describe zero-terminated text representation. They do not by themselves describe ownership.

An API that allocates a string must document how that string is released. An API that borrows a string must document the lifetime of the pointed-to text through ordinary lifetime annotations.

### 1.9.5 string literals

A string literal is constant data. Without a target type, its inferred type is
`string`.

```camp
string s = "hello";
```

A string literal may also target any of these destination types:

- `string`
- `wstring`
- `astring`
- `const char[]`
- `const wchar[]`
- `const achar[]`
- `const char*`
- `const wchar*`
- `const achar*`
- fixed `char[n]`
- fixed `wchar[n]`
- fixed `achar[n]`

Examples:

```camp
string s = "hello";
wstring ws = "hello";
const char[] view = "hello";
const char* raw = "hello";
fixed char[8] buffer = "hello";
```

The corresponding mutable character-pointer and mutable span-array forms
are not valid literal destinations:

```camp
char[] view = "hello"; // ERROR: string literals are constant
char* raw = "hello";   // ERROR: string literals are constant
```

When a literal targets a string type or pointer type, the result is a zero-terminated pointer. When it targets a character array, the result is a counted array view of the literal's code units. When it targets a compatible fixed-size character array, the literal's code units are copied into that fixed storage and any remaining elements are zero-filled.

### 1.9.6 string methods and array methods

string methods operate on zero-terminated text. Counted text methods operate on character arrays.

Representative examples:

```camp
nuint getLength(string this);
scoped char[] asArray(string this);

nuint countChars(const char[] this);
uchar getChar(const char[] this, @index nuint unit);
scoped char[] slice(const char[] this, @range nuint index = 0, nuint count = ^0);
string stringCopy(const char[] this, within allocator);
```

The same pattern applies to `wstring` / `wchar[]` and `astring` / `achar[]`.

Camp also has a compiler-sponsored one-way view conversion from each string
primitive to its counted const character-array form:

```camp
string text = "hello";
const char[] span = text;      // scans text.Length
```

The compiler finds the appropriate in-scope length property or `getLength`
method for the string family. The reverse direction is not implicit because it
would require proving or creating a zero terminator.

### 1.9.7 Arrays of strings

Because strings are pointer-shaped primitive values, arrays of strings have the ordinary array-of-pointers shape:

```camp
string[] names;
```

lowers as an element pointer plus an array length, not as separate arrays of string pointers and string lengths.

### 1.9.8 Text families and ordinary usage

Camp keeps the three text families parallel:

| Family | string type | Counted type | Typical use |
|---|---|---|---|
| UTF-8 | `string` | `char[]` | general text, source-facing APIs, most common default |
| UTF-16 | `wstring` | `wchar[]` | UTF-16-oriented foreign APIs or host environments |
| ASCII / code page | `astring` | `achar[]` | legacy APIs, code-page text, explicitly narrow text surfaces |

Typical usage looks like this:

```camp
void writeUtf8(const char[] text);
void writeUtf16(const wchar[] text);
void writeLegacy(const achar[] text);
void callForeign(string text);
```

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

That aligns naturally with Camp's ordinary success-code convention, where the default value of the status type means success.

### 1.10.4 Enums in APIs

Enums appear naturally in signatures, structs, and expanded values:

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

Camp's error-handling model does not require every enum to be a status enum, but when an enum *is* used in that role, aligning success with the default value is the natural fit.

### 1.10.6 What this section does not assume

The current source set uses enums heavily, but does not yet give them a dedicated standalone spec page. Accordingly, this section defines only what is already stable and visible in the source material:

- enums are nominal named sets of constants
- members may have explicit values
- enums are used directly in ordinary type positions
- status enums commonly align `OK = 0` with Camp's ordinary success convention

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
- requires explicit casts to cross the boundary unless a specific built-in rule says otherwise
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
newtype Values: int[];      // ERROR
newtype Bytes: byte[8];     // ERROR
newtype MaybeId: int?;      // ERROR
newtype Client: HttpClient; // ERROR
```

Callable `newtype` underlyings use the ordinary callable forms. Context-carrying callable `newtype`s may declare an explicit callable `this` parameter to qualify the hidden context parameter:

```camp
newtype fn int Parser(const char[] text);
newtype delegate bool Predicate(int value);
newtype delegate nuint CharFormatter(const this, char[] buffer);
newtype iter char CharReader(const this);
newtype once void Completion(escaped this, int result);
newtype async int Loader(const char[] path, thrown IoError);
newtype async iter char[]? LineSource();
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
newtype delegate nuint CharFormatter(const this, char[] buffer);
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

A callable `newtype` may also be used as a callable ascription on a compatible function or method declaration. In that position, the `newtype` names the semantic callable contract for the declaration's natural callable reference form while preserving the same underlying representation.

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

struct Date
{
	nuint format(char[] buffer = default) : CharFormatter
	{
		...
	}
}
```

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
		this.value += 1; // ERROR
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

Methods declared on a `newtype` use the `newtype`'s own name in symbol generation.

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

Camp's conversion model is intentionally smaller and more visible than that of many modern languages.

### 1.12.1 Design principles

The stable pattern across the language is:

- allow implicit conversion where one form is clearly a widening or convenience view of another
- require explicit conversion when crossing a nominal boundary
- avoid hidden ownership changes
- avoid hidden materialization of addressable storage
- avoid hidden unwrapping of expanded forms

The result is a conversion model that is practical, but not eager to guess.

### 1.12.2 Primitive character conversions

The built-in character conversion chain is:

Implicit widening:

```text
achar -> char -> wchar -> uchar
```

Explicit narrowing:

```text
uchar -> wchar -> char -> achar
```

### 1.12.3 Optional lifting

A value of type `T` implicitly converts to `T?`.

```camp
int? count = 5;
string? maybeText = someString;
```

This is a genuine convenience conversion and one of the few expanded conversions that Camp makes implicit by default.

The default value of `T?` is the unspecified optional. `default(T)` is a value of `T`, so assigning it to `T?` produces a specified optional carrying the default payload.

There is no opposite implicit conversion. `T?` does not implicitly unwrap to `T`.

### 1.12.4 string and pointer conversions

`string`, `wstring`, and `astring` are primitive string types with
pointer-shaped ABI representations. They implicitly convert to their underlying
const pointer forms:

```camp
string s = "hello";
const char* raw = s;
```

Converting a raw pointer to a string type is explicit because the cast asserts
zero termination:

```camp
string again = (string)raw;
```

There is no implicit conversion among `string`, `wstring`, and `astring`. A
conversion between text families requires an explicit API that performs the
required encoding conversion or validation.

Each string type also has a compiler-sponsored implicit conversion to the
matching counted const character array:

```camp
const char[] span = s;
```

This scans the string length using the visible string length property or
`getLength` method. A counted character array does not implicitly become a
zero-terminated string. Use an API that validates or creates the terminator when
that conversion is required.

A fixed-size array converts to the matching span array by synthesizing the span's `elements` and `length` components from the fixed storage:

```camp
fixed byte[32] storage;
byte[] span = storage;
```

The reverse conversion is not implicit because a span does not prove the existence of an inline fixed-size storage object of the required length.

### 1.12.5 Materializing expanded forms

Use `(struct)` when an expanded value must become real copyable storage with one address:

```camp
byte[] data = getData();
auto stored = (struct)data;
```

The reverse conversion from matching materialized storage to the expanded form is allowed where the destination type is known:

```camp
struct(byte[]) stored = ...;
byte[] view = stored;
```

This value conversion does not collapse pointer identity. Storage identity remains real.

### 1.12.6 Newtype conversions

Crossing a `newtype` boundary is explicit only unless a specific built-in rule says otherwise.

```camp
newtype UserId: uint;

UserId id = (UserId)42;
uint raw = (uint)id;
```

There is no implicit conversion in either direction, and sibling `newtype`s over the same base type remain distinct.

### 1.12.7 No implicit optional unwrapping

There is no implicit conversion from `T?` to `T`.

Presence must be checked explicitly through the `.specified` component, and the payload is read explicitly through the `.value` component.

### 1.12.8 No implicit owning conversion from counted text

A counted character array does not implicitly become an allocated zero-terminated string.

```camp
const char[] view = "hello";
string owned = view; // ERROR: not an implicit conversion
```

Ownership-changing or terminator-producing operations are expected to be explicit and usually allocation-bearing.

### 1.12.9 No implicit aggregate address formation

An expanded value does not implicitly materialize itself merely because code asks for a pointer-shaped use that truly requires one whole-object address.

Materialize first:

```camp
byte[] data = getData();
auto temp = (struct)data;
useArrayStoragePointer(&temp);
```

This rule prevents a large class of hidden temporary-creation behavior.

### 1.12.10 Conversions and deletion are independent

Delete availability is not a conversion rule.

In particular, a primitive string type's pointer-shaped representation does not imply ownership. APIs that allocate string storage must define the matching release operation explicitly.

### 1.12.11 Conversions in generic code

Generics add one important wrinkle.

In erased generic code, `T: any` is a non-copying constraint. It may represent copyable types, fixed structs, classes, fixed-size arrays, compiler-expanded forms through materialized storage, and pointer types, but the generic body may not copy, assign, return, or otherwise transport `T` by value merely because `T: any` was declared.

`T: copyable` is the erased constraint for generic code that requires `T` value copying. It excludes direct class types, fixed structs, and fixed-size array value types. Pointer types, including pointers to those fixed values, remain copyable pointer values.

For generic code, `T*` means pointer to the storage form of `T`. For compiler-expanded forms this is the materialized storage form. For fixed structs, classes, and fixed-size arrays, it is a pointer to the fixed instance or fixed storage object.

A generic copy of a `T` value requires both `T: copyable` and an available `sizeof(T)` parameter when erased lowering needs the size. `sizeof(T)` permits size-based storage operations, enumeration, and default-fill under `T: any`; it does not make `T: any` copyable.

### 1.12.12 Practical summary table

| From | To | Implicit? | Notes |
|---|---|---|---|
| `achar` | `char` / `wchar` / `uchar` | yes, by widening chain | representation widening |
| `char` | `wchar` / `uchar` | yes | representation widening |
| `wchar` | `uchar` | yes | representation widening |
| `uchar` | `wchar` / `char` / `achar` | no | explicit narrowing |
| `T` | `T?` | yes | optional lifting |
| `default(T)` | `T?` | yes | specified optional with default payload |
| `string` | `const char*` | yes | exposes the underlying const pointer |
| `const char*` | `string` | no | explicit assertion of zero termination |
| `string` | `const char[]` | yes | scans length through visible string length API |
| `string` / `wstring` / `astring` | another string family | no | requires explicit transcoding or validation API |
| `char[]` | `string` | no | requires explicit terminator-producing API |
| `T[n]` | `T[]` | yes | span view over fixed storage |
| `T[]` | `T[n]` | no | requires actual fixed storage of known length |
| expanded value | `struct(expanded-type)` | no hidden address materialization | use `(struct)` |
| `struct(expanded-type)` | expanded value | yes when destination is known | exposes stored components |
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

Camp also treats storage and abstraction separately. A expanded value can be materialized into real stored fields, and a class can live either in existing storage or in allocated storage. So the important distinction is never “stack versus heap.” It is always the value model.

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

The practical copyability difference is easiest to see in assignment and aliasing. A copyable type may be copied by ordinary value operations. A fixed type may not. Classes, fixed structs, and fixed-size arrays are fixed types; ordinary structs are copyable types unless another rule says otherwise.

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
	string baseUrl;
	int timeoutMs;
}
```

Public consumers see that `HttpClient` exists and can name pointers to it, but they do not see the class layout. The full layout exists only in the private header.

This difference is intentional.

- structs are part of the data ABI directly
- classes are part of the callable ABI directly, but their storage is private

### 2.1.9 Structural storage forms

Camp provides `struct(...)` storage forms for compiler-expanded types and anonymous structural storage with real fields.

These are storage forms, not object forms.

Example:

```camp
byte[] bytes = getBytes();
auto stored = (struct)bytes;
```

The result is a real one-address storage object. That places it in the copyable value/storage side of the language. It does not behave like a class or fixed struct.

Anonymous `struct(...)` forms follow strict compatibility rules:

- field names must match
- field order must match
- field types must match

They are useful for locals, fields, casts, arrays of materialized expanded values, and temporary storage, but they are not part of the exported nominal ABI surface.

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
	Map<string, Image> entries;
	int version;
}
```

The key difference is not field syntax but field visibility and layout exposure.

#### Struct fields

If a struct’s layout is visible, all of its fields are visible.

Structs do not have the public/private header split that classes use for layout opacity. An exported struct’s public ABI surface includes its actual fields.

A copyable struct may not contain a fixed struct or class instance as an in-place field, because copying the enclosing value would copy the fixed field.

A copyable struct may contain fixed-size array fields only when the fixed-array storage is aggregate-copyable. For a single-dimensional fixed array, this usually means the element type is copyable. For nested fixed arrays, the rule applies recursively. Copying the enclosing struct copies the fixed-array storage as part of the enclosing aggregate copy, but direct fixed-array field assignment remains invalid except for initializer-pattern writes.

```camp
struct Packet
{
	fixed byte[32] data;
	nuint length;
}

Packet a;
Packet b;

a = b;          // OK: aggregate copy, including `data`
a.data = b.data; // ERROR: direct fixed-array copy
```

A fixed struct may contain fixed struct fields and fixed-size arrays whose elements are fixed values. It may contain in-place class fields when the class layout is visible. A class may also contain fixed struct fields and fixed-size array fields.

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

Primitive receiver names use title-style spellings in flattened symbols:
`string` becomes `String`, `wstring` becomes `WString`, `astring` becomes
`AString`, `nuint` becomes `NUInt`, and so on.

```camp
// Symbol: String_tryParseInt
bool tryParseInt(string this, out int value);
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

If `this` is omitted, the receiver still exists implicitly. Lifetime defaults for the implicit receiver are defined in the lifetime chapter. When an instance method ascribes a context-carrying callable `newtype` with an explicit callable `this` parameter, those callable `this` qualifiers become the method receiver's effective qualifiers unless the method writes an explicit `this` parameter.

Member fields and member methods are not implicitly in scope inside an instance
method body. Use `this.` for instance access:

```camp
struct Rect
{
	int width;

	int getWidth()
	{
		return this.width; // OK
	}
}
```

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
	static FileHandle* open(const char[] path);
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
	string name;

	Logger(string name)
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
	string name;

	Connection(string name)
	{
		this.name = name;
	}
}

class SecureConnection: Connection
{
	bool verifyCertificates;

	SecureConnection(string name, bool verifyCertificates = true)
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
	string path;

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
	Map<string, Image> entries;

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
	const char[] method;
	const char[] url;
	HeaderCollection headers;
	int timeoutMs;

	HttpRequest(unscoped const char[] method, unscoped const char[] url)
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

Fixed-size array fields may be initialized or overwritten in constructors and trailing initializers using target-typed initializer expressions, compatible string literals, or `default`.

```camp
struct Packet
{
	fixed byte[4] magic;
	fixed char[16] name;

	Packet()
	{
		this.magic = [1, 2, 3, 4];
		this.name = "demo";
	}
}
```

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

The same no-hidden-bulk-construction rule applies to fixed-size array storage. Declaring `fixed T[n] storage;` creates inline storage; it does not implicitly call a constructor for every element. Individual elements may be initialized in place when the element operation itself is valid.

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
- exported interface implementation relationships expose vtable objects that C
  callers may pass to generic Camp functions requiring `vtableof(...)`

#### Private header

The private header contains full type details and internal helper surfaces used within the defining module.

- full class layout appears here
- hidden `_vt` and `_vt_InterfaceName` fields appear here with their concrete vtable-pointer types
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
	void setText(char[] value);
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

Interfaces in Camp are explicit ABI-visible contracts. They are not hidden runtime objects, they are not structural method matching, and they are not delegate-like expanded `(context, call)` values.

An interface declaration defines a nominal vtable shape. Ordinary interface values are represented by pointers to interface-instance slots.

### 2.4.1 What an interface is

```camp
interface IRef
{
	void retain();
	void release();
}
```

Camp distinguishes two related source forms:

- `IFoo`
- `IFoo*`

The distinction exposes the underlying calling convention without requiring ordinary code to spell the lowered form.

### 2.4.2 Bare `IFoo` versus `IFoo*`

| Source form | Meaning |
|---|---|
| `IFoo` | pointer to the interface vtable |
| `IFoo*` | pointer to an interface-instance slot whose stored pointer is the interface vtable |

Most ordinary code uses `IFoo*`.

The lowered storage model is:

```camp
struct IFoo
{
	// vtable entries
}
```

So the common lowered shapes are:

| Lowered form | Meaning |
|---|---|
| `IFoo` | vtable storage object |
| `IFoo*` | pointer to a vtable object |
| `IFoo**` | pointer to a stored vtable-pointer slot; this is the ordinary interface-instance pointer |

At the source level, bare `IFoo` corresponds to the vtable pointer form. Source `IFoo*` corresponds to the interface-instance pointer form.

### 2.4.3 Conceptual lowering model

Given:

```camp
interface IRef
{
	void retain();
	void release();
}
```

Camp conceptually lowers the interface vtable to:

```camp
struct IRef
{
	fn void(IRef** ctx) retain;
	fn void(IRef** ctx) release;
}
```

Each vtable entry is a plain function pointer. Its first parameter is the interface-instance slot pointer.

That slot pointer is the context. It points at storage containing the vtable pointer being used for the call.

### 2.4.4 Interface inheritance lowering

Interface inheritance is flattened into the derived vtable shape.

```camp
interface Readable
{
	nuint read(byte[] buffer);
	bool getEndOfFile();
}

interface Seekable: Readable
{
	void seek(nuint position);
	nuint getPosition();
}
```

Conceptually:

```camp
struct Readable
{
	fn nuint(Readable** ctx, byte[] buffer) read;
	fn bool(Readable** ctx) getEndOfFile;
}

struct Seekable
{
	Readable Readable;

	fn void(Seekable** ctx, nuint position) seek;
	fn nuint(Seekable** ctx) getPosition;
}
```

The inherited interface sub-vtable appears first when it is the leading base contract. In that case, upcasting from `Seekable*` to `Readable*` may be a no-op at the interface-slot address. Other inherited layouts may require an adjusted interface conversion.

Diamond inheritance does not duplicate the same base contract repeatedly.

### 2.4.5 Calling through bare `IFoo`

Calling a method on bare `IFoo` requires supplying an interface-instance slot explicitly.

Conceptually:

```camp
IRef vt = SomeType_IRef;
IRef* value = ...;

vt.retain(value);
vt.release(value);
```

In lowered terms, `value` is an `IRef**`: a pointer to a stored vtable-pointer slot.

### 2.4.6 Calling through `IFoo*`

Calling a method on `IFoo*` supplies the interface-instance slot automatically.

```camp
IRef* value = obj;
value.retain();
value.release();
```

Conceptually, that is equivalent to calling the vtable entry with the interface-instance slot as the first argument.

### 2.4.7 Nominal conformance

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

### 2.4.8 Every declared interface entry is required in v1

Camp keeps the v1 interface model simple: every declared entry is required.

That means:

- missing required members in the type body are a compile error
- conformance does not silently fill in null entries
- inherited interface entries remain required
- constructor and destructor entries, when declared, are also required

Optional-entry designs were considered, but v1 does not use them.

### 2.4.9 Lifetime annotations are part of interface method contracts

Receiver lifetime annotations and ordinary parameter lifetime annotations are part of an interface method's contract.

That means:

- an implementation method must satisfy the same lifetime contract as the interface method
- changing receiver lifetime requirements changes the callable contract
- an implementation that does not match the interface method's lifetime annotations does not implement that member

This applies equally to an in-scope explicit `this` parameter and to ordinary annotated parameters.

### 2.4.10 Interface inheritance

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

### 2.4.11 Interface casting and ambiguity

Upcasting to a base interface is allowed.

Depending on the layout involved, it may be:

- a no-op at the interface-slot address
- or an adjusted interface conversion

Downcasting is never implicit. Camp does not provide a general safe runtime downcast for interfaces. Any such cast is explicit and unsafe.

If inherited method names would make an interface call ambiguous, the call must be disambiguated with an explicit cast. Camp does not silently choose one inherited method set.

### 2.4.12 Class implementation of interfaces

When a class implements an interface, the compiler adds one hidden field per **declared** implemented interface:

- `_vt_InterfaceName`

That hidden field stores a pointer to the vtable object for the class/interface pair. In lowered form, a field for `IFoo` has the shape:

```camp
IFoo* _vt_IFoo;
```

Layout rules:

- `_vt` comes first when the class is virtual or abstract
- hidden interface fields are placed after the ordinary object/base fields of the most-derived class
- interface fields appear in declared interface order
- a class does not get separate hidden fields for every inherited base interface automatically

For each declared implemented interface, the compiler generates a standalone vtable object:

- `TypeName_InterfaceName`

The compiler also assigns the hidden interface fields during typed construction, before user constructor body execution.

### 2.4.13 Class-to-interface conversion

A class instance converts naturally to an interface-instance pointer.

```camp
ExtraCalculator* calc = new ExtraCalculator(5);
ICalculator* iface = calc;

int sum = iface.add(4, 6);
```

No heap boxing is introduced. The resulting interface pointer refers to the class's stored interface-specific vtable-pointer field.

If the implementing interface field is at offset zero in the object layout, vtable entries may call concrete methods directly using a compatible receiver shape. Otherwise, the vtable entry uses a fixup thunk.

A fixup thunk:

1. receives the interface-instance slot pointer
2. recovers the class instance pointer from the slot address
3. invokes the real implementation path

The recovery is equivalent to subtracting the offset of the relevant `_vt_InterfaceName` field from the slot address.

### 2.4.14 Manual lowering example

This example shows the shape beneath the interface feature.

```camp
interface Readable
{
	nuint read(byte[] buffer);
	bool getEndOfFile();
}

interface Seekable: Readable
{
	void seek(nuint position);
	nuint getPosition();
}

interface NamedResource
{
	char[] getName();
	void rename(char[] name);
}

class MemoryDocument: Seekable, NamedResource
{
	string name;
	byte[] data;
	nuint position;

	nuint read(byte[] buffer) { ... }
	bool getEndOfFile() { ... }
	void seek(nuint position) { ... }
	nuint getPosition() { ... }
	char[] getName() { ... }
	void rename(char[] name) { ... }
}
```

A representative lowered form is:

```camp
struct Readable
{
	fn nuint(Readable** ctx, byte[] buffer) read;
	fn bool(Readable** ctx) getEndOfFile;
}

struct Seekable
{
	Readable Readable;

	fn void(Seekable** ctx, nuint position) seek;
	fn nuint(Seekable** ctx) getPosition;
}

struct NamedResource
{
	fn char[](NamedResource** ctx) getName;
	fn void(NamedResource** ctx, char[] name) rename;
}

class MemoryDocument
{
	Seekable* _vt_Seekable;
	NamedResource* _vt_NamedResource;

	string name;
	byte[] data;
	nuint position;

	MemoryDocument(char[] name, byte[] data)
	{
		this._vt_Seekable = &MemoryDocument_Seekable;
		this._vt_NamedResource = &MemoryDocument_NamedResource;

		this.name = name.stringCopy();
		this.data = data;
		this.position = 0;
	}

	~MemoryDocument()
	{
		delete this.name;
	}

	// ordinary method implementations
}
```

The first declared interface can use direct vtable entries when its slot is at offset zero:

```camp
Seekable MemoryDocument_Seekable =
{
	.Readable =
	{
		.read = (fn nuint(Readable** ctx, byte[] buffer))MemoryDocument_read,
		.getEndOfFile = (fn bool(Readable** ctx))MemoryDocument_getEndOfFile,
	},

	.seek = (fn void(Seekable** ctx, nuint position))MemoryDocument_seek,
	.getPosition = (fn nuint(Seekable** ctx))MemoryDocument_getPosition,
};
```

A later declared interface uses fixup entries:

```camp
NamedResource MemoryDocument_NamedResource =
{
	.getName = MemoryDocument_NamedResource_getName,
	.rename = MemoryDocument_NamedResource_rename,
};

char[] MemoryDocument_NamedResource_getName(NamedResource** ctx)
{
	MemoryDocument* instance =
		(MemoryDocument*)(((byte*)ctx) - offsetof(MemoryDocument._vt_NamedResource));

	return instance.getName();
}

void MemoryDocument_NamedResource_rename(NamedResource** ctx, char[] name)
{
	MemoryDocument* instance =
		(MemoryDocument*)(((byte*)ctx) - offsetof(MemoryDocument._vt_NamedResource));

	instance.rename(name);
}
```

An interface value is the address of the stored vtable-pointer field:

```camp
MemoryDocument document = init MemoryDocument("notes.txt", bytes) finally delete;

Seekable** seekable = &document._vt_Seekable;
Readable** readable = (Readable**)&document._vt_Seekable;
NamedResource** named = &document._vt_NamedResource;

seekable.seek(seekable, 0);
readable.read(readable, buffer);
named.rename(named, "archive-notes.txt");
```

Ordinary source code uses the sugared interface forms instead of these lowered `**` forms.

### 2.4.15 Virtual overrides and inherited interface implementation

If a base class implements an interface and derived classes are meant to customize that behavior, the intended pattern is:

- the base class declares the interface implementation
- the implementing methods are virtual when customization is intended
- derived classes override those methods

Re-implementing an already-implemented interface again in a derived class is discouraged.

### 2.4.16 Struct implementation of interfaces

Structs do not gain hidden interface fields.

Instead, a pointer to a struct value that implements an interface may convert to an ordinary interface pointer by creating a scoped indirect interface pointer. This applies to both copyable structs and fixed structs.

An indirect interface pointer is compiler-created adapter storage. It is not a type that user code can name directly. The adapter contains:

- first a vtable pointer
- then a pointer to the original struct value

The resulting interface pointer points at the adapter's vtable-pointer slot. Calls through that pointer use a fixup vtable whose entries use the stored pointer-to-data as the implementation receiver.

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

### 2.4.17 Scoped-only automatic struct conversion and escaped interfaces

Automatic struct-to-interface conversion is allowed only where the indirect interface pointer's adapter storage remains valid for the duration of the use.

Typical valid cases:

- passing a pointer to a struct value to a scoped interface-pointer parameter
- storing the interface pointer in a newly initialized local in the current declaration-scope

An indirect interface pointer may exist as a temporary only for scoped interface-pointer arguments. An unscoped interface-pointer argument may not receive such a temporary; the caller must first bind the interface pointer to a newly initialized local in the current declaration-scope.

An indirect interface pointer may not be assigned to an array element, caller-provided argument, field, component of an aggregate, already-initialized local, or any other storage location outside the current declaration-scope initialization.

The adapter is scoped stack storage. It does not implicitly satisfy escaped interface-pointer requirements.

For an `escaped interface`, automatic struct-to-interface conversion is forbidden because that automatic conversion path uses scoped adapter storage.

### 2.4.18 Interface constructors and destructors

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

- it receives the implementing interface-instance slot pointer
- it must explicitly declare `within allocator` as its last parameter
- the instance itself may be deallocated after destruction completes

For both constructor and destructors, concrete implementations do not need to spell `within allocator` unless they actually use that parameter. The generated interface thunk forwards it only when needed.

An interface constructor entry does not make the interface type itself directly instantiable. This is still invalid:

```camp
new ICounterStore()
```

### 2.4.19 Generic interfaces and generic interface methods

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

These are ordinary ABI-visible interface surfaces. Generic substitution affects the declared parameter and result types. It does not change the basic interface-instance representation: vtable entries still receive the interface-instance slot pointer as their first parameter.

### 2.4.20 Exported interface implementation relationships

Interfaces are part of the ABI surface in their own right. They are not opaque in the same way classes are opaque.

If a struct or class exports an interface implementation relationship, the
compiler exposes the vtable object for that implementation pair.

Conceptually:

- `TypeName_InterfaceName`

becomes part of the exported ABI surface as an `extern const` pointer to the
interface vtable shape.

That exported symbol lets a C caller call a generic Camp function that requires
`vtableof(T: Interface)`: the caller passes a pointer to the concrete object and
the matching exported interface vtable pointer.

Exported class opacity still remains in force. Hidden `_vt_InterfaceName` fields are private-header details, not public class layout.

If outside code cannot see the implementing class layout, it cannot form the interface-instance pointer by taking the address of the hidden field. A module may expose an explicit projection helper when that conversion needs to cross the public ABI.

### 2.4.21 Interface cost model

| Case | Cost |
|---|---|
| bare `IFoo` | one pointer to a vtable |
| `IFoo*` | one pointer to an interface-instance slot |
| class implementation | one hidden stored vtable-pointer field per declared implemented interface |
| struct conversion | a scoped indirect interface pointer adapter may be created |
| interface call | one explicit indirection-based call |

There is no hidden runtime registry, hidden metadata lookup, or heap boxing requirement for ordinary class-backed interface calls.

### 2.4.22 Mental model

The right mental model is simple:

- an interface declaration defines a nominal vtable shape
- bare `IFoo` is a vtable pointer
- source `IFoo*` is the ordinary callable interface-instance form
- lowered interface-instance pointers have the shape `IFoo**`
- class implementations store hidden interface-specific vtable-pointer fields
- struct implementations use scoped indirect interface pointer adapters

That model is explicit, ABI-visible, and consistent with the rest of Camp's design.

# 3. Statements and Expressions

This section defines the executable core of Camp: the statements that control evaluation, the ordinary operators used in expressions, the rules for member and property access, expanded multi-value results, and the syntax used for iteration, slicing, and cleanup.

Camp intentionally keeps most day-to-day statement and operator syntax familiar to C-family programmers. The important work here is therefore not to restate C in full, but to make Camp-specific rules precise: boolean conditions are explicit, member access uses one uniform surface, expanded values participate directly in results and calls, property syntax is a rewrite over methods, and cleanup remains explicit.

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
- `goto`
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

`return` exits the current function. If the function returns a value, the
returned expression is target-typed to the function's return type.

An ordinary single-value return looks as expected:

```camp
int abs(int x)
{
	if (x < 0)
		return -x;
	return x;
}
```

Camp also allows expanded multi-value returns. Those are described in §3.6.

### 3.1.5 Labels and `goto`

Camp supports C-style labels and `goto` within a single function.

```camp
retry:
	if (!tryStep())
		goto retry;
```

A label is visible throughout its function body. A `goto` may not jump to a
label in another function, lambda, or generator body. Unlike ordinary structured
control flow, `goto` is allowed to jump out of blocks without running skipped
`finally` expressions; use it with the same care expected in C.

### 3.1.6 Discard `_`

`_` is a write-only discard. It can be used where a value is produced but the
program intentionally ignores it:

```camp
_ = doWork(out int result);
tryParse(text, out _);
mayFail(catch _);
```

Each discard has its own hidden storage as needed. It may not be declared as a
normal variable, type, function, parameter, field, or member name, and reading
from `_` is an error.

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

### 3.2.3 Equality and expanded values

Expanded values compare according to their specific form.

Optionals compare by payload and presence flag when the payload type supports equality. Arrays compare by their components unless a library method defines elementwise comparison explicitly. Delegates compare by call target and context when such comparison is permitted by the target platform.

Elementwise sequence comparison is a library operation, not hidden array equality.

### 3.2.4 Member access is not `->`

Camp does not use the C `->` operator.

Member access is always written with `.` whether the receiver is:

- a value
- a pointer
- a class instance
- a class pointer
- a expanded value with named components

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

- expanded-return deconstruction or multi-step call results
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
auto title = "Report";              // string
auto bytes = new byte[256];          // byte[]
auto maybe = default(int?);          // int?
```

`auto` is often the clearest choice when the initializer already makes the type obvious or when the exact expanded form is more verbose than helpful.

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
- string and character-array literals
- initializer forms
- omitted trailing `out` values captured into locals
- some generic arguments and method-group conversions

Examples:

```camp
string title = "Report";
```

Here the string literal is target-typed as `string`.

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

- the distinction between expanded slots and materialized storage matters
- a narrow nominal type is intended
- zero-terminated text versus counted text matters
- a pointer lifetime annotation matters
- the exact numeric type matters

Examples:

```camp
string title = "Report"; // clearer than auto when string representation matters
const char[] path = request.Path;
escaped byte* data = getBuffer();
```

## 3.4 Member Access

Member access is written with `.` and is shared across fields, methods, expanded components, and static members.

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

### 3.4.3 Expanded components

Compiler-expanded values expose their components through member access.

```camp
byte[] buffer;
int? count;

log(buffer.length);

if (count.specified)
	log(count.value);
```

The ABI component symbols are still introduced into the containing scope. For `buffer`, the symbols are `buffer` and `buffer_length`; for `count`, the symbols are `count` and `count_specified`. A user declaration that repeats one of those ABI component names in the same scope is a duplicate declaration.

Fixed-size array values expose `.elements` and `.length` through the same member-access surface. Those members are synthesized from the fixed storage and do not introduce `name_length` or other expanded component symbols for the binding.

### 3.4.4 Method references

A method name used without `()` refers to the method itself rather than calling it. That ordinary member-binding behavior remains available even when a method is also eligible for property syntax.

```camp
delegate void(const char[]) writer = Console.writeLine;
writer("hello");
```

Property syntax never removes the underlying method symbol.

When the referenced declaration has a matching callable `newtype` ascription, the method-reference expression has the ascribed nominal callable type for that reference form. Without such an ascription, the expression keeps its ordinary anonymous callable type. Explicit callable `this` qualifiers on the target callable type are enforced when the method reference is formed.

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

struct Date
{
	nuint format(char[] buffer = default) : CharFormatter
	{
		...
	}
}

const Date date = ...;
auto formatter = date.format; // CharFormatter
```

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

If a property getter omits an explicit receiver, its implicit `this` is `const`.
Setter receivers and ordinary method receivers remain mutable by default.

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
	string Text;
	string getText() => this.Text;
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
	string getPath(thrown Err) => ...;
	void setPath(string value, thrown Err) => ...;
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
iter char[] getLines() => ...;
```

```camp
fileView.Lines        // ERROR
fileView.getLines()   // OK
```

This rule preserves the ordinary expectation that property access is field-like or value-like. Iterator use carries cleanup and control-flow consequences that are better made explicit.

## 3.6 Multiple Return Values

Camp supports multiple result values through explicit result slots. This is not a general tuple subsystem and does not create arbitrary anonymous expanded values.

The common forms are:

- omitted trailing `out` parameters bound by immediate deconstruction
- async completion callbacks with more than one non-error result slot
- iterator and async-iterator protocols that yield several result slots

### 3.6.1 Omitted trailing `out` values

A call may omit trailing `out` parameters when the call expression is immediately consumed by a binding form.

```camp
void getBounds(out int width, out int height);

auto (width, height) = getBounds();
```

This lowers directly to explicit caller-provided storage:

```camp
int width;
int height;
getBounds(out width, out height);
```

If a single trailing `out` parameter is omitted, the result may be bound as an ordinary single value:

```camp
void getCount(out int count);

auto count = getCount();
```

A function that returns an array uses the array element pointer as the ordinary return value and the length as an omitted trailing result slot. When the call result is bound as an array, the compiler reconstructs the expanded array local from both components. A caller may also select one component directly:

```camp
byte[] getBytes();

auto bytes = getBytes();
nuint lengthOnly = getBytes().length;
byte* elementsOnly = getBytes().elements;
```

### 3.6.2 Deconstruction

A multi-output result may be deconstructed when the arity matches.

```camp
auto (min, max) = getMinMax(values);
auto (x, y) = getOrigin();
```

Each local is inferred independently from the corresponding result slot.

### 3.6.3 Multi-output results are not general values

A multi-output result produced by omitted `out` parameters is a binding surface. It is not an anonymous value that can be stored, indexed, placed into arrays, or passed around as a first-class object.

When a result needs a durable shape, declare an ordinary `struct` result type:

```camp
struct DivResult
{
	int quotient;
	int remainder;
}

DivResult divideInt(int a, int b)
{
	return { .quotient = a / b, .remainder = a % b };
}
```

### 3.6.4 Async and iterator result slots

Async, iterator, and async-iterator protocols may carry multiple non-error result slots where the protocol explicitly defines them.

Those slots may be consumed through deconstruction:

```camp
auto (x, y) = await getPointAsync();
```

Protocol-defined multi-output slots do not imply user-defined expanded value types.

## 3.7 Error Handling and Cleanup

Camp uses explicit error values rather than exceptions. The control-flow surface still uses familiar keywords, but the underlying model is value-based and ABI-visible.

### 3.7.1 `thrown`

A function that may fail declares a trailing `thrown` parameter:

```camp
int parsePort(const char[] text, thrown ParseError)
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
int parsePort(const char[] text, thrown ParseError)
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
void openConfig(const char[] path, thrown AppError)
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
foreach (char[] line in fileView.getLines())
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
uchar getChar(const char[] this, @index nuint unit);
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
scoped char[] slice(const char[] this, @range nuint index = 0, nuint count = ^0);
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

### 3.9.5 Arrays and strings use both indexing and slicing

Span arrays and fixed-size arrays support ordinary element indexing through `[]` and slice-like views
through range indexing.

```camp
int[] values = [1, 2, 3, 4, 5, 6];

int first = values[0];
int last = values[^1];
auto middle = values[2..^1];
```

The same source surface works for primitive string types. String indexing and
slicing are code-unit based and require a visible string length property.

```camp
string text = "hello";

char firstChar = text[0];
char lastChar = text[^1];
const char[] body = text[1..^1];
```

Plain pointers do not support from-end indexing or range slicing. If a program
has a pointer to materialized array storage or to fixed-size array storage, it must dereference to the array
value first.

```camp
int[8]* values;
int[] prefix = (*values)[0..4]; // OK
int[] bad = values[0..4];       // ERROR: slicing a pointer
```

### 3.9.6 Methods and property indexers may also be range-aware

Methods may opt into the same range syntax by declaring `@index` or `@range`
parameters. That keeps user-defined slice-like APIs aligned with built-in array
and string slicing.

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

Storage-class specifiers such as `const`, `volatile`, `scoped`, `unscoped`, and
`escaped` follow C-style placement: a specifier applies to the thing on its
left; if there is nothing on its left, it applies to the thing on its right.

```camp
const int* a;      // pointer to const int
int* const b;      // const pointer to mutable int
const int[] data;  // const elements, mutable array binding
int[] const view;  // const array binding, mutable elements
```

Mutable values may convert to const views, non-volatile values may convert to
volatile views, and lifetime widening follows `escaped -> unscoped -> scoped`.

### 4.1.3 `escaped`

`escaped` means the annotated value is not in stack storage.

This is a storage/context fact, not a pairwise relation.

It does not imply process-global lifetime, thread safety, or cross-thread mobility. A value allocated from a custom allocator may still be allocator-bound or arena-bound. `escaped` means only that the value is not in stack storage.

Examples:

```camp
escaped Widget* p
escaped char[] text
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
- compiler-expanded components
- materialized `struct(T)` storage
- array elements when the element type contains pointers
- fixed-size array elements when the element type contains pointers
- delegate context storage
- compiler-generated iterator frames
- compiler-generated async frames
- compiler-generated postponed-operation contexts

This rule is recursive through materialized storage.

A value therefore does not need to itself be a pointer in order to participate in lifetime checking. A stack value containing pointers is checked through the pointers it contains.

This is what makes containers of `char[]`, `Span<T>`, delegate values, and other pointer-bearing values meaningful under the lifetime system.

A pointer-bearing aggregate or context value has one lifetime for its contained pointers. Individual fields do not acquire separate lifetime annotations, even when those fields are nested inside other fields.

Assignment into a field or nested field is therefore checked as assignment into the containing value. A value may not be assigned into a field if that value is too narrow for the containing aggregate's current lifetime.

This rule applies equally to structs, classes, expanded values, materialized `struct(T)` storage, arrays and fixed-size arrays whose elements contain pointers, optionals whose payload contains pointers, delegates, and compiler-generated context objects.

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

When an instance method of an `escaped class` ascribes a context-carrying callable `newtype`, the escaped receiver requirement remains explicit in the callable contract or in the method declaration. The ascribed callable `newtype` may declare `escaped this`, or the method may explicitly declare `escaped this`. Omitting both is invalid for an ascribed `escaped class` method.

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

### 4.3.1 The allocator pattern

Allocators are ordinary types. The language does not require a particular
standard-library base class, but it does recognize a small pattern:

```camp
abstract class Allocator
{
	abstract void* alloc(nuint size);
	abstract void free(escaped void* ptr);
}
```

The exact integer type of `size` may vary, but `alloc` must take one integer
byte-count parameter and return untyped storage. `free` releases a pointer
previously returned by the same allocator.

When a `within` parameter omits its type, the compiler looks for a visible type
named `Allocator`. If a program wants a different allocator type, it may spell
that type explicitly:

```camp
void parse(within Arena* arena)
{
	...
}
```

### 4.3.2 Allocation fallback

If a `new` or pointer-form `delete` has no current allocator, or the current
allocator is `null`, the compiler uses ordinary functions visible at the call
site:

```camp
extern void* malloc(nuint size);
extern void free(void* ptr);
```

`malloc` must take a single integer byte count. `free` must accept `void*`.
This keeps allocation independent from any particular standard library.

Allocation failure is represented by `null`. The compiler-generated `new`
lowering checks for `null` before invoking the constructor.

### 4.3.3 Lexical allocator context

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

### 4.3.4 Expression-form override

A specific operation may override the surrounding context.

```camp
within(a)
{
	auto r = within(b) new Rect(10, 20);
}
```

This changes only that operation.

### 4.3.5 What `within` affects

`within` affects allocator-backed behavior.

In ordinary source code, that means primarily:

- `new`
- pointer-form `delete`
- constructor or destructor logic that accepts a `within` parameter
- interface-based construction and destruction paths that thread allocators

A plain `init` does not allocate outer instance storage. However, if the constructor selected by `init` takes `within`, the current allocator context is still forwarded into that constructor.

### 4.3.6 `within` parameters

A function, constructor, or destructor may declare a `within` parameter.

```camp
class StringBuilder
{
	Allocator* arena;
	char* buffer;

	StringBuilder(within arena)
	{
		this.arena = arena;
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
| Type | visible `Allocator*`, unless a type is written explicitly |
| Lifetime | `unscoped` |
| Default value | `null` |

Only one `within` parameter is allowed per routine.

### 4.3.7 Implicit forwarding

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
		this.arena = arena;
		this.data = new byte[length];
	}
}

within(tempArena)
{
	auto x = new BufferOwner(1024);
}
```

The constructor receives `tempArena` without the call having to mention it explicitly.

### 4.3.8 Inside the routine

Inside a routine that declares `within`, that parameter itself establishes the current allocator context.

```camp
void add(Item x, within arena)
{
	auto node = new Node(x);
}
```

Here `new Node(x)` uses `arena` when it is non-null, and otherwise falls back to
visible `malloc`.

### 4.3.9 Stored allocator style

A type may accept a `within` parameter and store the allocator for later internal allocations.

```camp
class StringBuilder
{
	Allocator* arena;
	char* buffer;

	StringBuilder(within arena)
	{
		this.arena = arena;
	}
}
```

This style is useful when many related allocations should remain tied to the object.

### 4.3.10 Threaded allocator style

A routine may instead accept `within` and simply use it during the call.

```camp
void push(Value v, within arena)
{
	auto node = new Node(v);
}
```

This style keeps the object model simpler and leaves allocator choice to the call chain.

### 4.3.11 `within` and async frames

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

The expression `init T[n]` remains an array allocation expression whose result is `T[]`. It is not a fixed-size array declaration. Fixed-size array storage is declared with a fixed-size array type and the `fixed` marker:

```camp
auto span = init byte[256]; // byte[]
fixed byte[256] storage;    // fixed byte[256]
```

For pointer-bearing local values, `init` participates in the constructor result lifetime rule described below.

### 4.4.2 `new`

`new` allocates storage and then constructs.

The allocator used for the outer allocation is chosen in this order:

1. an explicit `within(...)` attached to the `new` expression
2. the current surrounding `within(...)` context
3. visible `malloc(...)` fallback

```camp
auto a = new Node(1);                  // default allocator, unless inside within(...)
auto b = within(arena) new Node(2);    // explicit override
```

If the selected constructor declares `within`, that same allocator is also forwarded into the constructor unless the call supplies a different allocator explicitly.

If allocation returns `null`, the constructor is not called and the `new`
expression evaluates to `null`.

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
3. visible `free(...)` fallback

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
- `public` symbols are visible to other Camp files in the build
- exported declarations form the public ABI surface
- namespaces are an import-site naming aid, not a runtime feature
- the visible organization of Camp code is compiler-driven rather than header-driven

The goal is to make visibility explicit in source and keep generated public and private surfaces predictable.

### 5.1.1 Default visibility

A declaration is internal to its defining source file unless it is marked
`public` or `export`.

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

None of the declarations above are visible outside the defining module.

Cross-file declarations are written explicitly:

```camp
public struct InternalPoint
{
	int x;
	int y;
}

export bool isOrigin(in Point this)
{
	return this.x == 0 && this.y == 0;
}
```

`public` and `export` are both visible to other Camp files in the same build.
The difference is API exposure: `export` declarations are included in generated
public API surfaces, while `public` declarations are library-internal.

This rule applies uniformly to:

- types
- functions
- methods
- delegates
- enums
- `newtype`s
- other exported callable or data surfaces

### 5.1.2 `export` is about ABI surface, not only name lookup

An exported declaration is part of the module’s public ABI story. A `public`
declaration is visible to Camp code in the build, but is not documented in the
public API surface.

That means `export` affects more than ordinary visibility. It also affects what must appear in generated public headers and what outside code is allowed to name or call.

For example, an exported struct remains layout-visible in the public header, while an exported class remains opaque. Those type-specific consequences were defined earlier. The important point here is that `export` is the switch that places a declaration on the public boundary at all.

Source metadata is emitted by the compiler as a file-level output mode, not by
marking individual declarations with `export("api")` or similar syntax. Exported
declarations are included in the default metadata view; public and private
declarations can be requested by compiler option when a tool needs a broader
source-level view.

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

export void printLine(const char[] text)
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

An `extern` function, method, or variable is implemented outside Camp. An
`extern` `class`, `struct`, or `newtype` declaration describes a type whose
member implementations are supplied outside Camp.

### 5.1.7 Aliases

A top-level `alias` declaration introduces another name for an existing type,
primitive, callable symbol, target callspec/typespec, or alias.

```camp
export alias TCHAR = wchar;
alias write = Std::Console_writeLine;
```

Aliases are active throughout the file or, when `public` or `export`, across
the visible build surface. They are not C typedefs. Camp API output preserves
exported aliases, but generated C uses the resolved underlying name.

Alias targets are names, not full type expressions. For example, aliasing
`TCHAR` is valid; aliasing `char[]` is not.

### 5.1.8 Symbol overrides

The built-in `@symbol("Name")` attribute changes the canonical flattened symbol
for a function or variable.

```camp
@symbol("SetWindowTextA")
extern bool SetWindowText(HWND hWnd, astring lpString);

class Control
{
	@symbol("ControlValue")
	export int getValue()
	{
		return 0;
	}
}
```

The source name remains usable for ordinary Camp lookup. The override becomes
the canonical symbol name, so direct calls to the old compiler-generated
flattened name are not valid. Exported declarations preserve `@symbol` in Camp
API output; generated C uses the overridden name.

### 5.1.9 Doc comments and metadata attributes

Camp supports doc comments as a convenient source form for metadata attributes.
Doc comments attach to the immediately following declaration or declaration
child.

```camp
/// Writes a line to the current [Console] writer.
///
/// - value: Text to write.
/// @returns Nothing.
export void writeLine(const char[] value);
```

Plain doc text becomes `@summary(...)`. The recognized doc attributes are
`@summary`, `@remarks`, `@returns`, `@example`, `@see`, and `@deprecated`.
Child targets such as `- value:` attach documentation to parameters, type
parameters, fields, enum values, receiver parameters, or members when those
children exist. Unknown doc attributes and unresolved child targets are compiler
errors.

Inline code spans and triple-backtick fenced code blocks are treated as literal
text. Links written as `[Symbol]` become metadata symbol references. In the
generated Camp API surface, the example above is equivalent to ordinary metadata
attributes such as:

```camp
@summary("Writes a line to the current %s writer.", symbols: [symbolof(Console)])
export void writeLine(@summary("Text to write.") const char[] value);
```

`symbolof(...)` is valid only inside metadata attribute arguments. It is checked
by the compiler so documentation links refer to real visible declarations.

Metadata attributes are source-level information. Exported Camp API output
preserves them, while generated C and C API headers omit documentation for now.

### 5.1.10 Metadata JSON output

The compiler can emit a source-level metadata JSON file for documentation tools,
editors, and external generators:

```text
campc library.camp --emit-metadata export
campc library.camp --emit-metadata public
```

Supported values are `none`, `export`, `public`, and `all`. Metadata defaults
to `export` for static and shared library builds and to `none` for executable
builds and plain C-emission builds. When metadata is enabled, it is emitted as a
deliverable named `<project>_api.json` beside the other output artifacts. The
`all` view is the full source-level metadata view.

Metadata JSON describes Camp declarations as programmers see them: names,
symbols, visibility, generic parameters, fields, parameters, callable
ascriptions, aliases, and metadata attributes. It is not a lowered C ABI dump
and does not include generated helper declarations by default.

### 5.1.11 Target callspecs and typespecs

Some targets define calling-convention and pointer/memory-model specifiers.
Camp accepts those specifiers in fixed type and callable positions and validates
them against the selected target.

```camp
extern _stdcall void InitializeLibrary();
fn _cdecl int(int value) callback;
char* _far text;
newtype fn _far _pascal nint FARPROC();
```

Callspecs describe how a callable is called. Typespecs describe target-specific
pointer or storage forms. Target details such as INI file syntax are tooling
configuration, but the source language treats validated callspecs/typespecs as
part of the type.

Unspecified target specs may convert to explicit wider target specs when the
selected target says that conversion is safe. Explicit casts may be used for
compatible same-kind forms when an implicit conversion would be narrowing.

### 5.1.12 Conditional compilation

Camp has C#-style conditional compilation. Symbols are either defined or not
defined; they do not have values.

```camp
#define WINDOWS

#if WINDOWS && !UNICODE
export alias TCHAR = achar;
#else
export alias TCHAR = wchar;
#endif
```

Supported directives are `#define`, `#undef`, `#if`, `#elif`, `#else`, and
`#endif`. Conditions may use symbol names, `TRUE`, `!`, `&&`, `||`, and
parentheses. Code in inactive branches is tokenized but not parsed as Camp code.

### 5.1.13 Public versus private generated views

Camp’s visibility rules are reflected in two generated surfaces:

- a **public header** for exported declarations
- a **private header** for full module-internal details

This distinction was introduced earlier for data structures. At the module level, the key point is simpler:

- `export` decides what belongs in the public view
- `public` declarations are visible to Camp code but omitted from the public view
- private declarations remain local to the defining file

This gives Camp a direct source-level replacement for the traditional C pattern of manually splitting declarations across headers and implementation files.

### 5.1.14 Foreign import direction in v1

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
export iter char chars(char[] text, within allocator, thrown TextError)
{
	return charsOwned(text.stringCopy(within allocator));
}

class iter char charsOwned(escaped string text)
{
	finally delete text;

	const char[] units = text.asArray();
	for (nuint i = 0; i < units.length; i++)
		yield units[i];
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

Inside a generator body, an `init T[n]` array allocation expression is invalid. A generator may declare fixed-size array storage instead; that storage becomes part of the generated iterator state.

```camp
struct iter byte nextBytes()
{
	fixed byte[256] scratch; // OK
	auto temp = init byte[256]; // ERROR
	...
}
```

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

### 5.2.8 Yielding multiple result slots

Iterator protocols may yield more than one ordinary result slot when the iterator type explicitly declares that shape.

```camp
struct iter(int x, int y) points()
{
	...
}
```

The `next(...)` protocol writes each yielded slot through caller-provided storage:

```camp
bool next(Y* this, int* current_x, int* current_y);
```

This is a protocol-defined multi-output surface. It does not create a user-defined expanded value type.

If the yielded protocol has multiple current slots, compiler-generated cleanup passes the protocol-defined default cleanup shape.

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

For `T: any`, `T* current` means pointer to the storage form of `T`: materialized storage for compiler-expanded forms, or the fixed instance/storage object for fixed structs, classes, and fixed-size arrays.

An iterator that copies yielded `T` values requires `T: copyable` and the required `sizeof(T)` support. Under `T: any`, generic iterator code may enumerate or expose pointers to `T` storage, but it may not copy a `T` value into or out of the current slot.

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
- expanded yields and failing iterators follow the ordinary compiler-expanded
  form and `thrown` rules

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

The value or values produced by an awaited call are the non-`thrown` parameters of the completion callback.

That means:

- one completion value becomes one ordinary result
- multiple completion values may be bound by deconstruction
- a `thrown` completion parameter is rethrown automatically inside the awaiting function

Examples:

```camp
async int addAsync(int x, int y, thrown CalcError)
{
	return x + y;
}

async void getPointAsync(out int x, out int y, thrown CalcError)
{
	...
}

async int sample(thrown CalcError)
{
	return await addAsync(3, 4);
}

async void samplePoint(thrown CalcError)
{
	auto (x, y) = await getPointAsync();
}
```

Conceptually, the awaited call consumes a completion shape such as:

```camp
once void(int result, thrown CalcError) complete
```

or:

```camp
once void(int x, int y, thrown CalcError) complete
```

and yields the non-error slots to the binding site.

Multiple awaited result slots do not create a general anonymous value.

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
- an expanded result is skipped only if every lowered component is default

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
	log(value);
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

If the yielded type is compiler-expanded, cleanup still uses the ordinary
first-component cleanup signal rule.

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

For a non-capturing lambda or plain method reference, `auto` infers the plain `fn` form unless the expression is explicitly blessed or the referenced declaration has a matching callable `newtype` ascription.

```camp
auto increment = x => x + 1;
auto asDelegate = (delegate) x => x + 1;
```

```camp
newtype fn bool IntParser(const char[] text, out int value);
bool tryParseInt(const char[] text, out int value) : IntParser { ... }

auto parser = tryParseInt; // IntParser
```

This keeps the distinction between plain functions and context-carrying callables visible when no target type is present, while still preserving nominal callable types that are stated at the declaration site.

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

The resulting value has the ordinary expanded callable shape:

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

A fixed-size array is not captured by value. A scoped callable may refer to fixed-size array storage while the storage remains in scope. An escaped callable must capture a pointer to suitable storage or another copyable value; it may not copy the fixed-size array itself into the callable context.

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

If an anonymous delegate type is produced from a member method, qualifiers written on an explicit `this` parameter persist onto the resulting hidden context parameter. If the member method has a matching callable `newtype` ascription, the same bound method reference has that named callable `newtype` instead of the anonymous delegate or iterator type.

When the target context-carrying callable type has an explicit callable `this` parameter, those qualifiers are enforced at the method-reference conversion. A callable requiring `const this` accepts only methods callable through a const receiver. A callable requiring `escaped this` accepts only receiver contexts that satisfy the escaped receiver requirement.

So Camp does not need a separate runtime category for bound methods. They are ordinary callable values whose context happens to contain a receiver. Callable `newtype` ascription names that callable value when the declaration supplies a compatible nominal callable contract.

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
- non-capturing lambdas and plain method references target-type to `fn` unless a matching callable `newtype` ascription supplies a named callable type
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

This section defines the generic model itself. It assumes the ordinary rules for
compiler-expanded forms, storage materialization, interfaces, lifetimes,
`within`, constructors, and destructors that were defined earlier.

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
| erased non-copying value constraint | `T: any` | `T` may be any type supported by the erased model, but `T` values are not copyable under this constraint |
| erased copyable value constraint | `T: copyable` | `T` may be any type supported by the erased model that is copyable by value |
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

### 6.1.6 `copyable`

`T: copyable` is an erased value constraint for generic code that needs ordinary value copying, assignment, value storage, or value return.

Direct class types, fixed structs, and fixed-size array value types do not satisfy `copyable`. Pointer types do satisfy `copyable`, including pointers to classes, fixed structs, and fixed-size arrays, because the pointer value itself is copyable.

```camp
class ValueBox<T: copyable>
{
	T value;
}

fixed struct ParserState
{
	nuint position;
}

class Widget
{
}

ValueBox<int> a;            // OK
ValueBox<ParserState> b;    // ERROR
ValueBox<Widget> c;         // ERROR
ValueBox<byte[32]> d;       // ERROR
ValueBox<ParserState*> e;   // OK
ValueBox<Widget*> f;        // OK
ValueBox<byte[32]*> g;      // OK
```

A generic operation that copies `T` values under `T: copyable` also requires `sizeof(T)` when the erased lowering needs the storage size.

### 6.1.7 `implements IFoo`

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
class AnySlots<T: any>
{
	T* items;
	nuint capacity;
	Allocator* allocator;

	AnySlots(sizeof(T), within allocator)
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
- fixed-size arrays
- compiler-expanded forms through their materialized storage representation
- other forms supported by the language

In this model, the generic body does not rely on one fixed source-level representation for every `T`. It also does not assume that `T` is copyable. A body constrained only by `T: any` may not copy, assign, return, or otherwise transport `T` values by value.

A generic type that stores only a pointer to `T` may accept fixed values through `T: any`:

```camp
class Box<T: any>
{
	T* ptr;
}

Box<byte[32]> fixedArrayBox; // OK: stores byte[32]*
```

### 6.2.2 `in` is transport, not pointer semantics

In erased generic code, `in` becomes especially important.

An `in T` parameter:

- behaves like a by-value parameter in source
- is passed as a hidden pointer in the ABI
- does not expose pointer mechanics at the call site
- does not by itself change the lifetime category of the logical value

```camp
void inspect<T: any>(in T item, sizeof(T))
{
	log(sizeof(T));
}
```

This is a common style for erased generic input parameters when the routine only observes the value.

The address of an `in T` parameter refers to the address of the local transport image for the call, and therefore has an implicit scoped lifetime.

Use `T*` instead of `in T` only when the API truly needs one of these:

- stable shared storage
- mutation through that storage
- explicit address identity
- a pointer that may be kept or returned

### 6.2.3 Copying is not available under `T: any`

`T: any` is a non-copying erased constraint. The following operations are invalid in a body constrained only by `T: any` because they require copying, assigning, returning, or transporting a `T` value by value:

```camp
void copyOne<T: any>(T* dst, T* src, sizeof(T))
{
	*dst = *src; // ERROR
}
```

```camp
void copySecond<T: any>(T* dst, T* src, sizeof(T))
{
	dst[1] = src[1]; // ERROR
}
```

```camp
T getValue<T: any>(T* src)
{
	return *src; // ERROR
}
```

`sizeof(T)` may permit pointer indexing, enumeration, size-based allocation, and default-fill. It does not make `T: any` copyable.

Use `T: copyable` when the generic body must copy, assign, pass, return, store, move, or otherwise transport `T` values by value.

### 6.2.4 Expanded forms and storage

Compiler-expanded forms such as `T[]`, `T?`, and `delegate` do not recursively expand inside erased generic bodies.

When erased generic code needs storage for such a type, it uses the materialized storage form:

```camp
struct(T[])
struct(T?)
struct(delegate void())
```

This distinction matters sharply in erased generics because `T` may denote an expanded value that does **not** already exist as one storage object in non-generic source.

### 6.2.5 The meaning of `T*` in erased generic code

Outside erased substitution, pointer rules are ordinary.

Inside erased substitution of `<T: any>` or `<T: copyable>`, `T*` means a pointer to the storage form of `T`. For compiler-expanded forms, this is the materialized storage form. For fixed structs, classes, and fixed-size arrays, it is a pointer to the fixed instance or fixed storage object.

So for an expanded type in this context:

> `T*` means `struct(T)*`.

For a fixed-size array substitution such as `T = byte[32]`:

> `T*` means `byte[32]*`.

A non-materialized expanded value does not automatically provide the right pointer type. If code needs a real pointer to the value as a whole, it must materialize storage first.

### 6.2.6 `T: copyable` and erased value copying

`T: copyable` is stronger than `T: any`. A type parameter known to satisfy `T: copyable` may be used where the same type is required under `T: any`. The reverse is invalid because `T: any` may be a class, fixed struct, or fixed-size array.

```camp
void inspect<T: any>(T* value, sizeof(T))
{
	...
}

void useCopyable<T: copyable>(T* value, sizeof(T))
{
	inspect<T>(value); // OK
}
```

```camp
class List<T: copyable>
{
	...
}

void useAny<T: any>(T* value, sizeof(T))
{
	List<T> list; // ERROR
}
```

A copy operation in erased generic code requires both the `copyable` constraint and an available `sizeof(T)` parameter when the lowered operation needs the size.

```camp
void copyOne<T: copyable>(T* dst, T* src, sizeof(T))
{
	*dst = *src; // OK
}

void badCopy<T: copyable>(T* dst, T* src)
{
	*dst = *src; // ERROR: sizeof(T) is required for erased copy lowering
}
```

### 6.2.7 Arrays and optionals in generic code

Arrays and optionals remain compiler-reserved built-in type forms. They are not ordinary library generics.

#### Arrays

An array type `T[]` is an expanded form with `elements` and `length` components. In ordinary source, a binding named `items` exposes `items.elements` and `items.length`; the ABI symbols remain `items` and `items_length`.

In a `T: any` context, `T[]` may describe a span of `T` storage. If `T` is a compiler-expanded form, the span element storage uses the materialized storage form. If `T` is a fixed struct, class, or fixed-size array, indexing the span yields a fixed value lvalue and copying from that lvalue is invalid.

`sizeof(T)` permits enumeration because it gives the element stride. It does not permit copying under `T: any`.

```camp
void countItems<T: any>(T[] items, sizeof(T), out nuint count)
{
	count = 0;
	for (nuint i = 0; i < items.length; i++)
		count++;
}
```

#### Optionals

`T?` is a compiler-reserved expanded form with the usual optional semantics. Generic code treats it as a built-in type transformation. When storage is needed for the optional as a whole, the materialized form is `struct(T?)`.

### 6.2.8 Delegates and strings in generic code

Delegates are compiler-expanded forms. When a generic API needs to store a delegate as a whole, it stores the materialized delegate form.

string types are pointer-shaped primitive values, so they behave like other pointer-shaped values in generic code while retaining their zero-termination contract. Counted text uses character arrays and follows ordinary array rules.

### 6.2.9 Lifetimes and async

The lifetime system applies to erased generic values through their stored or transported representation.

If `T` contains pointers after substitution, then `in T`, `out T`, return `T`, array elements, optional payloads, delegate contexts, async frames, and iterator frames are all checked through the ordinary aggregate/container rule.

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

For `T: any`, `sizeof(T)` is the size of the concrete substituted type’s storage form. For compiler-expanded forms, that is the materialized storage form. For fixed structs, classes, and fixed-size arrays, it is the size of the fixed instance or fixed storage object.

`sizeof(T)` is not supplied automatically merely because an operation might copy. Generic code must request it explicitly when it needs size-based storage, allocation, default-fill, pointer indexing, or erased copy lowering.

For `T: any`, `sizeof(T)` permits size-based operations and enumeration, but never permits copying `T` values. For `T: copyable`, a generic copy operation also requires `sizeof(T)` when the erased lowering needs the storage size.

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
		this.retain();
	}
}
```

### 6.3.6 Example: size-aware erased storage

```camp
class RawSlots<T: any>
{
	T* items;
	nuint capacity;
	Allocator* allocator;

	RawSlots(nuint capacity, sizeof(T), within allocator)
	{
		this.allocator = allocator;
		this.capacity = capacity;
		this.items = (T*)allocator.alloc(sizeof(T) * capacity);
	}
}
```

The generic class asks for the storage size explicitly and uses it through ordinary allocator operations. It does not copy initialized `T` values.

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

A generic interface is just another generic declaration category. Generic substitution affects the declared entry signatures, but the interface-instance representation remains the ordinary vtable-pointer-slot model.

### 6.4.3 Generic interface methods

Interface methods may also declare their own generic parameters.

```camp
interface ITransformer
{
	TResult transform<TSource: any, TResult: any>(in TSource value);
}
```

This is allowed under the ordinary erased generic model. Generic method parameters affect the method's ordinary parameters and results; they do not add type-specific context parameters to the interface slot.

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
	void append(string text);
	string takeString();
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

	void writeLine(string text)
	{
		this.builder.append(text);
		this.builder.append("\n");
	}

	string finish()
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
class RawSlots<T: any>
{
	T* items;
	nuint capacity;
	Allocator* allocator;

	RawSlots(nuint capacity, sizeof(T), within allocator)
	{
		this.allocator = allocator;
		this.capacity = capacity;
		this.items = allocator.alloc<T>(capacity, sizeof(T));
	}
}
```

This is ordinary erased storage management, not a separate generic allocation subsystem. Generic containers that copy, move, return, or compact initialized `T` values require `T: copyable` and the needed `sizeof(T)` support.

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

The library surfaces described here build directly on the language rules already
defined for arrays, strings, compiler-expanded forms, iterators, async
functions, property accessors, and allocators. This section focuses on the
public library shape and intended usage.

## 7.1 Arrays and Strings

This section sketches the standard library surface for arrays, zero-terminated strings, and counted text.

### 7.1.1 Arrays

Arrays are compiler-expanded values with an element pointer and a length component. A binding named `items` exposes `items.elements` and `items.length`; the ABI symbols remain `items` and `items_length`.

Fixed-size arrays are not compiler-expanded values, but they convert to the matching array span and therefore use the same array APIs when a span receiver is expected.

#### 7.1.1.1 Array API design

The ordinary array API is written around explicit element type, length, and storage operations.

Representative surface:

```camp
scoped T[] slice<T: any>(const T[] this, @range nuint index = 0, nuint count = ^0, sizeof(T));
scoped T* addressOf<T: any>(T[] this, @index nuint index, sizeof(T));
T[] copy<T: copyable>(const T[] this, within allocator, sizeof(T));
```

The methods operate on the array's direct components. For a receiver named `items`, implementations use `items.elements` and `items.length`.

#### 7.1.1.2 Searching, comparison, and mutation

Non-copying generic helpers may use `T: any` with `sizeof(T)` where erased storage requires element stride. Helpers that copy or write a supplied `T` value into array storage use `T: copyable` and `sizeof(T)`.

```camp
nint indexOf<T: any>(const T[] this, in T match, sizeof(T));
bool contains<T: any>(const T[] this, in T match, sizeof(T));
void fill<T: copyable>(T[] this, in T value, sizeof(T));
```

Elementwise equality and comparison are library operations. Raw array equality compares the expanded components, not the sequence contents.

#### 7.1.1.3 Indexing and slicing

Raw element indexing uses `[]`.

```camp
int first = values[0];
int last = values[^1];
```

Slicing is expressed through `slice(...)` methods with `@range` parameters:

```camp
auto middle = values.slice(2..^1);
```

#### 7.1.1.4 Allocating array operations

Copy-producing array operations take an allocator:

```camp
T[] copy<T: copyable>(const T[] this, within allocator, sizeof(T));
```

The returned array owns the allocated element storage only according to the API contract that produced it. The array type itself is just pointer plus length. Copy-producing APIs require `T: copyable` because they copy element values.

#### 7.1.1.5 Arrays of expanded values

Direct arrays of non-materialized expanded values are invalid:

```camp
int?[] values;            // ERROR
char[][] lines;           // ERROR
delegate void()[] calls;  // ERROR
```

Use materialized element storage instead:

```camp
struct(int?)[] values;
struct(char[])[] lines;
struct(delegate void())[] calls;
```

This keeps the array ABI conventional and prevents recursive expansion.

Generic standard-library types and methods use `T: copyable` when they store, copy, move, compact, swap, or return `T` values. For example, a list or vector that owns contiguous element storage and moves elements during growth or removal is declared with `T: copyable`, not `T: any`. Generic APIs that only observe, enumerate, address, or default-fill storage may use `T: any`.

### 7.1.2 Strings and Counted Text

Camp distinguishes zero-terminated string pointer types from counted character
arrays.

| Family | string type | Counted type | Units |
|---|---|---|---|
| UTF-8 | `string` | `char[]` | `char` |
| UTF-16 | `wstring` | `wchar[]` | `wchar` |
| ASCII / system code page | `astring` | `achar[]` | `achar` |

The string types are primitive pointer-shaped keywords: `string`, `wstring`,
and `astring`. They point to const data. Mutable text buffers are ordinary
character arrays such as `char[]`.

#### 7.1.2.1 Zero-terminated strings and counted arrays

A `string` is a zero-terminated UTF-8 string pointer to const data. A `char[]`
is a counted UTF-8 code-unit sequence.

The same relationship applies to the UTF-16 and ASCII families.

Use string types for C-style zero-terminated APIs and compact pointer-shaped
string storage. Use counted character arrays when code needs an explicit length,
slicing, bounded access, or mutable buffer storage.

#### 7.1.2.2 Ownership and release

The core string type names describe representation, not ownership.

An API that allocates a string must state the matching release operation. An API that borrows a string must express the lifetime of the pointer through ordinary lifetime annotations.

The primitive string types do not define destructors.

#### 7.1.2.3 Length and null termination

string length is computed by scanning for the terminator:

```camp
nuint getLength(string this);
```

So:

```camp
string text = "hello";
nuint len = text.Length; // calls getLength()
```

A counted character array carries its length directly:

```camp
const char[] text = "hello";
nuint len = text.length;
```

#### 7.1.2.4 String literals

String literals are constant data. The default inferred type is `string`, and a
literal may target only string, const character-array, or const
character-pointer forms.

```camp
auto text = "hello";       // string
wstring wide = "hello";
const char[] view = "hello";
const char* raw = "hello";

char[] mutableView = "hello"; // ERROR
char* mutableRaw = "hello";   // ERROR
```

#### 7.1.2.5 Character encodings and scalar conversions

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

#### 7.1.2.6 Counted text indexing and slicing are code-unit based

Counted text indexing and slicing use code-unit positions, not Unicode character positions.

Examples:

```camp
const char[] text = "héllo";

nint i = text.indexOf("ll");
uchar ch = text.getChar(2);
nuint width = text.getCharUnits(^1);
auto tail = text[2..];
```

Important consequences:

- `indexOf(...)`, `slice(...)`, and related methods speak in units
- `getChar(...)` decodes the code point beginning at a code-unit position
- `countChars()` is a decoding operation and may therefore be `O(n)`

#### 7.1.2.7 Current counted text operations

The counted text API is intentionally broad enough for ordinary text work but still small enough to remember.

The common methods are conceptually duplicated across the UTF-8, UTF-16, and ASCII counted families.

##### Searching and comparison

```camp
nint indexOf(const char[] this, const char[] match, bool caseInsensitive = false);
nint indexOfChar(const char[] this, uchar match, bool caseInsensitive = false);
nint indexOfAnyChar(const char[] this, const uchar[] matches, bool caseInsensitive = false);

nint lastIndexOf(const char[] this, const char[] match, bool caseInsensitive = false);
nint lastIndexOfChar(const char[] this, uchar match, bool caseInsensitive = false);
nint lastIndexOfAnyChar(const char[] this, const uchar[] matches, bool caseInsensitive = false);

bool startsWith(const char[] this, const char[] match, bool caseInsensitive = false);
bool startsWithChar(const char[] this, uchar match, bool caseInsensitive = false);
bool endsWith(const char[] this, const char[] match, bool caseInsensitive = false);
bool endsWithChar(const char[] this, uchar match, bool caseInsensitive = false);

nint compareTo(const char[] this, const char[] other, bool caseInsensitive = false);
```

##### Unicode-aware access

```camp
nuint countChars(const char[] this);
uchar getChar(const char[] this, @index nuint unit);
nuint getCharUnits(const char[] this, @index nuint unit);
```

##### Borrowing transformations

```camp
scoped const char[] trim(const char[] this);
scoped const char[] trimStart(const char[] this);
scoped const char[] trimEnd(const char[] this);
scoped char[] slice(const char[] this, @range nuint index = 0, nuint count = ^0);
```

These operations return counted views rather than allocating new storage.

##### Copy-producing transformations

```camp
string stringCopy(const char[] this, within allocator);
string uppercaseCopy(const char[] this, within allocator);
string lowercaseCopy(const char[] this, within allocator);

string concatCopy(string[] this, within allocator);
string joinCopy(string[] this, const char[] separator, within allocator);
```

The naming is intentional:

- `stringCopy(...)` produces zero-terminated string storage
- `uppercaseCopy(...)` and `lowercaseCopy(...)` allocate
- `concatCopy(...)` and `joinCopy(...)` allocate

#### 7.1.2.8 Family-specific conversions

Each counted text family can copy into any string family. The conversion helpers
allocate and append the target terminator.

From UTF-8 counted text:

```camp
string copyString(const char[] this, within allocator);
wstring copyWString(const char[] this, within allocator);
astring copyAString(const char[] this, achar unrepresentable = '?', within allocator);
```

From UTF-16 counted text:

```camp
string copyString(const wchar[] this, within allocator);
wstring copyWString(const wchar[] this, within allocator);
astring copyAString(const wchar[] this, achar unrepresentable = '?', within allocator);
```

From ASCII counted text:

```camp
string copyString(const achar[] this, within allocator);
wstring copyWString(const achar[] this, within allocator);
astring copyAString(const achar[] this, achar unrepresentable = '?', within allocator);
```

The `copyAString` fallback character is used for code points that cannot be
represented in ASCII.

#### 7.1.2.9 string-to-array views

A zero-terminated string may be viewed as a counted character array by scanning
for its terminator. This conversion is compiler-sponsored and is equivalent to
calling the visible length property for the string family.

```camp
string text = "hello";
const char[] view = text;
```

### 7.1.3 `StringBuilder`

`StringBuilder` remains a provisional part of the standard library design, but its intended role is clear enough to note here.

It is the mutation-heavy text-construction utility for cases where repeated string concatenation would be awkward or wasteful.

It stores counted character data internally and can produce either counted views or zero-terminated string copies.

## 7.2 Streams and I/O

This section is provisional library design. The language features it relies on
are real Camp features, but the full stream library surface described here is
not yet the committed standard-library API.

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

For character arrays:

```camp
export CharReader reader(char[] this);
export AsyncCharReader asyncReader(char[] this);

export WCharReader reader(Wchar[] this);
export AsyncWCharReader asyncReader(Wchar[] this);

export ACharReader reader(Achar[] this);
export AsyncACharReader asyncReader(Achar[] this);
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
	static ByteReader openRead(const char[] path, FileMode mode = OPEN_EXISTING, FileOptions options = default, thrown IoError);
	static AsyncByteReader openReadAsync(const char[] path, FileMode mode = OPEN_EXISTING, FileOptions options = default, thrown IoError);

	static ByteWriter openWrite(const char[] path, FileMode mode = CREATE_OR_TRUNCATE, FileOptions options = default, thrown IoError);
	static AsyncByteWriter openWriteAsync(const char[] path, FileMode mode = CREATE_OR_TRUNCATE, FileOptions options = default, thrown IoError);

	static FileHandle* open(const char[] path, FileAccess access, FileMode mode, FileOptions options = default, thrown IoError);
	static async FileHandle* openAsync(const char[] path, FileAccess access, FileMode mode, FileOptions options = default, thrown IoError);

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

The currently implemented console surface is a small static helper class for
writing to standard output:

```camp
export class Console
{
	export static void writeString(const char[] value);
	export static void writeLine(const char[] value = default);
	export static void writeBool(bool value);
	export static void writeChar(char value);
	export static void writeInt(int value);
	export static void writeUInt(uint value);
	export static void writeDouble(double value);
	
	// These are planned:
	
	export static extern CharReader getReader();
	export static extern AsyncCharReader getAsyncReader();

	export static extern CharWriter getWriter();
	export static extern AsyncCharWriter getAsyncWriter();

	export static extern CharWriter getError();
	export static extern AsyncCharWriter getAsyncError();
}
```

Reader/writer console streams are planned library design and are described
below as part of the broader stream model.

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
export thrown(IoError) writeString(CharWriter this, char[] value);
export thrown(IoError) writeLine(CharWriter this, char[] value = default);

export thrown(IoError) writeBool(CharWriter this, bool value);
export thrown(IoError) writeInt(CharWriter this, int value);
export thrown(IoError) writeLong(CharWriter this, long value);
export thrown(IoError) writeUInt(CharWriter this, uint value);
export thrown(IoError) writeULong(CharWriter this, ulong value);
export thrown(IoError) writeFloat(CharWriter this, float value);
export thrown(IoError) writeDouble(CharWriter this, double value);

export async void writeStringAsync(AsyncCharWriter this, char[] value, thrown IoError);
export async void writeLineAsync(AsyncCharWriter this, char[] value = default, thrown IoError);
```

Equivalent `WChar*` and `AChar*` helper families exist for UTF-16 and ASCII/system-code-page streams.

#### Text-reading helpers

UTF-8 forms:

```camp
export string readAllCopy(CharReader this, within allocator, thrown IoError);
export async string readAllCopyAsync(AsyncCharReader this, within allocator, thrown IoError);

export iter nuint readLines(CharReader this, char[] buffer, thrown IoError);
export async iter nuint readLinesAsync(AsyncCharReader this, char[] buffer, thrown IoError);

export iter char[] iterateLines(CharReader this, char[] buffer, thrown IoError);
export async iter char[]? iterateLinesAsync(AsyncCharReader this, char[] buffer, thrown IoError);
```

Equivalent `WChar*` and `AChar*` reading helper families exist for UTF-16 and ASCII/system-code-page streams.

The distinction between the two line-reading styles is intentional:

- `readLines(...)` yields counts and stays closer to the core protocol
- `iterateLines(...)` yields line views directly and is more ergonomic

### 7.2.8 Example patterns

#### Reading a binary file

```camp
using Std::IO;

void dumpBytes(const char[] path, thrown IoError)
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

async void dumpBytesAsync(const char[] path, thrown IoError)
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

void writeData(const char[] path, const byte[] data, thrown IoError)
{
	auto writer = FileHandle.openWrite(path) finally delete;
	writer.writeAll(data);
}
```

Manual form without helpers:

```camp
using Std::IO;

void writeDataManually(const char[] path, const byte[] data, thrown IoError)
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

void printTextFile(const char[] path, thrown IoError)
{
	auto reader = FileHandle.openRead(path) finally delete;
	char[] lineBuffer = init char[512];

	foreach (char[] line in reader.CharReader.iterateLines(lineBuffer))
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

void writeTextFile(const char[] path, thrown IoError)
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

	foreach (char[] line in Console.getReader().iterateLines(lineBuffer))
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
