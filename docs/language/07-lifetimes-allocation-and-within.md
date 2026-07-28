+++
nav_title = "7. Lifetimes And Allocation"
+++

# Lifetimes, Allocation, And `within`

You have already seen `new`, `delete`, delegates, arrays, methods, and
`within` parameters. Those features all have one thing in common: they can move
pointer-shaped values around. A pointer may be copied, stored, returned, passed
through a callback, or kept in an object.

Camp's lifetime annotations give those movements names. They do not replace
ownership design, and they do not make memory automatic. They let a signature
say which values are only borrowed, which values may be retained, and which
returned views are tied to an input that the caller already owns.

The vocabulary is small: `scoped`, `escaped`, `unscoped(anchor)`, and
`within`. The trick is learning to read them as part of the API's story instead
of as decoration.

Before the examples start, it helps to know the defaults. Pointer-bearing input
parameters default to `scoped`: a function may use them during the call, but
may not retain them unless the signature says so. Pointer-bearing outputs
default to `unscoped(this)` for methods with a receiver, and to `unscoped` for
functions without one. Write the annotation when you need a different or more
specific relationship, such as a return value tied to a particular parameter.

## The Shape Of The Problem

Start with a simple borrowed view:

```camp
scoped(text) const char[] prefix(const char[] text, nuint count)
{
	return text[..count];
}
```

The function does not allocate text. It returns a view into the caller's
existing text. The result should not be stored somewhere that can outlive
`text`, because the result is only meaningful while the original storage is
still meaningful.

That is what `scoped(text)` says: the returned view is tied to the `text`
parameter. The caller can use it immediately, pass it to another borrowed-view
API, or copy its contents somewhere else. The caller should not treat it as an
owned buffer.

Without an annotation, readers are left guessing whether a function borrowed,
allocated, cached, or returned a global. Camp asks important relationships to
be visible in the signature.

## The Lifetime Vocabulary

The three most common lifetime words describe where a pointer-bearing value may
go:

| Form | Use It When |
|---|---|
| `scoped` | The value is borrowed for the current call or local scope. |
| `scoped(anchor)` | The value is borrowed from a named anchor, such as a parameter or `this`. |
| `escaped` | The value may be retained beyond the current call or scope. |
| `unscoped(anchor)` | The value is allowed to escape the default local relation because it is known to outlive a named anchor. |

A value's lifetime is not the same thing as its ownership. An `escaped byte*`
may still be borrowed from a long-lived owner. A `scoped byte*` may point to
valid memory. The annotations say what the callee is allowed to do with the
value, not who must eventually free it.

```camp
void inspect(scoped const byte[] data);
void remember(escaped const byte[] data);
```

`inspect` can look at `data` during the call. `remember` may store `data` for
later, so callers must pass a value that is safe to retain.

## Anchors

An anchor is a visible name that another lifetime depends on. It is usually a
parameter or the receiver `this`.

```camp
const byte[] firstBytes(const byte[] source, nuint count)
{
	return source[..count];
}
```

The input parameter is already scoped by default. If the returned slice needs
to promise a tighter relationship to `source`, write that relationship on the
result, as in the `prefix` example above. If the default output lifetime is
enough for the API, leave it implicit.

Anchors are part of the source contract. If a signature says
`scoped(source)`, the parameter name `source` matters. Rename it with the same
care you would use when renaming a public parameter that documentation or named
arguments depend on.

Receivers can be anchors too:

```camp
class ByteStore
{
	const byte[] data;

	const byte[] view(const this)
	{
		return this.data;
	}
}
```

The returned view uses the method output default, `unscoped(this)`. That
matches the way a reader would describe the method in plain language: "give me
a view of this store's data."

## `escaped`

`escaped` means the value may be retained somewhere that outlives the immediate
scope. That includes global storage, static fields, fields of long-lived
objects, heap-allocated delegate contexts, and any API whose contract says it
stores the value for later.

```camp
class Session
{
	escaped delegate void() onClose;

	void setOnClose(escaped delegate void() callback)
	{
		this.onClose = callback;
	}
}
```

The callback may run after `setOnClose` returns, so the callback and anything it
captures must be safe to retain. Passing an escaped callback does not say who
will delete its context; it says retaining the callable value is allowed.

String literals and function symbols are common escaped values. A pointer into
a local fixed buffer is not:

```camp
const char[] literal = "ready";      // escaped storage

fixed char[16] scratch = "ready";
const char[] temporary = scratch[..]; // scoped to this block
```

That distinction is why copying a pointer into a local variable does not make
it safe to store forever. The value still points where it pointed before.

## `scoped`

`scoped` is the borrowed side of the story. A scoped value can be used during
the current call or while its anchor remains valid, but it must not be retained
in escaped storage.

```camp
void parseLine(const char[] line)
{
	// Use line here, but do not store it for later.
}
```

Scoped values are useful because many APIs only need a temporary view. A parser
can scan a line, a formatter can write into a caller's buffer, and a callback
can run immediately without requiring heap-safe captures.

```camp
int runNow(delegate int() callback)
{
	return callback();
}
```

The callback is scoped by default, so it is allowed to borrow local state.
If the same API accepted `escaped delegate int()`, a lambda that captures local
stack state would need to prove those captures can escape.

## `unscoped(anchor)`

`unscoped(anchor)` is easy to misread. It does not mean "global" or "owned."
It means the value is not limited to the immediate call or local expression
because it is known to be valid with respect to a named anchor.

One common use is constructing an aggregate that keeps a borrowed view:

```camp
struct View
{
	const char[] text;

	View(unscoped const char[] text)
	{
		this.text = text;
	}
}
```

The constructor stores `text`, so it cannot accept a value that is only scoped
to the constructor call. It needs a view that can live at least as long as the
constructed `View` will be used.

Another use is a narrow proof boundary. Suppose native code gives you a pointer
and a length, and an escaped owner is responsible for the storage:

```camp
unscoped(owner) char[] trustedView(escaped void* owner, char* data, nuint length)
{
	char[] view = { data, length };
	return (unscoped(owner))view;
}
```

The cast is a promise: `view` is valid because `owner` keeps the storage alive.
The return type tells callers about that relationship; the cast in the body is
the local proof that the constructed view satisfies it. That proof should be
rare and local. If many callers need to write the same cast, the API probably
needs a clearer lifetime shape.

## `init`, `new`, And `delete`

Camp has two everyday ways to construct an object. `init` constructs a value in
storage that already exists. `new` allocates storage first, then constructs the
value there.

Use `init` when the value belongs to the current block:

```camp
Buffer scratch = init Buffer(4096) finally delete;
useBuffer(&scratch);
```

The local variable `scratch` has scoped storage. `init Buffer(4096)` constructs
the `Buffer` in that storage, and `finally delete` makes cleanup run on every
exit from the scope-like use. The `delete` here is about destruction and owned
resources inside the value; it does not free the local variable's stack-like
storage.

Use `new` when the object itself needs allocated storage and the caller will
hold an owning pointer:

```camp
Buffer* buffer = new Buffer(4096);
takeBuffer(buffer);
```

Here `new Buffer(4096)` allocates storage through the active allocation
context, constructs a `Buffer`, and returns a pointer. In this example,
`takeBuffer` takes ownership of `buffer`, so the caller does not write
`finally delete` at the declaration site. The cleanup responsibility has moved
to the receiving function. Whoever owns the pointer eventually calls `delete`,
which runs the destructor and frees the storage through the matching allocation
context.

The distinction is lifetime-shaped as well as storage-shaped. A local `init`
value is normally scoped to the current block. A `new` object is allocated
storage that can be retained when the API's lifetime contract allows it. The
language still expects explicit ownership design: copying a pointer does not
copy the object, and `delete` must be applied exactly where the ownership path
says cleanup belongs.

## Allocation Contexts With `within`

Some APIs allocate as part of their work. When that allocation choice belongs
to the caller, put `within allocator` in the signature:

```camp
escaped string copyDisplayName(const char[] name, within allocator)
{
	return name.copyString(within allocator);
}
```

The `within allocator` parameter is part of the callable contract. A caller can
choose the allocation context for the returned string, and the nested
`copyString` call uses the same context. The function does not need to name an
allocator type in its public shape just to say "caller chooses where this
allocation comes from."

A caller can also choose an allocation context for a block:

```camp
within (arena)
{
	Buffer* buffer = new Buffer(4096);
	delete buffer;
}
```

Inside the block, `new` uses `arena`, and `delete` uses the same active context
unless a more specific expression says otherwise. That matching is the point:
storage allocated through one allocator-shaped context should be cleaned up
through the same context unless the allocator's own contract says another path
is valid.

For a single operation, use a `within` expression:

```camp
Buffer* buffer = within (arena) new Buffer(4096);
within (arena) delete buffer;
```

Use the parameter form when allocation belongs to the function's public shape.
Use the statement or expression form when a caller is choosing where a
particular piece of work should allocate or clean up.

Constructors and destructors can also take `within allocator` when an instance
owns storage allocated through the active context:

```camp
class Buffer
{
	byte[] data;

	Buffer(nuint capacity, within allocator)
	{
		this.data = new byte[capacity];
	}

	~Buffer(within allocator)
	{
		delete this.data;
	}
}
```

The constructor and destructor both accept the allocation context. A caller
does not need to pass it to every nested `new`; the active context flows through
source allocation inside the construction or cleanup path.

The implicit type of `within allocator` is the standard library's `Allocator*`
interface. You can write an explicit allocator type when an API really needs
one, but most APIs should not. Custom allocators normally participate by
implementing `Allocator`, so callers can supply them through the ordinary
`within allocator` contract.

This flow also affects generated storage for some source constructs. A
capturing `new delegate` may allocate hidden context. If an API accepts a
`within` allocator and creates one, the allocator choice can matter even when
the source code does not spell a raw allocation call.

Declaring `within allocator` parameters is typical for libraries, and it is
required for shared and static libraries whose callers must control allocation
across the boundary. Executable programs have more freedom. If an executable
does not care which allocator is used, or it simply wants the default heap
allocator everywhere, it does not need to thread `within allocator` through its
own functions.

## Captures And Callback Lifetimes

You already saw that delegates carry context. Lifetimes decide whether that
context may retain what it captures.

```camp
void schedule(escaped delegate void() callback);

void start()
{
	int attempts = 0;
	schedule(new delegate () =>
	{
		attempts += 1; // ERROR if attempts cannot escape
	});
}
```

The problem is not the lambda syntax. The problem is the promise made by
`schedule`: it may keep the callback after `start` returns. Capturing a local
variable by reference would leave the callback pointing into a dead stack
frame.

For an immediate callback, a scoped callable shape is enough:

```camp
void runNow(delegate void() callback)
{
	callback();
}

void update()
{
	int attempts = 0;
	runNow(() => attempts += 1);
}
```

The callable chapter introduced delegate shape; this chapter adds the lifetime
piece. For ordinary Camp code, the rule is simple: if a callback can run later,
its captured state must live later too.

## Aggregates And Retained Fields

Structs, classes, optionals, arrays, and delegates can all carry
pointer-bearing state. The lifetime of the whole value has to account for what
it retains.

```camp
struct NameView
{
	const char[] text;
}
```

`NameView` does not own the characters. If you build one from a local fixed
buffer, the view is only valid while that buffer is valid:

```camp
NameView makeBadView()
{
	fixed char[16] scratch = "camp";
	NameView view = { .text = scratch[..] };
	return view; // ERROR: view retains scratch
}
```

The fix depends on the API:

- Return a view tied to a caller-provided anchor.
- Copy the text into storage that can escape.
- Keep the value local to the block where the borrowed storage lives.
- Change the type so ownership is explicit.

This is the same idea you saw with class fields in the object chapter. A field
that stores a pointer-bearing value is a promise about how long that value will
remain valid.

## Lifetime Casts

Lifetime casts are proof boundaries:

```camp
escaped const char[] retained = (escaped const char[])text;
unscoped(owner) const char[] anchored = (unscoped(owner))text;
```

They do not copy storage, allocate memory, or extend a stack frame. They tell
the compiler that the program has a proof it could not infer from the local
syntax.

Use casts when crossing a narrow boundary where you really do know more than
the compiler:

- a native handle owns the bytes behind a pointer;
- a registry returns storage that lives for the program;
- an object field is valid because another retained field owns the backing
  allocation;
- a target API has a lifetime rule the Camp type cannot express directly.

Do not use lifetime casts as a way to make an error disappear. A wrong lifetime
cast is exactly the kind of bug the annotations are trying to keep visible.

## Common Fixes

Most lifetime diagnostics are asking one of a few questions:

| Diagnostic Shape | Usual Fix |
|---|---|
| Returning a local view | Tie the result to an input anchor, or copy into longer-lived storage. |
| Storing a scoped value | Require an `escaped` or `unscoped` input, or keep the storage local. |
| Capturing a local in an escaped delegate | Make the callback scoped, allocate retained state, or avoid the capture. |
| Allocator mismatch | Thread the same `within` context through construction and cleanup. |
| Aggregate retains a short-lived value | Change the aggregate lifetime, copy the data, or redesign ownership. |

The compiler is not trying to make borrowed data hard to use. It is trying to
make the moment of retention visible. Once you can see that moment, the right
fix is usually a design decision: borrow for less time, retain for real, or
write the relationship in the signature.
