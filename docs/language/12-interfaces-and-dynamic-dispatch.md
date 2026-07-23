+++
nav_title = "12. Interfaces"
+++

# Interfaces And Dynamic Dispatch

Interfaces are Camp's named dynamic contracts. They are useful when a caller
should be able to work with "something that can write bytes" or "something that
can report a value" without knowing the concrete type.

Camp does not make that work through reflection, runtime type information, or
structural duck typing. An interface is a vtable-shaped contract, and a type
implements it explicitly. That keeps interface calls visible enough to cross a
native ABI while still giving ordinary Camp code a pleasant call surface.

This chapter starts with everyday interface use, then opens the box just far
enough to explain what an interface pointer points at, how class and struct
implementations differ, and why that difference matters for lifetimes.

## A First Interface

An interface declares the operations a caller can use:

```camp
interface TextSink
{
	void write(const char[] text);
}
```

A class or struct implements the interface by naming it in the type declaration
and marking the method that fills the slot:

```camp
class LogSink: TextSink
{
	void write(const char[] text): TextSink
	{
		writeLogLine(text);
	}
}
```

The marker matters. A same-name method is not enough by itself. `: TextSink`
says this method implements the `TextSink.write` slot.

When the method name differs from the interface slot, use the explicit slot
form:

```camp
class AuditSink: TextSink
{
	void appendAuditEntry(const char[] text): TextSink.write
	{
		writeAuditEntry(text);
	}
}
```

That is useful when the concrete type wants a domain-specific method name but
still satisfies a broader interface contract.

## `Interface` And `Interface*`

Camp makes a distinction that is easy to miss at first:

| Form | Meaning |
|---|---|
| `TextSink` | The vtable-level interface form. |
| `TextSink*` | An interface pointer used for ordinary dynamic calls. |

Most code uses `TextSink*`.

```camp
void writeHeader(TextSink* sink)
{
	sink.write("header");
}
```

An interface pointer is not simply a pointer to the concrete object. It points
to an interface-instance slot. That slot tells Camp which vtable to use, and
the vtable entry knows how to get from the interface context to the concrete
receiver.

You can think of bare `TextSink` as the vtable-level thing: it describes the
table of callable slots. `TextSink*` is one step further out. It is the pointer
you pass around when you want to call those slots on a particular object or
adapter.

The vtable-level form is mostly visible in generic capability syntax such as
`vtableof(T: TextSink)` or in low-level code that intentionally works with
slot functions. If you are just calling interface methods, use `Interface*`.

## How An Interface Call Works

You can read this source-level call directly:

```camp
sink.write("ready");
```

Conceptually, Camp does three things:

1. It reads the vtable from the interface pointer.
2. It finds the `write` function pointer in that vtable.
3. It calls that function pointer, passing the interface pointer as the call
   context along with the ordinary arguments.

A simplified C-shaped sketch looks like this:

```c
typedef struct TextSinkVTable TextSinkVTable;
typedef struct TextSinkSlot TextSinkSlot;

struct TextSinkVTable {
	void (*write)(TextSinkSlot *context, const char *text, size_t text_length);
};

struct TextSinkSlot {
	const TextSinkVTable *vtable;
};
```

That is not a promise about exact emitted names. It is the useful model:
interface dispatch is a vtable load followed by an indirect call. The context
comes from the `Interface*` value, not from a hidden runtime object.

Readers coming from C# or Java can think of the vtable as the method table that
tells the call which implementation to use. Readers coming from C can think of
it as a struct of function pointers with a context pointer passed to each
function. Camp's source syntax lets both views meet at `sink.write(...)`.

## Class Implementations

Classes are the natural fit when an interface pointer needs to be stored,
returned, or used for as long as the object lives.

```camp
class CountingSink: TextSink
{
	int count;

	void write(const char[] text): TextSink
	{
		this.count += 1;
		writeLogLine(text);
	}
}
```

For each directly implemented interface, a class instance has hidden dispatch
storage. Conceptually, the object contains a vtable-pointer slot for that
interface. Converting `CountingSink*` to `TextSink*` returns a pointer to that
slot inside the object.

Camp also generates an interface accessor for the class. For `CountingSink:
TextSink`, that accessor is named `getTextSink()`:

```camp
CountingSink* sink = new CountingSink();
TextSink* view = sink.getTextSink();
```

You usually do not need to call the accessor yourself, because ordinary
conversion works at assignment and call sites. The accessor is useful when you
want to name the projection explicitly, or when you are looking at generated
API surface and wondering where the interface pointer comes from.

That means the interface pointer's lifetime is tied to the object:

```camp
CountingSink* sink = new CountingSink();
TextSink* view = sink;

view.write("created");

delete sink;
```

`view` is valid while the object is valid. After `sink` is deleted, the
interface pointer is not safe to use, because it points into the deleted
object.

If the hidden interface slot is not at the beginning of the object, the vtable
entry can be a small adapter that recovers the containing object before calling
the concrete method. You normally do not think about that adapter, but it
explains why an interface call can start from an interface slot rather than a
plain object pointer.

## Struct Implementations

A struct can implement an interface too:

```camp
struct BufferSink: TextSink
{
	nuint written;

	void write(const char[] text): TextSink
	{
		this.written += text.length;
	}
}
```

The lifetime story is different. Structs do not gain hidden interface fields.
When a struct value or struct pointer is converted to an interface pointer,
Camp creates a scoped adapter. That adapter contains:

- a vtable pointer for the struct/interface pair;
- a pointer back to the original struct storage.

Then the interface call uses the adapter as its context, and the vtable slot
uses the stored struct pointer to call the concrete method.

```camp
void writeGreeting(TextSink* sink)
{
	sink.write("hello");
}

BufferSink sink = default;
writeGreeting(sink);
```

The adapter does not copy the struct value and does not box it on the heap.
It is scoped storage. That is the key rule.

A struct-backed interface pointer is safe only while the adapter and the
original struct storage are both alive. It is fine for a call or a fresh local
whose scope contains the use. It is not a good value to store in an escaped
field, return from a function, or keep after the struct storage goes away.

Use a class when you want an interface pointer that can naturally live with an
object. Use a struct when the value is ordinary storage and the interface view
is a short-lived way to call through a contract.

## Required, Optional, And Defaulted Slots

An interface method without an initializer is required. Every implementing
type must provide a compatible marked method.

```camp
interface CounterView
{
	int getValue();
}
```

An interface method initialized with `null` or `default` is optional:

```camp
interface DebugSink
{
	void write(const char[] text);
	void writeDebugName(const char[] name) = null;
}
```

The slot still exists in the vtable, but an implementing type may leave it
empty. Camp does not insert a null check before calling it. Check the slot when
absence is possible:

```camp
void describe(DebugSink* sink)
{
	sink.write("debug");

	if (sink.writeDebugName != null)
		sink.writeDebugName("parser");
}
```

A method initialized with a function target is defaulted:

```camp
interface LineSink
{
	void write(const char[] text);
	void writeLine(const char[] text) = defaultWriteLine;
}

void defaultWriteLine(LineSink* this, const char[] text)
{
	this.write(text);
	this.write("\n");
}
```

An implementing type can override the default by providing a marked method. If
it omits the method, the vtable uses the default target when the method is
called through the interface view:

```camp
LineSink* sink = openLineSink();
sink.writeLine("ready");
```

There is no hidden C#-style body attached to the concrete type. The default is
an ordinary function pointer in the interface vtable slot.

## Interface Inheritance

Interfaces may inherit from other interfaces:

```camp
interface Reader
{
	nuint read(byte[] buffer);
}

interface SeekableReader: Reader
{
	void seek(nuint position);
	nuint getPosition();
}
```

A `SeekableReader*` can be used where a `Reader*` is expected. The conversion
may be a simple view or may require an adjusted interface pointer, depending
on the inherited interface layout.

You do not need to know the exact adjustment rules to use the feature well.
The practical rule is this: interface inheritance is nominal, and inherited
members are part of the derived contract. If two inherited paths make a member
ambiguous, write the interface you mean rather than relying on guesswork.

## Constness And Lifetimes

Interface method signatures carry the same kind of contract as ordinary method
signatures. Parameter types, return types, `out`, `thrown`, constness, and
lifetime annotations all matter.

```camp
interface CellView
{
	const int* value(const this);
}
```

An implementation must satisfy that contract:

```camp
struct IntCell: CellView
{
	int storage;

	const int* value(const this): CellView
	{
		return &this.storage;
	}
}
```

The interface says `value` can be called on a const receiver and that callers
receive a read-only pointer. The implementation keeps both promises.

This implementation would be rejected:

```camp
struct MutableOnlyCell: CellView
{
	int storage;

	int* value(): CellView
	{
		return &this.storage;
	}
}
```

The receiver is mutable-only, so it cannot implement a slot that requires
`const this`.

The same rule applies to the whole signature. If the interface declares a
`thrown` slot, the implementation must keep it:

```camp
enum ParseError
{
	OK = 0,
	EMPTY
}

interface Parser
{
	int parse(const char[] text, thrown ParseError error);
}

struct SimpleParser: Parser
{
	int parse(const char[] text): Parser
	{
		return (int)text.length;
	}
}
```

`SimpleParser.parse` is missing the `thrown ParseError` slot, so it does not
implement the interface method. An implementation cannot quietly drop a failure
path, change an `out` shape, or retain a scoped value the interface promised
not to retain.

The same principle applies to parameter lifetimes, `out`, `thrown`, and other
signature details. The interface slot is the public promise; the implementing
method must be at least that precise.

Interfaces can also be declared `escaped`:

```camp
escaped interface BackgroundTask
{
	void run();
}
```

An escaped interface is designed for interface pointers that may be stored or
called later. A class-backed implementation is the usual fit, but the concrete
class must be able to supply the escaped receiver contract too:

```camp
escaped class TimerTask: BackgroundTask
{
	void run(): BackgroundTask
	{
		pollTimer();
	}
}

void schedule(escaped BackgroundTask* task);

void startTimer()
{
	TimerTask* task = new TimerTask();
	schedule(task);
}
```

Because `TimerTask` is an `escaped class`, its instance methods default to an
escaped receiver. That matches the escaped receiver expected by
`BackgroundTask.run`.

A struct implementation may satisfy the interface for scoped use, but its
automatic interface adapter is scoped storage:

```camp
struct StackTask: BackgroundTask
{
	void run(): BackgroundTask
	{
	}
}

void badSchedule()
{
	StackTask task = default;
	schedule(task); // ERROR: scoped adapter cannot satisfy escaped interface pointer
}
```

Use a class-backed implementation or another explicitly owned adapter when the
interface pointer must escape.

## Interface Conversions

Interface conversion is where the vtable model becomes a day-to-day rule.
Camp can convert a value to an interface pointer only when it knows how to
produce a real interface-instance slot.

For a class implementation, conversion can happen directly at assignment:

```camp
CountingSink* sink = new CountingSink();
TextSink* view = sink;
view.write("ready");
```

It can also happen at a call site:

```camp
void writeOnce(TextSink* sink)
{
	sink.write("once");
}

CountingSink* sink = new CountingSink();
writeOnce(sink);
```

Both forms use the class object's hidden interface slot. Calling the generated
accessor makes the same projection explicit:

```camp
TextSink* view = sink.getTextSink();
```

For a struct implementation, conversion uses a scoped adapter:

```camp
BufferSink buffer = default;

writeOnce(buffer);

TextSink* localView = buffer;
localView.write("again");
```

That local interface pointer is useful inside the same scope, but it should
not escape:

```camp
TextSink* badView()
{
	BufferSink buffer = default;
	return buffer; // ERROR: the adapter would outlive `buffer`
}
```

When interfaces inherit from other interfaces, derived interface pointers can
convert to base interface pointers:

```camp
interface FlushableSink: TextSink
{
	void flush();
}

void writeAndFlush(FlushableSink* sink)
{
	TextSink* text = sink;
	text.write("ready");
	sink.flush();
}
```

The conversion may be a direct view or an adjusted interface pointer depending
on the inherited contract layout. Source code should not depend on which one
the compiler chooses.

Avoid raw casts that fabricate an interface pointer. An interface pointer has
to point at a valid interface-instance slot. A plain object pointer or `void*`
does not magically become that slot just because the address is non-null.

When an API expects `TextSink*`, pass a real implementation. When an API needs
to store the pointer, make sure the implementation and lifetime are appropriate
for storage.

## `vtableof` At A Glance

Generic code is covered later, but interfaces introduce one generic-looking
piece of syntax that is worth recognizing now:

```camp
void writeTwice<T: implements TextSink>(
	T* sink,
	vtableof(T: TextSink))
{
	sink.write("first");
	sink.write("second");
}
```

`T: implements TextSink` says the concrete type must explicitly implement the
interface. `vtableof(T: TextSink)` supplies the vtable capability needed for
erased generic dispatch.

You do not need `vtableof` for ordinary non-generic interface pointers:

```camp
void writeOnce(TextSink* sink)
{
	sink.write("once");
}
```

For now, treat `vtableof` as the explicit capability generic code asks for
when it wants to call interface methods on a type parameter. The generics
chapter will return to this model.

## Designing Interface APIs

Use an interface when callers need dynamic behavior through a named contract.
Use a class when identity and long-lived interface pointers are central. Use a
struct when the implementation is value-shaped and the interface view is
short-lived.

Keep these habits in mind:

- mark implementation methods explicitly;
- keep optional slots rare and check them before calling;
- use defaulted slots for simple shared behavior, not as hidden interface
  bodies;
- remember that `Interface*` points at an interface-instance slot, not
  necessarily at the concrete object;
- prefer class-backed implementations when an interface pointer will be stored
  or escape;
- prefer struct-backed implementations when callers only need a scoped dynamic
  view over existing value storage.

Interfaces are one of Camp's clearest examples of its larger design: write the
high-level contract in source, but keep the ABI shape simple enough that a C
caller can understand what is really being passed around.
