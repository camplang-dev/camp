+++
nav_title = "11. Enums And Newtypes"
+++

# Enums, Newtypes, And Inline Constants

Some names should make a program clearer without making the value heavier.
An error code wants a small closed set of meanings. A native handle wants to
stop looking like any other integer. A limit or protocol value wants to appear
in the public API without becoming storage.

That is the work of enums, newtypes, and inline constants. They sit close to
the ABI, but they are not just decoration over integers. They tell Camp, its
metadata, and its readers which values are interchangeable and which ones are
not.

This chapter is about choosing the lightest tool that still carries the
meaning you need.

## A Small API Shape

A public native-facing API often ends up with all three tools:

```camp
export enum OpenError
{
	OK = 0,
	NOT_FOUND,
	ACCESS_DENIED
}

export newtype NativeResourceHandle: nint
{
	bool isValid()
	{
		return (nint)this != 0;
	}
}

export inline uint DEFAULT_BUFFER_SIZE = 4096;

NativeResourceHandle openFile(const char[] path, thrown OpenError error);
void closeFile(NativeResourceHandle handle);
```

`OpenError` gives names to the failure cases. `NativeResourceHandle` keeps a native
handle from being casually mixed with every other `nint`. `DEFAULT_BUFFER_SIZE`
is visible to callers without creating a global variable.

None of those declarations says "make an object." They say "keep this small
value meaningful."

## Enums For Named Choices

An `enum` declares a nominal integer type whose values are named.

```camp
enum FileKind
{
	REGULAR,
	DIRECTORY,
	DEVICE
}
```

Use an enum when the set of values is part of the contract:

- modes and states;
- status and error values;
- small protocol tags;
- options where a raw integer would hide intent.

Enum values are constants in the enum's member scope, but you do not usually
need to write the enum type when the target type is already known:

```camp
void inspectFile()
{
	FileKind kind = REGULAR;
	describeFile(DIRECTORY);
}

void describeFile(FileKind kind)
{
}
```

Camp can see from the variable type and the parameter type that `REGULAR` and
`DIRECTORY` are `FileKind` values.

Use qualification when the target type is not known, or when it makes a dense
piece of source easier to read:

```camp
auto kind = FileKind.DEVICE;
```

Qualification is also the way to break ties if two visible enum types use the
same member name and the surrounding expression does not already decide which
enum you mean.

## Underlying Types And Numbering

If you do not choose an underlying type, an enum uses `uint`. When ABI width
matters, write the width explicitly:

```camp
export enum PacketKind: ushort
{
	HELLO = 1,
	DATA,
	GOODBYE = 100
}
```

Explicit underlying types may be `sbyte`, `byte`, `short`, `ushort`, `int`,
`uint`, `long`, `ulong`, `nint`, or `nuint`.

Values auto-increment from the previous value. In the example above, `DATA` is
`2`. Camp checks that every value fits in the underlying type:

```camp
enum Small: byte
{
	LAST = 255
	// Another unassigned value here would be an error.
}
```

For exported enums, the underlying type is not trivia. It is the ABI width of
the value.

## Status And Error Enums

Camp's `thrown` convention treats the default value of the thrown type as
success. If an enum is used as a status or error type, make the success value
explicit and put it first:

```camp
enum ParseError
{
	OK = 0,
	EMPTY,
	INVALID_DIGIT
}
```

That keeps three things lined up:

- `default(ParseError)` means success;
- C-style zero status means success;
- readers can see the success case without hunting.

Do not use a large enum as a substitute for diagnostics. Throw the small
status, and let the API provide a separate way to retrieve detailed context
when that context is needed.

## Enum ABI And Symbols

Camp does not depend on target C enum layout for ABI width. The C-facing shape
of an exported enum is conceptually a typedef of the chosen integer width plus
named constants.

```camp
@symbol("Difficulty")
export enum DifficultyLevel: ushort
{
	@symbol("DIFFICULTY_EASY") EASY,
	HARD = EASY + 100
}
```

A C-facing surface may look conceptually like this:

```c
typedef uint16_t Difficulty;
#define DIFFICULTY_EASY ((Difficulty)0)
#define Difficulty_HARD ((Difficulty)100)
```

The exact emitted spelling belongs to the target emitter. The useful rule is
stable: enum width comes from the underlying type, and `@symbol` is how you
pin native names when an external ABI already has them.

Use symbol overrides for native compatibility, not for ordinary style
preferences.

## Value Newtypes

A value `newtype` creates a nominal type over an existing numeric or pointer
representation.

```camp
newtype UserId: uint;
newtype WindowHandle: nint;
newtype NativeBuffer: void*;
```

When the native ABI already has a type name, keep the Camp source name readable
and use `@symbol` for the emitted spelling:

```camp
@symbol("HWND")
export newtype WindowHandle: nint;
```

The representation may be familiar, but the source type is distinct:

```camp
newtype UserId: uint;
newtype GroupId: uint;

void loadUser(UserId id);
void loadGroup(GroupId id);

UserId user = (UserId)10;
GroupId group = (GroupId)10;

loadUser(user);
loadGroup(group);
// loadUser(group); // wrong type
```

That is the point. A `UserId` and a `GroupId` can both be represented as
`uint` and still not mean the same thing.

Use value newtypes for handles, identifiers, flags, native scalar types, and
pointer-shaped tokens where the representation is already right but the
meaning needs protection.

## Newtype Methods

A value newtype can have methods, but it is not a small struct in disguise.
It has no instance fields, constructors, destructors, virtual methods, or
stored object identity.

```camp
newtype NativeResourceHandle: nint
{
	bool isValid()
	{
		return (nint)this != 0;
	}

	void close()
	{
		closeFile(this);
	}
}
```

Inside a value-newtype method, `this` is the value itself and is read-only.
The method receives a `NativeResourceHandle`, not a hidden `NativeResourceHandle*`.

That makes methods a good place for small conveniences and cleanup helpers:

```camp
NativeResourceHandle handle = openFile(path, catch OpenError error) finally close();
```

`finally close()` says how to clean up the native handle. `delete handle` would
not mean "close this resource"; `delete` is for deleting allocated storage.

When a method really needs pointer-style receiver behavior, write an explicit
receiver function:

```camp
void clear(UserId* this)
{
	*this = default;
}
```

## Callable Newtypes As API Names

You already saw callable newtypes in the functions chapter. They belong here
too because they serve the same design purpose as value newtypes: they give a
role a name.

```camp
newtype fn bool ParseInt(const char[] text, out int value);
newtype delegate void ProgressHandler(int completed, int total);
```

Two callbacks can have the same raw shape and still mean different things. A
named callable newtype lets an API say "this is a progress handler" or "this is
a parser" instead of handing readers an anonymous function type and hoping the
parameter name explains enough.

Callable newtypes can also carry defaults, receiver-context qualifiers, call
specifiers, lifetime annotations, and `thrown` slots. Reach for them when the
callback is part of an API contract or appears in more than one place.

## Inline Constants

An `inline` constant is a compile-time value with a name.

```camp
inline uint PAGE_SIZE = 4096;
export inline uint PROTOCOL_VERSION = 3;
```

It has no ordinary storage. The value can appear in metadata, generated API
surface, or emitted code as needed, but callers are not taking the address of a
global variable.

Inline constants must have initializers, and the initializer must be a
compile-time constant expression:

```camp
inline uint HALF_PAGE = PAGE_SIZE / 2;
inline nuint POINTER_BITS = sizeof(nuint) * 8;
```

Inline constants are intended for scalar, enum, string-like, pointer-null, and
function-null values. They are not aggregate storage:

```camp
struct Point
{
	int x;
	int y;
}

// inline Point ORIGIN = default; // aggregate storage, not an inline constant
```

Use an ordinary value, a static field, or a function when the value needs
storage, initialization behavior, or a meaningful address.

## Type-Scoped Constants

An inline constant can live in a type's member scope:

```camp
struct Limits
{
	inline uint DEFAULT_CAPACITY = 16;
	export inline uint EXPORTED_CAPACITY = DEFAULT_CAPACITY * 2;
}

uint capacity = Limits.DEFAULT_CAPACITY;
```

The constant belongs to the type's source namespace. It does not add a field
to each instance.

This is useful when the constant explains the type:

```camp
newtype WindowStyle: uint
{
	static inline WindowStyle VISIBLE = (WindowStyle)0x10000000;
	static inline WindowStyle DISABLED = (WindowStyle)0x08000000;
}
```

Inside a newtype body, spell these as `static inline`. Newtypes do not have
instance fields, so a member value must belong to the type rather than to an
instance.

Type-scoped constants are often better than a cluster of unrelated module
constants because the lookup path carries context.

## Inline Constant Symbols

Exported inline constants may appear in generated native headers where the
target supports constant emission. A symbol override can pin the native name:

```camp
@symbol("MAX_CONNECTIONS")
export inline uint CONNECTION_LIMIT = 64;
```

The override applies to the emitted constant symbol, not to source lookup.
Camp code still uses `CONNECTION_LIMIT`.

For enum declarations and enum members, `@symbol` controls the enum typedef
symbol and member constant symbols. For `class`, `struct`, `interface`, and
`newtype` declarations, `@symbol` controls the effective native type symbol and
the default prefix used by static members and generated ABI helpers. Use the
symbol rules in the names chapter for the full placement and precedence rules.

## Small Names, Stronger APIs

The common thread is not size; it is intent.

An enum says a value belongs to a closed vocabulary. A value newtype says two
values with the same representation are still not interchangeable. A callable
newtype says a callback has a role, not just a parameter list. An inline
constant says a value belongs in the API without becoming storage.

These are small tools, but they are often what make a low-level API pleasant to
use. A native boundary may still see integers, pointers, function pointers, and
constants. Camp callers see `OpenError`, `NativeResourceHandle`, `ParseInt`,
and `DEFAULT_BUFFER_SIZE`.

That is the shape to aim for: keep the machine representation simple, but do
not make the reader carry all of the meaning in their head.
