# Camp In One Page

Camp is a native language for code that wants to be honest about the machine
without making every useful abstraction feel like contraband. It is meant for
systems, libraries, runtimes, bindings, embedded targets, classic and retro
platforms, and other places where C toolchains and ABI boundaries are part of
the job.

If you already know your way around C-shaped code, Camp should feel familiar
quickly. The interesting parts appear where native code usually gets vague:

- Who owns this allocation?
- How long does this pointer or view remain valid?
- What does this callback carry with it?
- Where does an error go?
- What shape does this API have when C, another language, or another toolchain
  sees it?

This page is the short map. It will not teach the whole language, but it should
tell you whether Camp is the sort of language you came looking for, and it
should give you enough landmarks to read the chapters that follow.

## A Tiny Program

Here is a complete Camp program:

```camp
using Std;

export int main()
{
	Console.writeLine("Hello, Camp.");
	return 0;
}
```

There is not much ceremony here. `using Std;` brings the standard library
namespace into scope. `main` is an ordinary function that returns an `int`
status. Marking it `export` places it on the public boundary, and for ordinary
builds that is enough for the compiler to treat the project as an executable.
`Console.writeLine` is a real standard-library API for writing a line to
standard output.

The file has the shape you would expect: imports first, declarations after
that, statements inside braces. Camp does not ask you to learn a new visual
language before you can print a line.

The rest of the guide grows out from this small program. You will add
declarations, introduce types, decide where values live, pass pointers and
arrays across boundaries, handle errors, and eventually design APIs whose
source contracts are clear to the compiler, to callers, and to native tools.

## What Camp Is Trying To Be

Camp is designed around a plain idea: source code should say the important
parts of the native contract out loud.

That gives the language a few strong goals:

1. Make native API design readable in the source.
2. Map cleanly to the patterns C developers already use to build libraries.
3. Keep allocation, lifetime, error, and callback contracts visible.
4. Expose high-level features across ABI boundaries without hand-written glue.
5. Stay useful on targets that do not look like a modern desktop runtime.

Camp has classes, interfaces, generics, properties, iterators, lambdas, async
calls, overloads, named arguments, and documentation comments. Those features
exist because library code should be pleasant to read and hard to misuse.

But Camp is careful about which high-level features it chooses. If a feature
cannot be expressed across a native boundary, Camp is suspicious of it. The
language does not assume runtime exception handling, required runtime type
information, compile-time-only generics with no stable callable ABI, or a large
standard library full of complex runtime-owned types.

Instead, Camp tries to make common native patterns feel automatic. A small
image library might expose this:

```camp
export enum ImageError
{
	OK = 0,
	E_NOT_FOUND,
	E_UNSUPPORTED_FORMAT
}

export class Image;

export Image* openImage(const char[] path, within allocator, thrown ImageError error);
export void closeImage(Image* image);
```

This is the kind of API C libraries already tend to design by convention: an
opaque handle, an explicit open/close pair, an error channel, and a decision
about where allocation comes from. Camp gives that shape source-level meaning
instead of leaving it as folklore.

## Familiar Ground

Camp source is made of declarations. At the top level you will write functions,
types, aliases, constants, and exported API surfaces. Inside types you will
write fields, methods, properties, constructors, destructors, and static
members.

A small value type looks ordinary:

```camp
export struct Size
{
	int width;
	int height;
}

export int area(Size size)
{
	return size.width * size.height;
}
```

Control flow and expressions mostly stay where you expect them to be. Camp has
blocks, branches, loops, `switch`, `return`, calls, member access, indexing,
casts, construction, arithmetic, boolean logic, and cleanup. The reference
chapters cover the full surface later; the important first impression is that
ordinary code remains ordinary.

Camp does make nominal boundaries count. A `struct`, a `class`, an
`interface`, an `enum`, and a `newtype` are different promises, not just
different storage spellings. When you cross one of those boundaries, the source
should show the choice with a constructor, conversion, adapter, cast, or
interface dispatch.

## What Camp Makes Explicit

The first unusual thing you will notice is that Camp APIs often carry more of
their contract in the type or signature.

Allocation is one example:

```camp
export Buffer* createBuffer(nuint capacity, within allocator);

Buffer* makeTemporaryBuffer(Allocator arena)
{
	return within (arena) new Buffer(4096);
}
```

`within` is not decorative. It selects the allocation context used by the call
or expression. The surrounding names are ordinary library or application
types; the important part is that allocation policy travels through the source
instead of disappearing into a global heap.

Constness can be dependent too:

```camp
export constof(bytes) byte* firstByte(const byte[] bytes)
{
	return bytes.elements;
}
```

`constof(bytes)` means the returned pointer follows the constness of the
argument. Pass a mutable array and the pointer is mutable. Pass a const view
and the pointer is const. That is a source-level promise callers can rely on.

Lifetimes describe a different kind of relationship: whether a pointer-like
value may be stored, returned, captured, or retained after a call.

```camp
class TextCache
{
	const char[] text;

	void remember(unscoped const char[] value)
	{
		this.text = value;
	}
}

export void setCompletion(escaped once void() callback);
```

Here `remember` stores a view, so the argument cannot be a short-lived local
view that disappears after the call. `setCompletion` accepts a callback that
may be retained and invoked later. Camp gives source vocabulary to these
relationships instead of leaving them in comments, naming conventions, or
tribal knowledge.

Errors are explicit too:

```camp
int parseDigits(const char[] text);

enum ParseError
{
	OK = 0,
	E_EMPTY
}

export int parsePort(const char[] text, thrown ParseError error)
{
	if (text.length == 0)
		throw ParseError.E_EMPTY;

	return parseDigits(text);
}
```

A thrown slot is part of the callable shape. A caller can catch the value,
forward it, or arrange cleanup with `finally`. Camp does not require a runtime
exception system in order to write code that reads like it has structured
errors.

Callbacks and async calls follow the same philosophy. A raw function pointer,
a delegate with context, a one-shot callable, an iterator, and an async
completion have different ownership stories, so Camp gives them different
source forms.

## ABI Boundaries Are First-Class

Camp compiles through C and native toolchains. That does not make Camp "C with
nicer syntax", and it does not make the generated C the real source of truth.
Camp source is the contract. The generated C, headers, metadata, and native
artifacts serve that contract.

The point is not that every Camp declaration lowers to one obvious C spelling.
Often it does not. A source value may carry more ABI pieces than it appears to
carry in Camp:

- an array is a view with elements and length;
- a delegate carries a call target and context;
- an interface value dispatches through a vtable-shaped contract;
- an async function is callback-shaped at the boundary;
- an iterator has protocol state.

For example, this Camp declaration is one source-level API:

```camp
export int sum(byte[] values);
```

A C-facing boundary has to make the array view concrete. Conceptually, that
means something closer to this:

```c
int sum(unsigned char* values_elements, size_t values_length);
```

The exact generated spelling belongs to the compiler and target, but the
design pressure is the important part: Camp lets you write the source-level
thing you mean while still making the ABI shape clean and predictable.

That same idea is why Camp cares about async, iterators, and class models as
ABI features. A C library can already express these patterns with structs,
function pointers, context pointers, opaque handles, and callbacks. Camp's job
is to let you write them as language features and still expose a clean native
surface.

## What Camp Leaves Out

Camp is not trying to smuggle a managed runtime through a C compiler. It avoids
features that only make sense when every caller shares the same runtime model.
In particular, Camp does not build the language around:

- runtime exception objects and stack-unwinding metadata;
- runtime reflection as a required feature;
- compile-time-only generics with no stable callable ABI;
- a large standard runtime full of complex, ABI-hostile container types;
- a universal target model that hides important platform differences.

This is a tradeoff. You give up some conveniences that managed languages can
provide when they own the whole world. In exchange, the language stays useful
when the world is a C header, a static library, an embedded firmware image, or
a target with unusual pointer and memory rules.

## What Camp Borrows

Camp is not shy about its influences:

- From C, it borrows seriousness about headers, native compilation, object
  layout, and toolchains.
- From C++, it borrows respect for construction, destruction, value layout,
  and zero-overhead intent.
- From C# and Java, it borrows comfortable declarations, properties,
  interfaces, and readable library surfaces.
- From Rust, it borrows the instinct that lifetimes and unsafe boundaries
  should be visible rather than folkloric.
- From modern languages generally, it borrows the expectation that named
  arguments, iterators, lambdas, and async code should be usable in ordinary
  source.

Camp combines those ideas around a different center: native ABI control and C
emission without a single required runtime.

## Where Camp Asks For Care

Camp's precision is useful, but it is not free.

You will sometimes need to choose the right source shape:

- `struct` when layout and value storage are the promise;
- `class` when identity, lifecycle, or opacity matter;
- `interface` when callers need a dynamic contract;
- `newtype` when an existing representation needs a distinct meaning;
- `delegate` or `once` when a callback carries context;
- raw pointers when the boundary really is raw.

You will also see native concerns earlier than you might in a managed
language. If an API allocates, you may need to know which allocator it uses. If
an API stores a callback, you may need to know how long the callback's receiver
and captured context remain valid. If an API is exported, you may need to care
about the source name, the native symbol, and the shape visible to consumers.

Those are not distractions from Camp's design; they are the design. Camp is for
code where those answers matter.

## When Camp Is Probably The Wrong Tool

Camp is probably not the easiest choice for:

- quick scripts;
- purely managed applications;
- projects that want memory management to be completely implicit;
- code that depends on a large built-in runtime;
- environments where target-specific ABI details should never be visible.

Camp starts to make more sense when the native boundary is real: when you are
writing a C-consumable library, building runtime or platform code, wrapping an
existing native API, targeting constrained environments, or designing a package
where ownership and ABI shape are part of the public surface.

## The Road Ahead

The next few chapters should make Camp feel concrete. First you will write a
small program and pull it apart. Then you will learn the declarations you can
make, the types you will use every day, and the difference between structs,
classes, functions, methods, callables, allocation, lifetimes, pointers, and
errors.

After that, the guide moves into the larger features: namespaces, enums,
newtypes, interfaces, generics, iterators, async calls, the standard library,
and native interop. The final reference chapters gather the syntax you will
want to look up while writing code: expressions, statements, operators,
attributes, and documentation comments.

The important thing to carry forward is this: Camp wants ordinary code to stay
ordinary, and important contracts to be visible. When the language feels
familiar, trust that familiarity. When it asks you to be more precise, it is
usually pointing at a boundary where precision will save someone trouble later.
