# Enums, Newtypes, And Inline Constants

Enums, newtypes, and inline constants give names to values without introducing
the full object model of structs and classes. They are central to Camp API
design because they preserve intent across source, metadata, and ABI output.

Use an enum when a value is one of a named set. Use a `newtype` when an
existing representation needs a distinct nominal type. Use an `inline`
constant when a compile-time value should be part of the source or ABI surface.

## Enums

An `enum` introduces a nominal integer type whose values are named constants.

```camp
export enum Direction
{
	North,
	South,
	East,
	West
}
```

Enums are useful for mode switches, state machines, status codes, error
categories, option selection, and any public API where a raw integer would hide
meaning.

```camp
export enum HttpMethod
{
	GET,
	POST,
	PUT,
	PATCH,
	DELETE
}
```

Enum values can be used in expressions, switches, metadata, inline constants,
and signatures.

## Enum Underlying Types

If the underlying type is omitted, the enum uses `uint`.

An explicit underlying type may be one of the integral primitive types,
including natural integer types:

```camp
export enum FileMode: ushort
{
	OPEN_EXISTING,
	CREATE = 7,
	APPEND
}
```

Supported explicit underlying categories are `sbyte`, `byte`, `short`,
`ushort`, `int`, `uint`, `long`, `ulong`, `nint`, and `nuint`.

Member values are evaluated as Camp constant expressions and checked against
the underlying type. Auto-increment continues from the previous member value.
Overflow is an error, and negative values are invalid for unsigned underlying
types.

```camp
enum Small: byte
{
	A = 255,
	B        // ERROR: would be 256
}

enum UnsignedStatus: uint
{
	NEGATIVE = -1 // ERROR
}
```

## Enum Values

An enum value may be plain or explicitly initialized:

```camp
enum ParseState
{
	START = 0,
	HEADER,
	BODY = 10,
	TRAILER
}
```

In this example, `HEADER` is `1` and `TRAILER` is `11`.

Enum value names live in the enum's member scope and are also available through
qualified member access:

```camp
ParseState state = ParseState.HEADER;
```

Use qualification in examples and public-facing code when it makes the enum
source obvious.

## Enums In Error And Status APIs

Camp's thrown-value convention treats the default value of the error type as
success. For status or error enums, make the success value explicit and assign
it `0`.

```camp
export enum IoError
{
	OK = 0,
	E_NOT_FOUND,
	E_ACCESS_DENIED,
	E_INTERRUPTED
}
```

This is a convention, not a special enum-only rule. It aligns the enum with
ordinary default-value semantics and with `thrown` checking.

## Enum ABI Representation

Camp enums do not rely on target C `enum` layout. Exported C-facing output uses
a typedef of the fixed underlying representation plus typed member constants.
This keeps ABI width precise on every target.

```camp
@symbol("Difficulty")
export enum DifficultyLevel: ushort
{
	@symbol("DIFFICULTY_EASY") EASY,
	HARD = EASY + 100
}
```

Conceptually, the public C surface is shaped like:

```c
typedef uint16_t Difficulty;
#define DIFFICULTY_EASY ((Difficulty)0)
#define Difficulty_HARD ((Difficulty)100)
```

The exact emitted C spelling is target-owned, but the contract is stable:
the enum has the ABI width of its underlying type.

## Enum Symbols And Metadata

`@symbol` on the enum type overrides the exported type symbol and default
member prefix. `@symbol` on a member overrides the member's full exported
constant symbol.

Enum metadata includes the computed numeric value for each visible value. The
metadata value is the result after evaluating explicit initializers,
auto-increment, and underlying-type checks.

Use `@symbol` only when an external ABI or wire contract requires a particular
native spelling.

## `newtype`

`newtype` introduces a real nominal boundary over an existing representation.

```camp
export newtype UserId: uint;
export newtype WindowHandle: nint;
export newtype NativeBufferPtr: void*;
```

These values may have ordinary integer or pointer representations at the ABI
level, but Camp treats the named types as distinct.

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

That distinction is the purpose of `newtype`: machine compatibility is not the
same thing as source compatibility.

## Value Newtypes

A value `newtype` uses this form:

```camp
newtype TypeName: UnderlyingType;
```

Allowed value underlyings are numeric types and pointer types:

```camp
newtype PixelCount: int;
newtype UserToken: ulong;
newtype DataPtr: byte*;
newtype OpaqueState: void*;
```

Disallowed value underlyings include arrays, optionals, fixed-size arrays,
structs, classes, interfaces, and other aggregate or expanded forms:

```camp
newtype Values: int[];      // ERROR
newtype Bytes: byte[8];     // ERROR
newtype MaybeId: int?;      // ERROR
newtype Client: HttpClient; // ERROR
```

Crossing a value-newtype boundary is explicit unless a specific built-in rule
says otherwise.

## Callable Newtypes

Callable `newtype`s name callable shapes.

```camp
export newtype fn int Parser(const char[] text, out int value);
export newtype delegate bool Predicate(int value);
export newtype iter char CharReader(const this);
export newtype async int Loader(const char[] path, thrown IoError);
```

Use callable newtypes when a callback or iterator shape is part of a public
contract, appears in many places, or has important defaults, call specs,
receiver qualifiers, or thrown slots.

A context-carrying callable newtype may declare an explicit callable `this`
parameter to qualify the hidden context:

```camp
newtype delegate nuint Formatter(const this, char[] buffer = default);
newtype once void Completion(escaped this, int result);
```

The explicit callable `this` parameter is not an ordinary argument supplied by
callers. It describes the callable context carried by a bound receiver, lambda,
delegate, iterator frame, async frame, or postponed operation.

## Callable Ascription

A compatible function or method can ascribe a callable newtype after its
parameter list. The ascription names the declaration's natural callable
reference form.

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

struct Date
{
	nuint format(char[] buffer = default): CharFormatter
	{
		...
	}
}
```

Callable ascription does not generate wrappers, thunks, allocation, or closure
objects. The declaration's natural callable reference must already be
compatible with the ascribed callable newtype.

Receiverless declarations ascribe `fn` newtypes. Receiver-bearing declarations
ascribe context-carrying callable newtypes accepted by the language, such as
`delegate` and `iter`.

## Newtype Members

A `newtype` may open a member scope containing ordinary methods:

```camp
newtype NativeHandle: nint
{
	bool isValid()
	{
		return this != 0;
	}

	void close()
	{
		closeNativeHandle(this);
	}
}
```

A value newtype may not contain fields, virtual methods, constructors, or
destructors. It is a nominal wrapper over an existing representation, not a
struct or class.

```camp
newtype Counter: int
{
	int value;        // ERROR
	Counter();        // ERROR
	~Counter();       // ERROR
	virtual void f(); // ERROR
}
```

When a newtype wraps an owned resource such as a native handle, express cleanup
as an ordinary method and use `finally close()`:

```camp
NativeHandle handle = openNativeHandle(path) finally close();
```

`delete handle` is not a resource close operation. `delete Newtype*` means
pointer-storage deletion for a pointer to newtype storage, not semantic cleanup
of the wrapped value.

## Newtype `this`

Inside a newtype instance method, `this` is passed by value and is read-only.

```camp
newtype Counter: int
{
	int next()
	{
		return this + 1;
	}
}
```

This differs from struct and class instance methods, whose implicit receivers
are pointer-shaped. A method declared inside `newtype Handle: nint` has the
source/ABI character of `Handle_method(Handle thisValue)`, not
`Handle_method(Handle* thisPointer)`.

If pointer receiver behavior is needed, write an explicit receiver on an
out-of-scope extension method:

```camp
void reset(Counter* this)
{
	*this = (Counter)0;
}
```

## Newtype ABI Representation

A newtype has the same ABI representation as its underlying structural form.
Passing, returning, and storing it use the same machine-level representation as
the underlying type.

For exported value newtypes, generated C-facing output uses a typedef-like
surface:

```camp
export newtype NativeHandle: nint;
```

Conceptually:

```c
typedef intptr_t NativeHandle;
```

For callable newtypes, the named callable contract survives in exported
signatures rather than being flattened into an anonymous callable spelling at
every use site.

## Inline Constants

`inline` constants are compile-time values.

```camp
export inline uint MaxPlayers = 9;
public inline string InternalName = "Camp";
inline uint PrivateLimit = MaxPlayers + 1;
```

Inline constants have no ordinary storage. Their values are emitted at use
sites, metadata, or generated headers as needed.

An inline constant must have an initializer:

```camp
inline uint MissingInitializer; // ERROR
```

The initializer must be a compile-time constant expression valid for the
declared inline type.

## Inline Constant Types

Inline constants are for scalar, string, enum, and similarly constant-friendly
source values. They are not for aggregate storage, arrays, mutable string
pointers, or fixed array pointer storage.

```camp
inline uint HeaderMessage = 0x000E;
inline nuint NaturalBytes = sizeof(nuint);
```

Invalid examples:

```camp
struct Point
{
	int x;
	int y;
}

inline Point Origin = default;      // ERROR
inline int[] Values = default;      // ERROR
inline char* MutableText = "mutable"; // ERROR
```

Inline constants may refer to other inline constants and enum values, provided
the dependency graph is acyclic and every value can be computed at compile
time.

```camp
inline uint PageSize = 4096;
inline uint DoublePage = PageSize * 2;
```

Cycles are errors:

```camp
inline uint First = Second; // ERROR
inline uint Second = First; // ERROR
```

## Type-Scope Inline Constants

Types may contain inline constants in their member scope.

```camp
export struct Limits
{
	inline uint DefaultCapacity = 16;
	export inline uint ExportedCapacity = DefaultCapacity * 2;
}
```

Type-scope inline constants are addressed through the type's namespace:

```camp
uint capacity = Limits.DefaultCapacity;
```

They have no per-instance storage. Metadata treats them as static type members
with a computed value.

Out-of-scope static inline members can also be attached to eligible owner
types, including primitive owner types when the declaration is valid:

```camp
export inline int int.API_MIN = -1;
```

Use this sparingly; type-scope constants on user-defined types are usually
clearer.

## Inline Constants And Symbols

Exported inline constants appear in generated public ABI surfaces where that
target supports constant emission. Private inline constants remain internal to
the compilation view.

`@symbol` may override the emitted symbol:

```camp
@symbol("MAX_CONNECTIONS")
export inline uint MaxConnections = 64;
```

The override must be a valid symbol for the selected target and output kind.
Avoid symbol overrides unless a native header or ABI contract requires them.

## Choosing Between The Three

Use an enum when:

- the value is one of a closed named set;
- switches and status/error categories need readable cases;
- the ABI should expose a precise integer width and named constants.

Use a value newtype when:

- the representation is already right;
- different semantic values share a representation;
- accidental interchange would be a bug;
- no fields, constructors, or destructors are needed.

Use an inline constant when:

- the value is a compile-time constant;
- consumers should see a named source/API value;
- no storage, identity, or nominal type boundary is needed.
