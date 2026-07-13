# Camp Language Guide Redesign

This document proposes a new shape for `docs/language`: a guide that a working
programmer can read from the beginning, but also return to as a reference. The
current language material contains a great deal of valuable information, but it
is arranged like a compiler specification. The redesign below keeps the
user-facing substance, removes documentation-internal chatter, and moves
compiler-facing details to `docs/semantics`.

The new guide should feel like an experienced Camp programmer is walking the
reader through the language: first the shape of a real program, then the values
and declarations they will write every day, then the features that make Camp
different.

## Proposed Topic List

1. Camp In One Page
2. Your First Camp Program
3. Declarations And Program Shape
4. Everyday Types: Values, Text, Arrays, And Optionals
5. Structs, Classes, And Object Lifetimes
6. Functions, Methods, And Callables
7. Lifetimes, Allocation, And `within`
8. Pointers, Constness, And Conversion Boundaries
9. Errors, Cleanup, And Ownership Flow
10. Names, Imports, And Visibility
11. Enums, Newtypes, And Inline Constants
12. Interfaces And Dynamic Dispatch
13. Generics And Capabilities
14. Iterators, `foreach`, And Generated Sequences
15. Async, Await, And Deferred Calls
16. The Standard Library In Practice
17. Native Interop And ABI Boundaries
18. Expressions, Statements, And Operators Reference
19. Attributes, Documentation Comments, And Metadata Hints

## 1. Camp In One Page

### Introduction Draft

Camp is a small native language for code that wants to stay close to memory,
ABI boundaries, and C toolchains without giving up readable source-level
contracts. It looks familiar on purpose: functions, structs, classes, pointers,
arrays, and `try` / `catch` do not ask you to relearn programming from scratch.
The surprises are in the places where C-family languages usually hide danger:
lifetimes are written down, allocation contexts travel through APIs, arrays and
delegates have visible shapes, and native interop is a first-class design
pressure rather than an afterthought.

This opening page should help the reader decide whether Camp is the kind of
language they came looking for. It should show a tiny program, explain the
language's center of gravity, and name the ideas that will come back throughout
the guide.

### Subsections

1.1 What Camp is trying to be  
1.2 A tiny program  
1.3 The ideas that make Camp different  
1.4 Where Camp feels familiar  
1.5 Where Camp asks for more precision  
1.6 When Camp is probably the wrong tool  
1.7 Where to go next

### How The Topic Unfolds

The page begins with Camp's purpose, not documentation logistics. It should
then show a compact `Hello, world` style program with `using Std;`, `main`, and
`Console.writeLine`. From there it should explain the handful of ideas that a
reader should keep in mind while reading the rest of the guide: source APIs are
explicit about ownership, lifetimes, allocation, callable context, and native
boundaries.

### What The Reader Learns

After reading this topic, the reader knows what Camp is for, what kinds of
projects it suits, and which differences from C, C++, C#, Java, and Rust are
worth paying attention to first.

### Keep In The Language Guide

- Language goals and non-goals, rewritten as prose rather than a checklist.
- Who Camp is for and who it is not for.
- Novel aspects and influences.
- The minimal program, moved earlier and made more concrete.
- A short directional map to the rest of the guide.

### Move To `docs/semantics`

- Documentation conventions such as "syntax used in examples".
- The current "reading order" and "what counts as user-facing detail" sections.
- Any explanation of how the documentation set itself is organized.

## 2. Your First Camp Program

### Introduction Draft

The fastest way to understand Camp is to write a small program and then pull it
apart. This chapter should start with something unthreatening: print a line,
read a value or argument when the surrounding tooling supports it, call a helper
function, and return a status. The point is not to teach every rule; it is to
make a Camp file feel like a place the reader can stand.

By the end, the reader should recognize the basic shape of a Camp source file:
imports at the top, declarations below, `main` as an ordinary exported function,
and standard-library names coming from `Std`.

### Subsections

2.1 A complete `Hello, world`  
2.2 Imports and `Std`  
2.3 `main` and status returns  
2.4 Local variables and simple calls  
2.5 Splitting work into helper functions  
2.6 What the compiler sees at a high level  
2.7 A slightly less tiny example

### How The Topic Unfolds

This topic should start with a complete file, then explain each line in the
order the reader encounters it. It should introduce `using Std;`, `Console`,
ordinary function calls, return values, and basic local variables. It should
avoid dragging the reader into grammar, tokenization, symbol emission, metadata,
or build-system details.

### What The Reader Learns

The reader can recognize and write a small Camp program, understands that
standard-library names are ordinary declarations, and has enough footing to
read the rest of the guide.

### Keep In The Language Guide

- The useful parts of "source file shape".
- `using Std;` and ordinary standard-library calls.
- The minimal program.
- A first encounter with functions, locals, strings, and `return`.

### Move To `docs/semantics`

- File tokenization, trivia, conditional compilation mechanics, and grammar
  notation.
- Detailed preprocessor behavior beyond the few directives a user must see in
  normal code.
- Build-system and command-line compiler material, which belongs in
  `docs/compiler`.

## 3. Declarations And Program Shape

### Introduction Draft

Once a Camp file stops being a toy, it becomes a collection of declarations:
functions, structs, classes, interfaces, enums, aliases, newtypes, and values
attached to those types. This chapter should give the reader a map of the
things they can declare and where those declarations can live.

The tone should be practical: "Here are the building blocks Camp gives you;
here is when each one earns its place." The formal grammar can wait.

### Subsections

3.1 Top-level declarations  
3.2 Functions and variables  
3.3 Type declarations  
3.4 Members inside types  
3.5 Static members and inline constants  
3.6 Constructors and destructors at a glance  
3.7 Generic declarations at a glance  
3.8 Declaration bodies and forward declarations  
3.9 Choosing the right declaration form

### How The Topic Unfolds

The chapter should begin with a small source file that declares a helper
function, a struct, and a class. From there it should introduce each declaration
kind by use, not by grammar. Later sections can deepen each topic; this page is
the reader's map.

### What The Reader Learns

The reader can look at a Camp source file and understand what kind of thing
each declaration introduces. They know where to go next for members, lifecycle,
interfaces, generics, and interop.

### Keep In The Language Guide

- Type declarations and member declarations.
- Variables, constants, functions, receivers, generic declarations, and
  interface implementation markers, at a user level.
- Modifier overview where it helps a reader understand source.

### Move To `docs/semantics`

- Declaration grammar summary.
- Declaration collection order and generated declaration authority.
- Invalid-combination matrices unless they are common enough to be useful in
  prose.
- Source symbol identity, metadata IDs, and generated-surface rules.

## 4. Everyday Types: Values, Text, Arrays, And Optionals

### Introduction Draft

Most Camp code is not written in terms of abstract type theory. It moves ints,
booleans, strings, slices of bytes, arrays of values, optional results, and a
few small records. This chapter should introduce the types a programmer will
touch in their first hour and explain the one Camp idea that matters early:
some source values are simple scalars, while others are compact views over
other storage.

The reader should come away seeing why `string`, `char[]`, `T[]`, and `T?` are
different tools, not interchangeable spellings.

### Subsections

4.1 Primitive values in ordinary code  
4.2 Integers, natural integers, and portability  
4.3 Booleans and conditions  
4.4 Characters and string pointer types  
4.5 Counted text with `char[]`  
4.6 Array views with `T[]`  
4.7 Fixed-size arrays when storage is inline  
4.8 Optional values with `T?`  
4.9 Literals, `default`, and `null`  
4.10 Choosing between string, array, optional, and pointer forms

### How The Topic Unfolds

The chapter should start with familiar scalar values and then move into the
forms that are more Camp-specific: counted arrays, strings as native pointer
types, fixed-size storage, and optionals. It should explain ownership as an API
contract, not as a property magically attached to `T[]` or `string`.

### What The Reader Learns

The reader can choose between scalar primitives, string pointer types, counted
arrays, fixed storage, and optionals. They understand that arrays are views and
that ownership must be stated by the API that produces or consumes the value.

### Keep In The Language Guide

- Primitive type table, but trimmed and placed after practical examples.
- String literals, character literals, `default`, `null`, and `untyped` where
  relevant.
- Array values, fixed arrays, array literals, indexing, slices, optionals, and
  strings.
- Standard array and string helpers in a minimal form.

### Move To `docs/semantics`

- ABI component ordering for arrays and optionals.
- Expanded-form lowering details and materialized storage rules.
- Token-level literal lexing details.
- Full literal portability matrices.

## 5. Structs, Classes, And Object Lifetimes

### Introduction Draft

Camp gives you both plain values and identity-bearing objects. A `struct` is
where you put data whose layout matters and whose value can be copied when its
fields allow it. A `class` is where you put identity, lifecycle, inheritance,
and opaque implementation details. This chapter should teach that split before
it talks about every modifier.

The reader should feel the same distinction they already know from systems
code: sometimes you want a record, sometimes you want an object, and the choice
affects construction, destruction, copying, dispatch, and ABI shape.

### Subsections

5.1 Structs as plain values  
5.2 Classes as identity-bearing objects  
5.3 Fields, static fields, and type-scope constants  
5.4 Methods and receivers  
5.5 Constructors and initialization  
5.6 Destructors and cleanup  
5.7 `new`, `init`, and `delete` in context  
5.8 Virtual, abstract, override, sealed, and extern types  
5.9 Class-relative `classtype`  
5.10 Layout promises and opacity  
5.11 Common lifecycle patterns

### How The Topic Unfolds

The chapter should begin with a `struct` and a `class` that model similar data
in different ways. Then it should layer in members, constructors, destructors,
allocation, and dispatch. `classtype` belongs here because it is a user-facing
class-family feature, but its ABI lowering details should stay out of the main
flow.

### What The Reader Learns

The reader can decide between `struct` and `class`, write members and lifecycle
functions, understand what `delete` does, and recognize when `classtype` helps
class-family APIs.

### Keep In The Language Guide

- Structs, classes, fields, methods, constructors, destructors.
- `init`, `new`, `delete`, and common lifecycle patterns.
- The user-facing meaning of virtual dispatch, sealed classes, extern classes,
  and `classtype`.
- A light explanation of layout and opacity where it affects API design.

### Move To `docs/semantics`

- Exact class layout, hidden fields, vtable slot construction, and generated
  accessor lowering.
- Constructor binding algorithms and definite-assignment enforcement.
- Detailed C emission layout rules.

## 6. Functions, Methods, And Callables

### Introduction Draft

Camp functions look familiar until they start carrying the information that C
usually leaves in comments: output slots, thrown error slots, allocation
contexts, receiver relationships, callable contexts, and one-shot callbacks.
This chapter should make those forms feel natural. It should show that a Camp
signature is not just a list of arguments; it is the contract for how a call
moves values, errors, allocation, and sometimes execution itself.

### Subsections

6.1 Function declarations  
6.2 Parameters, arguments, and named calls  
6.3 `out`, `thrown`, and `within` parameters at a glance  
6.4 Receiver methods and member-call syntax  
6.5 Receiver-preserving `this` returns  
6.6 Function values with `fn`  
6.7 Delegates and captured context  
6.8 `once` callbacks  
6.9 Callable newtypes and ascription  
6.10 Lambdas and method references  
6.11 Overloads and overload selectors  
6.12 Async and iterator callable surfaces at a glance

### How The Topic Unfolds

Start with ordinary functions and calls, then add Camp's extra signature
surfaces one at a time. `out`, `thrown`, and `within` should be introduced here
as things a reader will see in signatures, then receive deeper treatment later.
Callables should be explained through common use: pass a function, bind a
method, capture a little context, enforce a one-shot callback.

### What The Reader Learns

The reader can read and write Camp function signatures, understand receiver
methods, pass callable values, use lambdas, and recognize when a callable
newtype gives a public API a meaningful name.

### Keep In The Language Guide

- Function and parameter forms.
- Receiver methods and receiver-preserving `this` returns.
- Callable types, delegates, `once`, lambdas, method references, overloads, and
  callable ascription.

### Move To `docs/semantics`

- Callable ABI component order.
- Delegate context layout and deletion algorithms.
- Lowering of default arguments and callable thunks.
- Full overload-resolution internals.

## 7. Lifetimes, Allocation, And `within`

### Introduction Draft

Camp does not pretend memory relationships are obvious. If a value may be
stored, returned, captured, or kept past the call that produced it, the source
should say so. This chapter is where the reader learns the vocabulary:
`escaped`, `scoped`, `unscoped(anchor)`, and `within`.

The chapter should be honest but not frightening. The goal is to show that
lifetimes are how Camp keeps borrowed views, callbacks, interface adapters, and
allocation contexts from becoming folklore.

### Subsections

7.1 Why lifetimes are written in signatures  
7.2 `escaped`, `scoped`, and `unscoped(anchor)`  
7.3 Anchoring returned views to inputs  
7.4 Allocation contexts with `within`  
7.5 Source-level `new` and `delete` with allocators  
7.6 Captures, callbacks, and generated contexts  
7.7 Aggregates and containers with pointer-bearing fields  
7.8 Lifetimes across iterators and async suspension  
7.9 Lifetime casts as proof boundaries  
7.10 Common mistakes and how to fix them

### How The Topic Unfolds

Begin with a borrowed slice returned from a buffer. Use that to introduce
anchors, then move to allocation contexts and escaped storage. Only after the
core idea is clear should the chapter mention captures, aggregates, iterators,
and async frames.

### What The Reader Learns

The reader can read lifetime annotations as part of an API's ownership story,
choose the right annotation for common borrowed/retained values, and understand
why `within` belongs in signatures that allocate.

### Keep In The Language Guide

- Lifetime annotations and defaults at the level a caller and library author
  need.
- Anchors, `within`, source allocation, captures, aggregate/container rule, and
  async/iterator implications.
- Common pitfalls with examples.

### Move To `docs/semantics`

- Slot-fact and value-fact algorithms.
- Flow graph details and bounded relation solving.
- Analyzer implementation rules and diagnostic internals.

## 8. Pointers, Constness, And Conversion Boundaries

### Introduction Draft

Camp pointers are deliberately plain: a pointer is an address, not an ownership
story. The interesting part is what the type system will let you do with that
address. This chapter should explain pointer depth, `const`, dependent
constness with `constof`, target-specific pointer forms, and the line between
ordinary casts, unsafe casts, and raw ABI fences.

The reader should come away with a practical instinct: if a conversion changes
meaning, make that meaning visible.

### Subsections

8.1 Pointer values and member access  
8.2 Pointer depth and address families  
8.3 `const`, `volatile`, and `constof(anchor)`  
8.4 Dependent constness at call sites  
8.5 Target type specifiers and call specifiers  
8.6 Implicit and explicit conversions  
8.7 `unsafe` casts  
8.8 Raw carriers: `void*`, `fn*`, `nint`, `nuint`, and `untyped`  
8.9 When to reconstruct instead of cast  
8.10 Conversion examples worth remembering

### How The Topic Unfolds

The chapter should start with ordinary pointer use, then deepen into the
places where Camp differs from C: member access through `.`, const-preserving
APIs, and explicit raw carrier boundaries. The conversion material should be
written from a user's point of view: what source form should I write, and what
promise am I making?

### What The Reader Learns

The reader understands pointer forms, constness, dependent constness, raw
carriers, and when casts are appropriate versus when a value should be rebuilt.

### Keep In The Language Guide

- Pointer types and pointer-depth explanation.
- `const`, `volatile`, and enough `constof` to use APIs correctly.
- Target type specifiers at a source level.
- Conversion categories and examples.
- Raw carriers and reconstruction guidance.

### Move To `docs/semantics`

- Full conversion classifier and target policy tables.
- Signature-compatibility variance details beyond practical examples.
- Warning plumbing and diagnostic style.
- Expanded-form conversion lowering.

## 9. Errors, Cleanup, And Ownership Flow

### Introduction Draft

Camp error handling is designed around explicit error slots and cleanup that
stays close to the value being protected. A function can return its success
value normally while carrying a `thrown` error path beside it. A local can say
what should happen when scope exits. The result should feel less like an
exception mechanism bolted on top and more like part of the function's shape.

### Subsections

9.1 Error values and success defaults  
9.2 `thrown` parameters  
9.3 `throw`, propagation, and `catch` arguments  
9.4 `try` / `catch` blocks  
9.5 `finally` blocks  
9.6 Expression-level cleanup with `finally`  
9.7 `finally delete` and cleanup methods  
9.8 Cleanup through iterators and async calls  
9.9 Choosing error shapes for APIs  
9.10 Patterns for safe resource use

### How The Topic Unfolds

Start with a file-open style example: open, handle a thrown error, and close in
a `finally`. Then explain the syntax. Cleanup should be presented as part of
ownership flow: "who produced this value, and what promise cleans it up?"

### What The Reader Learns

The reader can write functions that report errors, catch them, propagate them,
and reliably clean up resources without guessing whether `delete` or a method
call is appropriate.

### Keep In The Language Guide

- Error values and `thrown` signatures.
- `throw`, `catch`, `try`, `finally`, expression cleanup, `finally delete`, and
  cleanup ownership.
- User-facing iterator and async error flow.

### Move To `docs/semantics`

- ABI expansion of thrown slots.
- Lowering of cleanup paths.
- Flow analysis internals for thrown state and cleanup registration.

## 10. Names, Imports, And Visibility

### Introduction Draft

Names matter most when a program becomes a library. Camp lets a file import
names for source convenience, export names for API consumers, and keep helper
surface private even when it supports public declarations. This chapter should
arrive after the reader has seen enough Camp code to care about organizing it.

### Subsections

10.1 Qualified names  
10.2 `using` whole namespaces  
10.3 Selected imports  
10.4 Import aliases  
10.5 `export as` and API namespaces  
10.6 `export`, `public`, and private declarations  
10.7 Extern declarations and native names  
10.8 Source names versus ABI symbols  
10.9 Aliases and when they help  
10.10 Organizing a small library

### How The Topic Unfolds

Start with a file using `Std` and a small project namespace. Then introduce
qualified names, selected imports, and exported API names. Keep symbol and ABI
material only where it affects source decisions.

### What The Reader Learns

The reader can organize source files, import names clearly, expose a public API,
hide helpers, and understand when `@symbol` is about native compatibility
rather than Camp naming.

### Keep In The Language Guide

- Qualified names, `using`, selected imports, aliases, `export as`, visibility,
  extern visibility, and practical name lookup pitfalls.

### Move To `docs/semantics`

- Name lookup algorithm details.
- Generated views, metadata namespace representation, and symbol collision
  mechanics.
- Header emission specifics.

## 11. Enums, Newtypes, And Inline Constants

### Introduction Draft

Not every meaningful value needs a class or struct. Sometimes an integer needs
a closed set of names; sometimes a native handle needs a stronger type than
`nint`; sometimes a constant should live in the API without becoming storage.
This chapter should show how Camp gives names to values without making them
heavier than they need to be.

### Subsections

11.1 Enums for named choices  
11.2 Underlying enum types and ABI width  
11.3 Enum values in status and error APIs  
11.4 Value newtypes for nominal wrappers  
11.5 Callable newtypes for named callback shapes  
11.6 Newtype methods and cleanup helpers  
11.7 Inline constants  
11.8 Type-scope inline constants  
11.9 Symbols for native compatibility  
11.10 Choosing between enum, newtype, and inline constant

### How The Topic Unfolds

Begin with a small status enum and a handle newtype. Then show how these tools
protect public APIs from raw integers and unstructured callbacks. Inline
constants come after that as the lightest naming tool.

### What The Reader Learns

The reader can give semantic names to small values, keep native handles from
being casually mixed with integers, and expose constants without inventing
storage.

### Keep In The Language Guide

- Enum usage, underlying types, enum status conventions.
- Value newtypes and callable newtypes.
- Newtype members and `this` behavior.
- Inline constants and type-scope constants.
- User-facing `@symbol` behavior.

### Move To `docs/semantics`

- Exact enum metadata representation.
- Constant evaluation dependency graph details.
- C emission details for typedefs and macros.

## 12. Interfaces And Dynamic Dispatch

### Introduction Draft

Camp interfaces are not structural duck typing and they are not hidden runtime
objects. They are named contracts with vtables, and types implement them
explicitly. That makes them useful when an API wants dynamic dispatch without
giving up a stable native shape.

This chapter should explain interfaces as a practical tool first: "I have code
that can write to many sinks" or "I want a reader abstraction." ABI details
should appear only where they change how users design APIs.

### Subsections

12.1 Interface declarations  
12.2 Implementing an interface with a class  
12.3 Implementing an interface with a struct  
12.4 Required, defaulted, and optional methods  
12.5 Checking optional slots  
12.6 Interface inheritance  
12.7 Constness and lifetime in interface contracts  
12.8 Constructors and destructors in interfaces  
12.9 Interface conversions  
12.10 `vtableof` in generic code  
12.11 Designing interface-based APIs

### How The Topic Unfolds

Start with a small interface and one class implementation. Then introduce
struct implementations as a scoped adapter story. Optional/defaulted methods
should be taught through capability checks, not through vtable tables. Save
bare vtable values and layout talk for short "why this matters" notes.

### What The Reader Learns

The reader can declare interfaces, implement them explicitly, call through
interface pointers, check optional slots, and understand the practical
difference between class-backed and struct-backed interface values.

### Keep In The Language Guide

- Interface declaration and implementation markers.
- Required, defaulted, optional methods.
- Class versus struct implementation model at a user level.
- Interface inheritance, conversions, constructors/destructors, and `vtableof`.

### Move To `docs/semantics`

- Exact vtable struct shapes and slot function types.
- Hidden class field generation, fixup thunks, and adapter layout.
- Metadata details for interface slots.

## 13. Generics And Capabilities

### Introduction Draft

Camp generics are explicit about what generic code is allowed to assume. A
type parameter can be "any type," but that does not mean the body can copy it,
construct it, find its size, print its name, or dispatch through an interface.
When generic code needs one of those powers, the signature says so.

This chapter should make Camp generics feel less like template magic and more
like honest API design.

### Subsections

13.1 Generic functions and types  
13.2 Constraints: `any`, `copyable`, and interfaces  
13.3 What `T: any` can and cannot do  
13.4 `T: copyable` and value-moving APIs  
13.5 Interface-constrained generics  
13.6 `sizeof(T)`  
13.7 `typenameof(T)`  
13.8 `vtableof(T: Interface)`  
13.9 Generic construction and destruction  
13.10 Generic arrays, optionals, delegates, and strings  
13.11 Patterns for containers and algorithms

### How The Topic Unfolds

Begin with a simple `Pair<T: copyable>` and a function that chooses between two
values. Then show why `T: any` is deliberately weaker. Capability parameters
should be introduced as answers to concrete needs: allocate storage, name a
type, dispatch through an interface.

### What The Reader Learns

The reader can write generic APIs that ask for the capabilities they use, avoid
copying under `T: any`, and understand why size, type-name, and interface
vtable capabilities are explicit.

### Keep In The Language Guide

- Generic declaration surface.
- Constraint meanings.
- `T: any`, `T: copyable`, interface constraints.
- Capability parameters and common design patterns.
- Generic interactions with arrays, lifetimes, and async at a user level.

### Move To `docs/semantics`

- Erasure and materialized storage details.
- Capability parameter lowering and hidden retention.
- Generic diagnostic implementation rules.

## 14. Iterators, `foreach`, And Generated Sequences

### Introduction Draft

Camp iterators let a value produce a sequence without pretending the sequence
is already an array. They can be plain callable values, generated state
machines, struct-backed, class-backed, and error-producing. This chapter should
teach the common path first: write a generator, consume it with `foreach`, and
understand where cleanup happens.

### Subsections

14.1 Iterator type forms  
14.2 Writing a generator  
14.3 `yield` and current values  
14.4 Consuming with `foreach`  
14.5 Iterators that can fail  
14.6 Manual iteration when needed  
14.7 Iterator cleanup  
14.8 Arrays and `foreach`  
14.9 Generic iterators  
14.10 Struct iterators versus class iterators  
14.11 Iterator pitfalls

### How The Topic Unfolds

Start with a small generator that yields a few values. Then move to `foreach`,
thrown flow, and cleanup. Manual protocol details should appear later and only
because they help readers understand interop and advanced iterator design.

### What The Reader Learns

The reader can write and consume iterators, understand how `yield` works, and
know when iterator state is stack-like, heap-like, or cleanup-sensitive.

### Keep In The Language Guide

- Iterator source forms, generator declarations, `yield`, `foreach`, thrown
  flow, cleanup, arrays, generic iterators, and struct/class iterator choices.

### Move To `docs/semantics`

- Exact iterator lowering.
- Current-slot storage and ABI protocol details.
- Generated state-machine internals.

## 15. Async, Await, And Deferred Calls

### Introduction Draft

Camp async is callback-shaped under the hood, but source code can still read
like a sequence of operations. The language does not bring a hidden task
runtime with it. Instead, resumption is explicit: an object or parameter says
how continuations resume.

This chapter should help the reader write awaitable APIs and consume them
without assuming Camp works like C#, JavaScript, or Rust.

### Subsections

15.1 Async as a callback-shaped callable  
15.2 Awaitable result shapes  
15.3 `await` and error propagation  
15.4 Resumer selection  
15.5 `resumeAsync`  
15.6 `@awaitwith`  
15.7 `@noawait`  
15.8 Async frames and lifetimes  
15.9 Manual async calls  
15.10 `once` completion callbacks  
15.11 `postpone` for deferred calls  
15.12 Async interop patterns

### How The Topic Unfolds

Begin with a small awaited function, then show the callback shape it implies.
After that, teach resumers and frame lifetimes. `postpone` belongs near the end
as a related deferred-call tool, not as the centerpiece of async.

### What The Reader Learns

The reader can call async functions, design awaitable completion shapes, choose
or provide a resumer, and understand why lifetimes across suspension are more
strict than ordinary local flow.

### Keep In The Language Guide

- Async callable shape, await result rules, resumer selection, `resumeAsync`,
  `@awaitwith`, `@noawait`, frames and lifetimes, manual calls, `once`, and
  `postpone`.

### Move To `docs/semantics`

- State-machine lowering.
- Frame allocation order.
- Tail-await lowering and completion callback plumbing.
- Metadata representation of async surfaces.

## 16. The Standard Library In Practice

### Introduction Draft

The standard library is not the language, but it is where most first programs
touch the language. This chapter should be a practical tour: console I/O,
strings, arrays, collections, files, formatting, math, time, and timing. It
should avoid becoming a full API reference that goes stale every time a method
changes.

The goal is to show the style of the library and point readers toward the
source or generated metadata for exhaustive signatures.

### Subsections

16.1 What belongs to `Std`  
16.2 Console I/O  
16.3 Strings and counted text helpers  
16.4 Arrays and copying helpers  
16.5 Formatting with `CharFormatter`  
16.6 Files and native handles  
16.7 Streams and readers/writers  
16.8 Collections  
16.9 Math helpers  
16.10 Time, timers, and atomics  
16.11 When to inspect library metadata or source

### How The Topic Unfolds

Use small examples that readers can adapt: print text, copy a string, open a
file with cleanup, append to a list, format a date. Keep the examples accurate
but avoid documenting every overload.

### What The Reader Learns

The reader can find and use the standard-library surfaces that make examples
work, while understanding that exact API coverage lives in source and metadata.

### Keep In The Language Guide

- Minimal standard-library overview.
- Representative examples for allocation, arrays, strings, formatting,
  console, streams, files, collections, math, and time.

### Move To `docs/semantics`

- None of the standard library API should move to semantics merely because it
  is library material. However, implementation details of adapters, metadata,
  and emitted ABI belong in semantics or compiler supplements.

## 17. Native Interop And ABI Boundaries

### Introduction Draft

Camp is meant to live near C and native libraries. This chapter should teach
how to write that boundary clearly: `extern`, `@symbol`, call specs, type
specs, raw function pointers, counted arrays versus C strings, native handles,
and ownership. It should be practical and careful, because interop is where
tiny misunderstandings become real bugs.

### Subsections

17.1 `extern` functions  
17.2 Extern classes and opaque native values  
17.3 `@symbol` and native spelling  
17.4 Call specs  
17.5 Type specs  
17.6 Raw function pointers  
17.7 Passing arrays to native code  
17.8 Passing strings to native code  
17.9 Native handles and cleanup methods  
17.10 Target-conditioned code  
17.11 Generated headers and metadata at a user level  
17.12 Interop design guidelines

### How The Topic Unfolds

Begin with a tiny `puts`-style example, then show why richer APIs need wrappers
around native details. The chapter should connect back to earlier topics:
pointers, strings, arrays, newtypes, cleanup, and conversions.

### What The Reader Learns

The reader can write extern declarations, wrap native APIs safely, decide when
to expose raw surfaces, and understand where ABI names differ from source
names.

### Keep In The Language Guide

- User-facing interop source forms and design guidance.
- Arrays/string boundaries, native handles, raw callbacks, target-conditioned
  code at a practical level.

### Move To `docs/semantics`

- Target capability resolution.
- C emission preconditions.
- Header generation details.
- Metadata object schema details.

## 18. Expressions, Statements, And Operators Reference

### Introduction Draft

This is the part of the guide readers come back to while writing code. Most of
it will feel familiar, so it should not pretend every operator needs a sermon.
The chapter should be compact but useful: expression typing, precedence, calls,
member access, construction, cleanup expressions, blocks, loops, `switch`,
`return`, `foreach`, `try`, `throw`, and the handful of places where Camp does
something unusual.

### Subsections

18.1 Expression typing and target typing  
18.2 Evaluation order and side effects  
18.3 Operator precedence  
18.4 Arithmetic, boolean, bitwise, and null-coalescing operators  
18.5 Assignment and update expressions  
18.6 Names, member access, properties, and indexers  
18.7 Calls, named arguments, and overload selectors  
18.8 Casts, construction, and initializer lists  
18.9 Special expressions: `sizeof`, `typenameof`, `vtableof`, `await`, `postpone`, `throw`, and `within`  
18.10 Blocks, locals, and fixed storage locals  
18.11 `if`, loops, `switch`, `break`, `continue`, and `goto`  
18.12 `return`, `yield`, `foreach`, `try`, `catch`, `finally`, and `delete`  
18.13 Conditions and discards  
18.14 Quick tables

### How The Topic Unfolds

This chapter should function as a reference. Put the unusual Camp-specific
forms near the familiar syntax they extend. Keep examples short. Cross-link to
the deeper chapters for errors, iterators, async, allocation, and conversions.

### What The Reader Learns

The reader can quickly answer "what is the syntax for this?" without wading
through compiler explanations.

### Keep In The Language Guide

- Most of the current expressions/operators and statements/control-flow
  material, reorganized and de-duplicated.
- Operator precedence and statement forms.
- Short notes where Camp differs from common C-family expectations.

### Move To `docs/semantics`

- Detailed grammar productions.
- Lowering order for expanded forms unless visible to source behavior.
- Diagnostic expectation lists.

## 19. Attributes, Documentation Comments, And Metadata Hints

### Introduction Draft

Most Camp code should not need many attributes, but public APIs sometimes need
to talk to native symbols, metadata consumers, or documentation tools. This
chapter should explain the attributes and doc-comment forms a library author
will actually write, without turning into a metadata schema manual.

### Subsections

19.1 Attribute syntax in ordinary source  
19.2 `@symbol` for native and API compatibility  
19.3 Index and range attributes  
19.4 Async attributes: `@awaitwith` and `@noawait`  
19.5 Documentation comment forms  
19.6 Documenting parameters, returns, errors, ownership, and lifetimes  
19.7 Links and symbol references in docs  
19.8 Deprecation and remarks  
19.9 When metadata matters to users  
19.10 Where the full metadata rules live

### How The Topic Unfolds

Start with the attributes a reader has already seen elsewhere, then show how
documentation comments help public APIs explain ownership and errors. Keep
metadata material as "what this means for readers and tools," not "how the JSON
is emitted."

### What The Reader Learns

The reader can use attributes and doc comments when writing public APIs, and
knows where to look for compiler-writer details.

### Keep In The Language Guide

- Attribute syntax and attachment rules at a user level.
- `@symbol`, `@index`, `@range`, `@awaitwith`, `@noawait`.
- Doc comment forms and useful API-documentation guidance.
- `symbolof` only as much as needed for doc/metadata links.

### Move To `docs/semantics`

- Doc comment lowering.
- Metadata JSON relationship and schema details.
- Source text preservation rules for metadata.
- Attribute argument serialization.

## Global Content Migration

### Move Out Of `docs/language`

The following material should be removed from the language guide or reduced to
a short cross-link:

- Grammar notation, token trivia, tokenizer behavior, and parser-oriented
  lexical details.
- Documentation-internal disclaimers, reading-order explanations, and
  "what counts as user-facing" rules.
- Metadata JSON schema, generated declaration authority, metadata IDs, and API
  dump details.
- Exact ABI component ordering for arrays, delegates, iterators, thrown slots,
  async completion callbacks, and interface vtables.
- Lowering algorithms for callables, lambdas, async frames, iterators,
  cleanup, constructors, generics, and conversions.
- Diagnostic implementation guidance and test-surface checklists.
- Target policy tables and C emitter details.

Most of that material already has a natural home in `docs/semantics`; material
about command-line compilation, packages, build directives, and project layout
belongs in `docs/compiler`.

### Keep In `docs/language`

The guide should keep anything a programmer needs to choose the right source
form:

- What a feature is for.
- How it looks in source.
- How it behaves at call sites.
- What promises it makes about ownership, lifetime, allocation, errors,
  constness, dispatch, and native boundaries.
- What common mistakes look like and how to write the intended code instead.
- Light ABI notes only when they affect API design.

## Index Strategy

The guide should support two reading modes:

1. A first-time path from Topic 1 through Topic 9, enough to write useful Camp
   code.
2. A reference path where the index lists every topic and subsection, so a
   reader can jump straight to `constof`, `finally delete`, `vtableof`,
   `postpone`, or `@symbol`.

The top-level `docs/language/index.md` should therefore list topics and
subsections only. It should not explain documentation conventions. Each topic
should begin with a short, human introduction, then unfold from examples to
rules to design guidance. Cross-links should replace repeated explanations.

## Tone And Prose Strategy

The new guide should speak to someone who already knows how to program. It does
not need to explain what a loop is. It does need to explain why Camp's version
of a familiar tool is shaped the way it is.

Use prose that answers the reader's real questions:

- "What problem does this solve?"
- "What will this look like in my code?"
- "What is the surprising part?"
- "What mistake am I likely to make coming from C, C++, C#, Java, or Rust?"
- "Where do I go for the deeper rules?"

Avoid beginning every section with the same mechanical pattern. A page can open
with an example, a problem, a contrast with C, or a design pressure. The reader
should feel that each chapter has a reason to exist.
