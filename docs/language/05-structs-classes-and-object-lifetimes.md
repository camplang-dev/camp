+++
nav_title = "5. Structs And Classes"
+++

# Structs, Classes, And Object Lifetimes

Camp gives you both plain values and identity-bearing objects. That split is
one of the most important design choices in ordinary Camp code.

Use a `struct` when the value's storage is the point: a small record, a native
layout shape, a header, a coordinate, a span-like value, or a piece of data
owned by the caller or by a containing value. Use a `class` when identity,
allocation, inheritance, virtual dispatch, interface storage, or lifecycle is
the point.

That choice also says something important at the ABI boundary. Struct fields
are visible when a struct is exposed across the ABI. Class fields are not:
outside code can hold a class pointer and call exported functions, but it
cannot reach into the object's fields.

This chapter explains the user-facing model: what the declarations mean, how
construction and destruction work, what `new`, `init`, and `delete` do, and how
object shape affects exported APIs. The exact compiler layout matters at some
boundaries, but the source model is the place to start.

## A Record And An Object

Here is a complete program that uses a struct as a record and a class as an
identity-bearing object:

```camp
export int main()
{
	Position position = { .row = 3, .column = 4 };
	Counter* counter = new Counter();

	counter.increment();
	counter.increment();

	int total = distanceFromOrigin(position) + counter.getValue();
	Console.writeLine(total);

	delete counter;
	return total == 9 ? 0 : 1;
}

int distanceFromOrigin(Position position)
{
	return position.row + position.column;
}

struct Position
{
	int row;
	int column;
}

class Counter
{
	int value;

	void increment()
	{
		this.value += 1;
	}

	int getValue()
	{
		return this.value;
	}
}
```

`Position` is copied and passed as a value. `Counter` is allocated, mutated
through a pointer, and explicitly deleted. That is the everyday distinction:
records are values; objects have identity and lifecycle.

## Structs As Plain Values

A `struct` declares a value-oriented aggregate.

```camp
struct Size
{
	int width;
	int height;
}
```

Use structs for:

- small records and coordinates;
- fixed native layout surfaces;
- packet headers and protocol records;
- groups of values owned by the caller or containing object;
- values that should not imply heap allocation or object identity.

Struct fields are laid out in declaration order for the source model and for
the compiler's emitted representation. That makes field order part of the
struct's meaning, especially when a struct is exported or used at a native
boundary.

```camp
struct PacketHeader
{
	uint magic;
	ushort version;
	ushort flags;
}
```

Assignment copies a struct value according to the rules for its fields. If a
struct contains a pointer or array view, copying the struct copies that pointer
or view; it does not copy the pointed-to storage.

```camp
struct ByteView
{
	const byte[] data;
}
```

`ByteView` stores a counted view. It does not own `data.elements` unless the
API around it says so.

When a struct is part of an exported ABI surface, its fields are part of that
surface too. This is deliberate. Use a struct when callers are meant to see the
shape of the data.

## Fixed Structs

A `fixed struct` is still a struct: it has no inheritance, no hidden dispatch
field, and its fields are visible wherever the struct layout is visible. The
difference is that the instance is fixed in place after it is created. Ordinary
safe code may not assign, pass, return, or extract the instance by value when
that would copy it.

That gives you a useful middle ground. Use `fixed struct` when you want inline
storage and visible layout, but the value has address-sensitive state:

- parser, scanner, or state-machine storage;
- stack-allocated builders;
- synchronization or registration tokens;
- native records that must not be copied after initialization;
- generated iterator or callback context storage.

```camp
fixed struct TextBuilder
{
	char* buffer;
	nuint length;
	nuint capacity;

	TextBuilder(char* buffer, nuint capacity)
	{
		this.buffer = buffer;
		this.length = 0;
		this.capacity = capacity;
	}

	void clear()
	{
		this.length = 0;
	}
}

void reuse(char* scratch)
{
	TextBuilder builder = init TextBuilder(scratch, 256);
	TextBuilder* pointer = &builder;

	builder.clear();
	pointer.clear();

	// TextBuilder copy = builder; // ERROR: fixed structs are not copied by value
}
```

A fixed struct can have ordinary struct members: fields, methods,
constructors, destructors, static members, and interface implementations. It
can be constructed with `init` in local or caller-owned storage, or with `new`
in allocated storage. After that, pass it by pointer when another API needs to
observe or mutate the same instance.

The ABI distinction remains the same as for ordinary structs. An exported
fixed struct exposes its actual fields. If you need address-stable state but
also want to hide the layout from callers, use a class instead.

One practical consequence follows from the copy rule: a copyable struct should
not contain a fixed struct field by value, because copying the outer struct
would copy the fixed field. Make the containing type fixed too, store a
pointer, or use a class when identity and opacity are the better fit.

## Classes As Identity-Bearing Objects

A `class` declares an object type. Class instances are normally used through
pointers.

```camp
class FileHandle
{
	nint nativeHandle;
}
```

Use classes when you need:

- object identity;
- construction and destruction around owned resources;
- virtual methods or inheritance;
- interface implementations that can be stored with the object;
- opaque implementation details behind an exported API;
- reference-like behavior where callers should pass pointers.

A class can have fields, methods, constructors, destructors, static members,
virtual members, and interface implementations. A class object may also have
generated private fields for virtual and interface dispatch. Those generated
fields support the source contract; ordinary source code should reason from the
class declaration, not from generated storage names.

Class fields remain implementation details across the public ABI. Even an
exported class is opaque to external callers: they can pass pointers around,
but they cannot depend on field order, field names, or generated dispatch
storage.

Classes can be `abstract`, `virtual`, `sealed`, or `extern`:

| Form | Meaning |
|---|---|
| ordinary class | Concrete object type without open virtual inheritance by default. |
| `virtual class` | Class intended to participate in virtual dispatch. |
| `abstract class` | Base class with required derived implementation; not directly constructed. |
| `sealed class` | Concrete class that cannot be subclassed. |
| `extern class` | Native or separately implemented class boundary. |

## Fields, Static Members, And Inline Constants

Instance fields store data in each struct or class instance:

```camp
class Counter
{
	int value;
}
```

Static members belong to the type rather than to one instance:

```camp
class Counters
{
	static int created;
}
```

Inline constants are compile-time values in a type or module scope:

```camp
class ExitCode
{
	inline int OK = 0;
	inline int USAGE = 2;
}
```

Use `fixed` for inline fixed-size storage:

```camp
struct Digest
{
	fixed byte[32] bytes;
}
```

Fields that retain pointer-bearing values must satisfy lifetime rules. A class
field in escaped storage cannot safely receive a scoped pointer just because
the assignment is convenient. The type may store the pointer-shaped value, but
the signature and lifetime facts must prove the pointed-to storage outlives the
field.

## Methods And Receivers

Methods put behavior next to the struct or class state they operate on. In a
class, they are where object identity usually becomes useful: methods can
observe fields, mutate fields, enforce invariants, participate in virtual
dispatch, and expose a stable API while the fields stay private to the
implementation.

```camp
class Counter
{
	int value;

	void increment()
	{
		this.value += 1;
	}
}
```

Receiver details matter, but they belong to the callable surface of the method:
whether the receiver is const, escaped, virtual, interface-dispatched, or
captured into a method reference. The functions and callables chapter covers
that side. For object design, the important point is that methods are the
ordinary way to let callers use an object without exposing its fields.

## Constructors And Initialization

Constructors initialize an instance. They use the type name as the callable
name.

```camp
class Buffer
{
	byte[] data;

	Buffer(nuint length, within allocator)
	{
		this.data = new byte[length];
	}
}
```

A constructor establishes the invariants that the type's methods and destructor
rely on. It can take ordinary parameters, `within` allocation context
parameters, generic capabilities, and `thrown` error slots.

`init` constructs a value for the current context. For an ordinary local
variable, that means block-lifetime storage, often stack storage in the
generated program:

```camp
Position position = init Position();
```

`new` allocates storage and then constructs the object there. In everyday code,
that usually means heap allocation through the active allocation context:

```camp
Counter* counter = new Counter();
```

Default construction is available where the language permits it. A type that
owns resources should usually make construction explicit enough that callers
can see allocation, failure, and lifetime decisions.

## Object Initializers After Construction

After a constructor call, a trailing initializer can assign fields or
property-like surfaces on the newly constructed value:

```camp
class RequestOptions
{
	nuint timeoutMs;
	bool followRedirects;

	RequestOptions()
	{
		this.timeoutMs = 30000;
		this.followRedirects = true;
	}
}

RequestOptions* options = new RequestOptions()
{
	.timeoutMs = 10000,
	.followRedirects = false,
};
```

Read this as one initialization operation: construct the object, then apply the
listed assignments before the initializer expression is complete. It is useful
when a constructor establishes the main invariant and a few public settings are
natural to override at the call site.

It is not a replacement for a real constructor invariant. Required state should
still be visible in the constructor signature. The trailing initializer is best
for optional configuration, field-style setup, and clear "set these members
right after construction" code.

Lifetime checking treats values retained by the trailing initializer the same
way it treats values retained by the constructor body. If a trailing assignment
stores a pointer or array view into the new object, that storage must live long
enough for the initialized value.

Fixed-size array fields can also be initialized or overwritten this way with a
target-typed initializer, a compatible string literal, or `default`:

```camp
struct PacketHeader
{
	fixed byte[4] magic;
	fixed char[16] tag;
}

PacketHeader header = init PacketHeader()
{
	.magic = [0x43, 0x41, 0x4D, 0x50],
	.tag = "request",
};
```

## Destructors And Cleanup

Destructors release resources owned by an instance. They use `~TypeName`.

```camp
class Buffer
{
	byte[] data;

	~Buffer()
	{
		delete this.data;
	}
}
```

A destructor should release only resources the instance owns. If a field is a
borrowed pointer or borrowed array view, the destructor should not free it just
because it has access to the address.

`delete` runs destruction and, for pointer-owned storage, frees the allocation
through the matching allocation context:

```camp
Buffer* buffer = new Buffer(4096);
delete buffer;
```

`finally delete` is useful when a local binding should be cleaned up on every
exit from its scope-like use:

```camp
Buffer* scratch = new Buffer(4096) finally delete;
```

Destructors can also take a `within` allocator parameter when cleanup needs the
same allocation context that construction used:

```camp
struct Buffer
{
	byte* data;

	~Buffer(within allocator)
	{
		delete this.data;
	}
}
```

The allocation chapter goes deeper on `within`; here the key is that lifecycle
functions can make allocation context part of the type's contract.

## `new`, `init`, And `delete` In Context

The three everyday lifecycle forms are:

| Form | Meaning |
|---|---|
| `init T(...)` | Construct a value in storage the current context already owns; local `init` values usually have block lifetime. |
| `new T(...)` | Allocate storage, usually from the active heap or allocator, then construct into it. |
| `delete value` | Run cleanup; also free storage for owning pointer forms. |

Use `init` for local or block-lifetime values. Think "construct this value for
the current block":

```camp
Position position = init Position();
```

Use `new` when the instance itself should live on the heap or another active
allocator, normally until an explicit `delete`:

```camp
Widget* widget = new Widget();
```

Use `delete` when the value owns cleanup:

```camp
delete widget;
```

These forms compose with `within`:

```camp
within (arena)
{
	Widget* widget = new Widget();
	delete widget;
}
```

The active allocation context controls allocation and matching deallocation.
When a type captures an allocator for later cleanup, that allocator must itself
be safe to retain.

## Virtual, Abstract, Override, And Sealed

Virtual dispatch follows the class hierarchy. Interface dispatch follows an
interface vtable contract. They are related, but not the same mechanism.

```camp
virtual class Component
{
	virtual int value()
	{
		return 1;
	}
}

sealed class Button: Component
{
	override int value()
	{
		return 2;
	}
}
```

Use:

- `virtual` when derived classes may customize behavior;
- `abstract` when a base class requires derived classes to provide behavior;
- `override` when implementing a virtual base member;
- `sealed` when inheritance or overriding should stop.

## Extern Classes And Native Objects

`extern class` describes a native or separately implemented object boundary.
Camp can name it, pass pointers to it, and declare methods or helpers around
it, but Camp does not invent ownership of the native layout or lifecycle.

```camp
extern class NativeWindow;

@symbol("window_close")
extern void closeWindow(NativeWindow* window);
```

If a native object needs construction or destruction, expose that through
extern functions or a Camp wrapper that makes ownership explicit.

```camp
class Window
{
	NativeWindow* handle;

	~Window()
	{
		closeWindow(this.handle);
	}
}
```

This keeps native details at the boundary while letting ordinary Camp code use
a source-level object.

## Class-Relative `classtype`

Inside a class, `classtype` names the class-relative type. In an open class, it
means the enclosing class or a derived type. In a sealed class, it means the
enclosing class exactly.

Use `classtype` when an API talks about "a value from this same class family"
rather than the receiver object itself. A common example is a non-virtual
method that should accept another value whose class tracks the static receiver
type:

```camp
class MenuItem
{
	int value;

	void copyPresentationFrom(classtype* other)
	{
		this.value = other.getValue();
	}

	void setValue(int value)
	{
		this.value = value;
	}

	int getValue()
	{
		return this.value;
	}
}

class MenuButton: MenuItem
{
}

MenuButton* source = new MenuButton();
MenuButton* target = new MenuButton();

source.setValue(4);
target.copyPresentationFrom(source);
```

The call through `target` binds `classtype*` to `MenuButton*`, so
`copyPresentationFrom` accepts another `MenuButton*`. A call through a
`MenuItem*` would bind the same source method to `MenuItem*` instead.

Use a `this` return when a method returns the receiver itself. Use `classtype`
when a signature refers to another object that belongs to the same static class
family.

A registry-backed static factory can also use `classtype`, but only when the
implementation really can create the requested class:

```camp
class Widget
{
	static classtype* create(string typeName = typenameof(classtype))
	{
		Widget* value = WidgetRegistry.create(typeName);
		return (classtype*)value;
	}
}
```

`typenameof(classtype)` is allowed as a default parameter value. It gives the
method an ordinary string argument for the class named at the call site. It is
not a general runtime reflection operation, and the method body does not
magically know the derived call target unless that information is passed in.

Do not use `classtype` for stored relationships:

```camp
class Control
{
	Control* parent;      // stored relationship
	classtype* active;    // ERROR: class-relative type is not storage shape
}
```

Use the declaring class type for fields and document stronger runtime
invariants separately.

## Layout, Opacity, And Exported APIs

Structs and classes make different ABI promises.

For a struct, fields are the source layout and the public layout. Exporting a
struct makes every field part of the API and ABI contract:

```camp
export struct Color
{
	byte r;
	byte g;
	byte b;
	byte a;
}
```

For a class, fields are never the public ABI surface. Consumers can hold and
pass `Window*` without seeing the fields that implement `Window`, even when
`Window` itself is exported.

```camp
export class Window;

export Window* createWindow(const char[] title, within allocator);
export void destroyWindow(Window* window);
```

Use the opacity deliberately. If callers need to read and write fields, a
struct is the ABI shape Camp gives them. If callers need identity, lifecycle,
or a stable handle over changing implementation details, an opaque class
pointer is usually the cleaner boundary.

## Common Lifecycle Patterns

Use a plain struct for visible layout:

```camp
struct Rect
{
	int x;
	int y;
	int width;
	int height;
}
```

Use a class pointer for owned identity:

```camp
Widget* widget = new Widget();
delete widget;
```

Use `finally delete` for local owned values that should clean up on every exit:

```camp
Widget* widget = new Widget() finally delete;
```

Use an exported create/destroy pair for C-friendly library boundaries:

```camp
export Image* openImage(const char[] path, within allocator, thrown ImageError error);
export void closeImage(Image* image);
```

Use borrowed views only when the lifetime is clear from the surrounding API:

```camp
struct ByteView
{
	const byte[] data;
}
```

The borrowed view does not own its elements. If a type owns storage, make that
ownership visible in its constructor, destructor, allocation context, or public
API.
