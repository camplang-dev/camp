# Interfaces And Dispatch

## Interface Declarations

An interface is a nominal ABI-visible contract. It declares a vtable shape that
implementing types agree to provide. Camp interfaces are not hidden runtime
objects, structural "has these methods" matches, or delegate-like pairs of
context and call target. A type implements an interface because it explicitly
declares that interface and explicitly binds implementation methods to the
interface slots.

```camp
export interface Reader
{
	nuint read(byte[] buffer);
	bool getEndOfFile();
}
```

An interface declaration may contain ordinary methods, generic methods,
constructors, destructors, inherited interfaces, defaulted methods, and optional
methods. Each entry contributes to the interface contract and, for callable
entries, to the vtable shape.

The source form has two important levels:

| Source form | Meaning |
|---|---|
| `Reader` | The interface vtable pointer form. |
| `Reader*` | The ordinary interface-instance pointer form used for calls. |

Most ordinary code uses `Reader*`. Bare `Reader` is useful when an API is
talking about the vtable itself, such as a generic `vtableof(T: Reader)`
capability or a low-level ABI boundary.

## Interface Values And ABI Model

The user-facing mental model is that an interface declaration defines a vtable
struct. An interface instance is a pointer to a slot that stores a pointer to
that vtable. In lowered terms, source `Reader*` behaves like a pointer to an
interface vtable-pointer slot.

Given:

```camp
export interface Reader
{
	nuint read(byte[] buffer);
	bool getEndOfFile();
}
```

The conceptual vtable shape is:

```camp
struct Reader
{
	fn nuint(Reader** context, byte[] buffer) read;
	fn bool(Reader** context) getEndOfFile;
}
```

The first lowered parameter of an interface slot is the interface-instance slot
pointer. That pointer is the context for the call. It points at storage whose
first value is the vtable pointer used for dispatch.

Ordinary source code does not spell this lowered `**` shape:

```camp
Reader* reader = openReader(path);
byte[] buffer = ...;
nuint copied = reader.read(buffer);
```

The compiler supplies the interface-instance slot as the first argument to the
vtable entry.

## Implementing Interfaces

A `struct` or `class` implements an interface by naming it in the type
declaration and by marking each implementation method for the interface slot it
fills.

```camp
export interface Calculator
{
	int add(int left, int right);
	int subtract(int left, int right);
}

export class OffsetCalculator: Calculator
{
	int offset;

	OffsetCalculator(int offset)
	{
		this.offset = offset;
	}

	int add(int left, int right): Calculator
	{
		return this.offset + left + right;
	}

	int subtract(int left, int right): Calculator
	{
		return this.offset + left - right;
	}
}
```

Structural similarity is not enough. A method with the same name and signature
does not fill an interface slot unless it is marked. The ordinary marker form
is `: InterfaceName`, which binds the method to the slot with the same callable
name. The selector form `: InterfaceName.slotName` lets a differently named
method fill a specific slot.

```camp
export interface TextSink
{
	void write(const char[] text);
}

export class AuditSink: TextSink
{
	void appendAuditLine(const char[] text): TextSink.write
	{
		...
	}
}
```

One method may implement only one interface slot. If two methods claim the same
slot, or if a marked method is not compatible with the slot, the compiler
reports an error at the implementation method.

## Required, Defaulted, And Optional Methods

An interface method without an initializer is required. Every implementing type
must provide a compatible marked method unless an inherited class implementation
already satisfies the interface.

An interface method may also specify a vtable initializer:

```camp
export interface Formatter
{
	void writeInt(int value);
	void writeDebugName(const char[] name) = null;
	void writeSpace() = default;
	void writeLine(const char[] text) = defaultWriteLine;
}

void defaultWriteLine(Formatter* this, const char[] text)
{
	this.writeSpace();
	...
}
```

`= null` and `= default` make the slot optional. The slot still exists in the
vtable, but a type may omit it and the generated vtable stores a null function
pointer for that slot. Camp does not insert a null check before calls to
optional slots. Code that calls an optional slot should check that the slot is
present when null is possible.

`= targetFunction` makes the slot defaulted. The target must be a compile-time
function reference to a free function, out-of-scope receiver function, or static
method. The default target must match the source-level slot type exactly. For a
slot `R method(P)`, the default target has callable shape `fn R(Interface*
this, P)`.

If an implementing type declares a marked method for a defaulted slot, that
method overrides the default. An unmarked same-name method is ordinary type
surface and does not fill the slot.

Constructors and destructors remain required when an interface declares them.
Default and optional initializers apply only to ordinary interface methods.

## Interface Inheritance

Interfaces may inherit from other interfaces.

```camp
export interface Readable
{
	nuint read(byte[] buffer);
}

export interface Seekable: Readable
{
	void seek(nuint position);
	nuint getPosition();
}
```

Interface inheritance is nominal and conceptually flattened into the derived
vtable. Base interface entries appear once, even in diamond-shaped inheritance.
The inherited vtable portion appears before derived entries when that base is
the leading base contract. Upcasting from a derived interface to a base
interface may therefore be a no-op for leading base contracts, while other base
conversions can require an adjusted interface conversion.

If inherited method names make a call ambiguous, the caller must disambiguate
with an explicit cast. Camp does not silently choose one inherited contract.

## Lifetime And Constness In Interface Contracts

Lifetime annotations and receiver qualifiers are part of an interface method's
contract. An implementation must satisfy the interface slot's parameter,
receiver, return, `out`, and `thrown` shape, including `const`, `constof`,
`escaped`, `scoped`, and `unscoped` relationships.

```camp
export interface BufferView
{
	constof(this) byte* data(const this);
}
```

The implementation cannot loosen that contract by returning a mutable pointer
from a const receiver, retaining a scoped value, changing a thrown slot, or
removing a lifetime anchor. The interface slot is the public promise; the
implementation is checked against that promise.

## Class Implementation Of Interfaces

Class implementations store interface dispatch information inside each class
instance. For every interface directly declared by a class, the compiler adds a
hidden field that stores a pointer to the vtable object for that class/interface
pair. Conceptually, for `TextFile: Reader`, the class contains a hidden field
like:

```camp
Reader* _vt_Reader;
```

The hidden field is initialized during typed construction before the user
constructor body runs. Interface conversion for a class value returns the
address of that hidden vtable-pointer field. No heap boxing is introduced.

```camp
TextFile* file = new TextFile(path);
Reader* reader = file;
```

That conversion is modeled as if the class had a generated accessor:

```camp
constof(this) Reader* getReader()
{
	...
}
```

The accessor returns a pointer to the stored interface-vtable slot inside the
object. Calls through the interface pointer use the stored vtable. When the
interface slot pointer is not at the start of the object, the stored vtable
entries can be fixup thunks that recover the containing class instance before
calling the concrete implementation.

Hidden interface fields are private implementation details. Exported class
opacity still applies: outside code that cannot see the class layout cannot
form an interface pointer by taking the address of the hidden field. A module
can expose an explicit projection helper when that conversion must cross a
public ABI boundary.

If a base class implements an interface, derived classes inherit that
implementation relationship. A derived class should customize behavior by
overriding virtual implementation methods. Re-implementing an already
implemented interface in a derived class is not allowed.

## Struct Implementation Of Interfaces

Structs do not gain hidden interface fields. A pointer to a struct value can
convert to an interface pointer by creating a scoped indirect interface adapter.
The adapter contains a vtable pointer and a pointer to the original struct
storage.

```camp
export interface ByteReader
{
	nuint read(byte[] buffer);
}

export struct SliceReader: ByteReader
{
	const byte* data;
	nuint length;
	nuint position;

	nuint read(byte[] buffer): ByteReader
	{
		...
	}
}
```

The struct conversion preserves pointer identity for the original struct. The
interface call goes through an adapter vtable whose entries use the stored
pointer-to-data as the implementation receiver. There is no copied struct value
and no hidden heap boxing.

Because the adapter is scoped storage, automatic struct-to-interface conversion
is allowed only where the adapter remains valid for the duration of the use.
Typical valid cases include passing a pointer to a struct value to a scoped
interface-pointer parameter or initializing a new local in the current
declaration scope. The adapter may not be assigned to fields, array elements,
already-initialized locals, caller-provided storage, or escaped interface
requirements.

This distinction is important API design guidance: classes are the natural
choice when interface identity must be stored or escape. Struct-to-interface
conversion is for scoped use of existing value storage.

## Interface Constructors And Destructors

Interfaces may declare constructor and destructor requirements as part of the
contract.

```camp
export interface CounterStore
{
	CounterStore(within allocator);
	~CounterStore(within allocator);
	void add(nint value);
	nint getTotal();
}
```

An interface constructor lowers to a vtable entry conceptually named `create`.
It has no instance context parameter because no instance exists yet. It returns
a pointer to the implementing instance or context. It is not called through
ordinary instance dot syntax on `CounterStore*`, and it must explicitly declare
`within allocator` as its last parameter.

An interface destructor lowers to a vtable entry conceptually named `destroy`.
It receives the interface-instance slot pointer and must explicitly declare
`within allocator` as its last parameter. The instance may be deallocated after
destruction completes.

Concrete implementations do not need to spell the allocator parameter unless
they use it directly; generated interface thunks forward it as needed.

An interface constructor does not make the interface directly instantiable:

```camp
// Invalid: an interface is a contract, not a concrete allocation target.
// CounterStore* store = new CounterStore();
```

An interface with a constructor can be implemented only by a sealed class or by
a struct, because construction through the interface must produce a concrete
known implementation shape.

## Interface Calls And Optional Slot Checks

Calling a method on `Interface*` automatically supplies the interface-instance
slot pointer to the vtable entry.

```camp
Reader* reader = openReader(path);
byte[] buffer = ...;
nuint copied = reader.read(buffer);
```

Calling a method on a bare interface vtable value is a lower-level operation:
the caller must supply the interface-instance slot explicitly. Ordinary code
should prefer `Interface*` unless it is intentionally working with vtables.

Optional slots are still slots. Camp does not insert automatic null checks. If
an API permits missing optional slots, code that calls the optional method must
guard the call according to the interface's documented contract.

```camp
Formatter* formatter = openFormatter();

if (formatter.writeDebugName != null)
{
	formatter.writeDebugName("parser");
}
```

Outside call syntax, member access to an interface method reads the current
vtable slot function pointer. If you store that pointer in a local, call it by
supplying the interface receiver explicitly:

```camp
fn void(Formatter* this, const char[] name) slot = formatter.writeDebugName;

if (slot != null)
{
	slot(formatter, "parser");
}
```

The direct call form still supplies the receiver automatically:

```camp
formatter.writeDebugName("parser");
```

That direct call is allowed even for an optional slot, but a missing slot has
the same failure mode as invoking any null function pointer. Camp does not
treat the slot as an implicit boolean; write `slot != null` or
`formatter.writeDebugName != null`.

## Interface Conversions

Class-to-interface conversion is natural when the class implements the
interface. Struct-to-interface conversion is scoped and adapter-backed, as
described above. Interface upcasts to base interfaces are allowed. Interface
downcasts are not implicit and are not a general safe runtime operation.

```camp
Seekable* seekable = openSeekable(path);
Readable* readable = seekable;
```

Conversions can be no-op or adjusted depending on inherited interface layout.
When several inherited contracts would make the target ambiguous, use an
explicit cast to state the intended interface.

## `vtableof`

`vtableof(T: Interface)` supplies the vtable capability for generic code that
uses interface dispatch on a type parameter.

```camp
export void render<T: implements Writer>(
	T* writer,
	const char[] text,
	vtableof(T: Writer))
{
	Writer* view = writer;
	view.write(text);
}
```

For exported implementation relationships, the compiler exposes a vtable object
for the concrete type/interface pair. That allows C callers or other low-level
consumers to pass the vtable capability needed by generic Camp functions.

## Metadata And API Surface

Interfaces are not opaque in the same way classes can be opaque. An exported
interface is part of the ABI surface: its vtable shape, slot names, callable
contracts, inherited contracts, and implementation relationships matter to
callers and metadata consumers.

Metadata should describe required, optional, defaulted, constructor,
destructor, generic, and inherited interface surface. Hidden class fields and
stored fixup vtables are implementation details unless they are exposed through
an explicit API surface.

## Cost Model

The useful cost model is:

| Case | Cost |
|---|---|
| Bare `Interface` | One pointer to a vtable. |
| `Interface*` | One pointer to an interface-instance slot. |
| Class implementation | One hidden stored vtable-pointer field per directly declared implemented interface. |
| Struct conversion | A scoped adapter containing a vtable pointer and a pointer to the original struct. |
| Interface call | One vtable load and one indirect call. |

There is no hidden runtime registry, reflection lookup, or required heap boxing
for ordinary class-backed interface calls.

## Mental Model

Use this model when designing APIs:

- An interface declaration defines a nominal vtable shape.
- Bare `Interface` is the vtable-level form.
- Source `Interface*` is the ordinary callable interface-instance form.
- Class implementations store hidden per-interface vtable-pointer fields.
- Struct implementations use scoped adapters and do not store hidden fields.
- Interface constructors and destructors are vtable entries for construction
  and destruction contracts, not ordinary instance calls.
- `vtableof(T: Interface)` is the explicit generic capability for interface
  dispatch.
