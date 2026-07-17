# Camp LLM Coding Guide

This guide is optimized for an LLM agent that needs to write Camp code with a low
error rate. It is not a tutorial and it is not the canonical language reference.
Use it as a generation checklist together with the generated metadata for the
standard library and any project libraries in scope.

The canonical references are:

- [docs/language/01-camp-in-one-page.md](language/01-camp-in-one-page.md) for the start of the source-language guide.
- [docs/compiler/index.md](compiler/index.md) for command-line, package, build,
  and metadata behavior.
- [docs/semantics/index.md](semantics/index.md) for compiler-facing lowering and
  compatibility rules.

When this guide disagrees with metadata or a more specific language document, the
more specific source wins. When an API is not present in metadata, do not invent
it.

## Agent Workflow

1. Read the local Camp source around the edit before generating code.
2. Read available `.campbuild`, package metadata, and exported API metadata before
   calling library functions.
3. Generate source-level Camp. Do not emit lowered C-like structs, manual
   vtables, callback frames, or ABI helper fields unless the file is explicitly
   compiler test input for that representation.
4. Prefer existing project naming, module layout, allocator style, error style,
   and namespace style.
5. Use meaningful example and local names such as `message`, `buffer`, `writer`,
   `position`, `packet`, or `result`. Avoid generic placeholder names.
6. Keep target-specific details at the boundary. Most Camp code should not depend
   on C symbol names, ABI spellings, local filesystem paths, or machine-specific
   settings.
7. If the required semantics are unclear after reading docs, source, metadata,
   and nearby tests, leave the smallest accurate implementation and record the
   documentation uncertainty in `docs/OutstandingIssues.md`.

## Core Mental Model

Camp is a systems language with explicit ABI control, structured ownership and
lifetime annotations, deterministic cleanup, interfaces, generics, async
lowering, and C-oriented interop. It borrows familiar surface ideas from C-like
languages, but an LLM should not assume C++, C#, Rust, or Swift rules by analogy.

High-impact distinctions:

- Same-name overloads are not ordinary Camp functions. Use `overload` selector
  families when a shared call name is required.
- `T[]` is an array view, not an owned container.
- `T[N]` is fixed-size storage. Preserve `fixed T[N] name` for embedded fields
  and local inline storage where the surrounding code uses the explicit storage
  marker.
- `.` is used for member access through values and pointers. Camp does not use
  `->`.
- Capturing callables require `delegate`, `once`, or an async-appropriate target,
  not plain `fn`.
- Generic code must state the capabilities it uses. `T: any` is intentionally
  restrictive.
- Unsafe casts do not tunnel into arrays, delegates, optionals, interfaces, or
  other expanded forms. Convert the carrier intentionally, then reconstruct the
  expanded value.
- Interface dispatch is a language feature. Source code should not hand-build
  Camp interface table storage.

## Files, Namespaces, And Build Prelude

Camp files may begin with prelude directives before ordinary source tokens.
Keep these at the top of the file and follow the style already used by the
package. Use `#build` only for command-line-style build pragmas, `#within` only
for the file's allocation policy, and `namespace` for the exported namespace.

```camp
#build --artifact exec
#within explicit

namespace Samples::App;

export int main()
{
    Console.writeLine("ready");
    return 0;
}
```

Use `.campbuild` files for project selection, targets, artifact names, output
locations, and local build choices. Use source `#build` pragmas for facts that
belong to the source surface, such as required package uses or include files.
`#within explicit` and `#within implicit` select whether allocation and deletion
must spell their allocation context in that file. Do not add absolute local paths
or private machine information to committed source; put local setup notes in
`local/`, which is intentionally not part of the repository.

Namespaces are part of symbol identity. Use `using` for readability when the
project already does so. When stdlib is enabled, the compiler provides an
implicit root `Std` import; do not add `using Std;` as boilerplate in generated
examples. Add an explicit root `Std` import only when you need to replace that
default with an alias such as `using Std as S;` or a selected import such as
`using Std { Console };`. Child namespace imports such as `using Std::Time;`
are still useful and do not replace the implicit root import. For generated
examples, prefer `PascalCase` namespace names and lower camel case locals unless
nearby code uses a different convention.

## Declarations And Visibility

Top-level declarations include functions, types, newtypes, interfaces, enums,
constants, extern declarations, target blocks, and overload families. Keep
declarations at the narrowest visibility that works:

- `internal` exposes a declaration to other Camp source in the current project.
- `public` exposes a declaration to statically linked Camp modules in the final
  artifact without making it external ABI.
- `export` exposes a declaration directly across the external API/ABI boundary.
- Export projections (`export Type { ... } as ExternalName;`) expose a selected
  external view of a `public` declaration. Use them when designing a shared
  library API that should differ from the internal Camp shape.
- Unmarked declarations are package- or namespace-local according to the language
  rules.
- `extern` declares a symbol implemented outside Camp.
- `@symbol("name")` controls native ABI spelling for symbol-bearing declarations:
  functions, methods, exported globals, static fields, inline constants,
  enum values, and ABI-visible class/struct/interface/enum/newtype
  declarations. It does not change Camp source lookup.

Do not combine visibility modifiers unless the docs or nearby code show that the
combination is valid for that declaration kind. In ordinary application and
static-library code, prefer `public` for declarations other modules need. Use
direct `export` for true external entry points such as `main`, native interop
surfaces, or declarations intentionally owned by the current shared-library ABI.
For example:

```camp
export int countWords(const char[] text)
{
    int total = 0;
    // ...
    return total;
}
```

For a shared library with a curated external API, write the implementation
surface as `public` and project the external view:

```camp
namespace Text;

public class Counter
{
	public int getValue() => 0;
}

export Counter { getValue as value } as text_counter;
```

Every type mentioned by an exported function or projected member must itself be
exported or projected. Do not rely on the compiler to leak standard-library or
dependency declarations into your external API; re-export the specific public
types you want callers to see.

Use semicolons for declarations without bodies and braces for bodies. Expression
forms are useful only when the surrounding source already uses them and the
language docs confirm the exact syntax for that declaration.

## Overloads

Camp does not use ordinary same-name overload resolution. If several operations
must share one call name, mark the selector parameter with `overload`. This
makes overload selection explicit and metadata-friendly.

```camp
export void write(overload int value)
{
    // ...
}

export void write(overload const char[] text)
{
    // ...
}
```

When writing new code, first ask whether distinct names are clearer. Use overload
families for APIs that genuinely benefit from a shared call site and already fit
the package's public style. Do not create multiple top-level functions with the
same name and different parameter lists unless every entry is a valid overload
entry.

The selector must be the first non-`this` formal parameter, a declaration may
have only one selector, and the selector may not have a default value. Do not mix
ordinary declarations and overload declarations in one family. A generic
selector type must contribute a concrete method-symbol fragment; unconstrained
`T: any` is too weak for selector naming.

## Types At A Glance

Use this table as a generation checklist, then consult the language docs for
full details.

| Shape | Meaning | Agent notes |
| --- | --- | --- |
| `bool`, integer, float types | Built-in scalar values | Use explicit widths when ABI or serialization matters. |
| `char`, `const char[]`, `string` | Character and text forms | Confirm the exact string API from metadata. |
| `T*` | Pointer to `T` | Use `.` for member access. Do not write `->`. |
| `const T`, `volatile T`, `constof(...)` | Type qualifiers | Preserve qualifiers through APIs. Do not cast them away casually. |
| `T[]` | Array view over contiguous elements | Not ownership by itself. Track backing storage separately. |
| `fixed T[N] name` | Inline fixed-size storage | Preserve this spelling for embedded fields and local fixed storage where used. |
| `T?` | Optional value | Check presence before using the payload. |
| `fn R(...)` | Plain function pointer | No captures. |
| `delegate R(...)` | Callable with context | Use for captures and stored callbacks. |
| `once R(...)` | Callable consumed at most once | Common for continuations and async completion. |
| `escaped`, `scoped`, `within` | Lifetime and allocation controls | State them when values cross storage or async boundaries. |
| `struct(T)` | Materialized storage for expanded `T` | Needed for arrays or storage of expanded values. |
| `void*`, `fn*`, `nint`, `nuint`, `untyped` | Raw carriers | Keep raw spans short and reconstruct typed values explicitly. |

## Variables And Assignment

Prefer explicit types where they clarify ABI, ownership, or generic capability.
Use inference only when nearby code and compiler behavior make the result
obvious.

```camp
int count = 0;
const char[] title = "Camp";
Position position = Position(3, 4);
```

Assignments and argument passing preserve Camp's type, qualifier, lifetime, and
ownership rules. Do not assume a C-style implicit conversion is allowed just
because the native representation is compatible. When a value's lifetime,
constness, or representation changes, make the conversion visible and prefer an
API that expresses the intent.

## Arrays And Text

`T[]` is a pair-like view of elements and count, not a container that owns memory.
The view is only valid as long as its backing storage is valid. Do not return an
array view over local stack storage or store a view beyond the backing lifetime.

```camp
export int sumScores(int[] scores)
{
    int total = 0;
    for (int index = 0; index < scores.length; index += 1)
    {
        total += scores[index];
    }
    return total;
}
```

Use fixed storage when the array lives inline in a struct or stack variable:

```camp
struct PacketHeader
{
    fixed byte[16] identifier;
    int payloadLength;
}
```

Expanded values need materialized storage when one addressable storage object is
required:

```camp
struct(char[]) storedText;
struct(delegate void()) storedCallback;
```

Do not guess standard-library string helpers. Use metadata for operations such as
formatting, parsing, allocation, encoding, and comparison.

## Optionals

Use `T?` when absence is an ordinary value-level result. Use `thrown` when
absence is a failure path that should participate in propagation.

```camp
int? maybeCount = default;
if (maybeCount.specified)
{
    int count = maybeCount.value;
}
```

Optional payload conversions are not raw reinterpretations. Casts do not tunnel
through optional payloads unless the language defines that conversion. If nested
optional storage is required, materialize the inner optional shape rather than
assuming direct `T??` spelling is valid in every context.

## Pointers And Qualifiers

Camp pointer syntax is explicit and qualifier-sensitive. Preserve `const`,
`volatile`, and lifetime qualifiers unless the API is specifically designed to
convert them.

```camp
export int readLength(const PacketHeader* header)
{
    return header.payloadLength;
}
```

Member access through a pointer uses `.`. If you find yourself writing `->`, you
are writing C or C++, not Camp.

`constof(anchor)` expresses dependent constness. The anchor name is part of the
signature contract, so preserve parameter and receiver names that other type
slots depend on.

```camp
export constof(source) byte* firstByte(const byte[] source);
```

Use raw carriers only at interop or low-level boundaries:

```camp
@symbol("native_open")
extern void* nativeOpen(const char* path);

@symbol("native_close")
extern void nativeClose(void* handle);
```

After a raw conversion, reconstruct the typed value at the nearest safe point.
Avoid passing `void*`, `fn*`, `nint`, `nuint`, or `untyped` through ordinary
business logic.

Aggregate initializer arguments are allowed only when the parameter supplies a
safe storage rule. Passing `{ ... }` to `in T` or `const T*` is fine; the
compiler materializes a temporary and passes its address. Do not pass `{ ... }`
to a mutable `T*` parameter. Initialize a local and pass `&local` instead.

## Functions And Parameters

Parameter modifiers are semantic, not decorative. Preserve and generate them
when required by the value flow:

- `out` writes a result through an argument.
- `in` keeps a parameter input-only where the declaration requires it.
- `thrown` declares the error channel.
- `within` identifies the allocator or allocation context used by the operation.
- `escaped` means a value can outlive the current scope.
- `scoped` means the value is limited to the current scope.

```camp
export bool tryReadCount(
    const char[] text,
    out int value,
    thrown ParseError error)
{
    // ...
}
```

Use named arguments when they improve clarity or when an API requires them. Do
not silently drop `thrown`, allocator, or lifetime parameters just to make a call
shorter; either pass them through or handle them locally.

Call sites use `out` for output slots and `catch` for thrown slots:

```camp
int count = 0;
ParseError parseError;
bool ok = tryReadCount(text, out count, catch parseError);
```

A parameter named `this` is a receiver. Receiver functions can be called with
member-call syntax, and receiver qualifiers participate in constness, lifetimes,
virtual dispatch, interface matching, and callable references.

```camp
export nuint length(const Buffer* this);
nuint size = buffer.length();
```

## Callable Values

Use the narrowest callable shape that matches the behavior:

- `fn` for plain functions with no captured state.
- `delegate` for callbacks that carry context.
- `once` for continuations or callbacks consumed exactly once.
- Callable newtypes when the API needs a nominal callable identity.

```camp
export newtype fn bool TextParser(const char[] text, out int value);

export bool parsePort(const char[] text, out int value) : TextParser
{
    // ...
}
```

Do not cast one callable signature to another to bypass type checking. Write a
small adapter with the correct signature. If the callable crosses a lifetime
boundary, mark the callable or its captured data with the appropriate lifetime
qualifier.

Lambdas are target-typed. A capturing lambda needs a context-carrying target such
as `delegate`, `once`, or an async callable shape; a plain `fn` target is for
context-free functions.

```camp
delegate int(int value) doubleValue = value => value * 2;
```

Escaped lambdas can capture only values safe to store in their generated context.
When an escaped delegate or once lambda owns generated context, follow the
language docs and nearby code for `new delegate`, `delete delegate`, allocator,
and cleanup spelling.

Bound method references may use expanded receivers such as optionals or
delegate-like values. For non-escaped delegate targets, the compiler materializes
the receiver components into temporary context storage and generates an adapter.
For escaped delegate targets, do not rely on implicit stack materialization; put
the receiver in escaped materialized storage first or redesign the API.

## Lifetimes, Allocation, And Cleanup

Camp lifetime annotations are part of correctness. The most common LLM mistake
is to store a scoped value in an escaped location or to return a view over storage
that has already ended.

Rules of thumb:

- Values passed as ordinary parameters are scoped unless the declaration says
  otherwise.
- Stored callbacks, retained array views, async continuations, and object fields
  often need `escaped` values.
- Allocations that outlive the current function should be tied to an explicit
  `within` context or owning type.
- Pair `new`, `init`, or native allocation with the corresponding `delete`,
  destructor, cleanup API, or `finally` block.

```camp
export Buffer* createBuffer(nuint capacity, within allocator)
{
    return within(allocator) new Buffer(capacity);
}
```

Use `finally` for deterministic cleanup when a function can leave through
multiple paths:

```camp
FileHandle handle = FileHandle.open(path) finally close();
```

If ownership is transferred, name the operation and type so the transfer is
obvious. If ownership is borrowed, do not call `delete` or native cleanup on the
borrowed value.

## Structs, Classes, And Lifecycle

Use `struct` for value-shaped data and inline storage. Use `class` for identity,
virtual dispatch, inheritance-oriented APIs, or reference semantics. Do not
translate C++ habits mechanically: a Camp struct can still have methods and
lifecycle rules, but it is not a class object.

```camp
export struct Position
{
    int row;
    int column;

    int manhattan()
    {
        return this.row + this.column;
    }
}
```

Classes can carry virtual behavior and participate in class-specific interface
dispatch:

```camp
export abstract class Writer
{
    abstract void write(const char[] text, thrown IoError error);
}
```

Constructors and destructors are part of the type contract. If a type owns memory,
native handles, array backing storage, or retained callbacks, make its lifecycle
explicit and follow existing project patterns for constructor and destructor
names. For `extern class`, do not assume Camp owns layout or lifecycle unless the
declaration says so; call the documented native create/destroy operations.

Value newtypes wrap values and should remain value-like. Do not add destructor
behavior to a value newtype unless the language docs and surrounding code show
that the wrapped representation owns a resource.

## Shadow Classes

Use a `shadow class` only when a foreign or cross-module base object owns its
layout and exposes `@getshadow`/`@setshadow` hooks for attaching Camp state. Do
not use a shadow class as a substitute for an ordinary Camp-owned class.

Agent-facing rules:

- A shadow class pointer is physically the base object pointer. The compiler
  stores shadow fields, interface slots, and virtual shadow state in generated
  shadow data reached through the hooks.
- The base surface must provide one usable `@getshadow` getter and one usable
  `@setshadow` setter. Do not hand-roll shadow storage or cast hook results in
  source code unless you are writing the hook implementation itself.
- Shadow constructors initialize shadow fields. The public `new` or create path
  creates the base object, allocates and installs shadow data, and then runs the
  shadow constructor body. `_op_initnew` does not allocate or install shadow
  data.
- Constructor binding is source-level. Do not infer constructor argument
  validity from lowered ABI parameters for delegates, `once`, strings, arrays,
  or other expanded values.
- Shadow classes cannot declare destructors or be stack-constructed with `init`.
- Cleanup normally belongs in a base lifecycle callback or interface handler.
  Release owned fields first, then call `delete shadow`.
- `delete shadow` deletes generated shadow data only. It does not delete the
  base object, call a destructor, clear the base hook slot, or delete a local
  variable named `shadow`.
- Do not access shadow fields after an obvious `delete shadow`.

## Interfaces And Dispatch

Interfaces describe callable contracts. Implementations attach methods to a
specific interface slot by using the implementation marker syntax shown in the
language docs and nearby code.

```camp
export interface ITextSink
{
    void write(const char[] text, thrown IoError error);
}

export class ConsoleSink: ITextSink
{
    void write(const char[] text, thrown IoError error) : ITextSink
    {
        Console.writeLine(text);
    }
}
```

Agent-facing rules:

- Bare `Interface` is the vtable-level form. `Interface*` is the ordinary
  interface-instance pointer used for calls.
- Classes and structs can implement interfaces, but their ABI representation and
  dispatch storage differ. Classes store hidden per-interface vtable-pointer
  fields; structs convert through scoped adapter storage. Do not assume a class
  implementation can be copied into a struct implementation.
- Required interface methods must be implemented. Default methods can be used
  when the interface defines them. Optional methods must be checked before use.
- Interface constructors and destructors are vtable entries for construction and
  cleanup contracts. They do not make the interface directly instantiable.
- Do not manually construct vtables or interface wrapper structs in source-level
  code.
- Generic code that needs an interface implementation must state the capability
  and pass or request `vtableof(T: Interface)` when required by the API shape.
- Interface values retain the lifetime and ownership constraints of the
  underlying implementation. Do not widen a scoped implementation into escaped
  interface storage.

When documenting or generating ABI-sensitive code, use the semantic supplement
instead of guessing representation details from C output.

## Enums, Newtypes, And Constants

Enums are nominal. Specify an underlying type when interop, serialization, or ABI
requires it; otherwise use the package's convention.

```camp
export enum LogLevel: uint
{
    Debug,
    Info,
    Warning,
    Error,
}
```

Use value newtypes for domain-specific values with the same representation as an
underlying type:

```camp
export newtype PortNumber: int;
```

Use callable newtypes for nominal callback APIs:

```camp
export newtype delegate void Completion(int status);
```

Inline constants are compile-time values. Do not take their address or treat them
as mutable storage.

```camp
export inline uint MaxPacketSize = 4096;
```

## Generics

Generic Camp code must declare what it does with `T`. Do not assume every generic
type can be copied, compared, default-constructed, sized, or placed into an array.

Common capability patterns:

- Use `T: any` when the code only passes, stores by address, or otherwise handles
  `T` without copying or requiring layout operations.
- Use `T: copyable` when the code copies, returns by value, or duplicates `T`;
  it is still erased, so fields may not store direct `T` values.
- Use `in T value` when generic values need input transport without granting the
  body ordinary copy or storage rights.
- Use interface constraints when the code calls interface methods on `T`.
- Use `sizeof(T)` when the code indexes, slices, allocates, enumerates storage
  of generic elements, default-fills erased storage, or copies `T: copyable`
  values through erased storage.
- Use `typenameof(T)` only for diagnostic or metadata-style names.
- Use `vtableof(T: SomeInterface)` when generic interface dispatch needs a table.
- Use `T*`, `T[]`, or explicit erased storage for generic type fields. A field
  declared as plain `T value;` is invalid even when `T: copyable`; locals of
  type `T` are allowed inside functions with the needed runtime size capability.

```camp
export T choose<T: copyable>(T left, T right, bool useLeft, sizeof(T))
{
    if (useLeft)
    {
        return left;
    }

    return right;
}
```

Arrays, optionals, delegates, interfaces, and other expanded values may need
materialized storage in generic contexts. Reach for `struct(T)` when the
language docs say the expanded value needs an addressable representation.

`classtype` and `this` return types are specialized tools. Use them only when the
surrounding code already uses class-generic construction or fluent APIs and the
language reference confirms the shape.

## Iteration

Use ordinary loops for simple indexed arrays and `foreach` or `iter` where the
API exposes an iterator shape.

```camp
export int countPositive(int[] values)
{
    int total = 0;
    foreach (int value in values)
    {
        if (value > 0)
        {
            total += 1;
        }
    }
    return total;
}
```

Iterator functions retain state between yields. Do not yield references, views,
or scoped values whose storage ends before the consumer can use them. If an
iterator needs cleanup, model the cleanup explicitly rather than relying on a
consumer to exhaust the sequence.

## Async And Postponed Work

Write async code at the source level and let the compiler lower it. Do not
generate callback state machines manually unless you are writing compiler tests
for async lowering.

Key constraints:

- Async operations complete through a continuation-like shape. Model a single
  completion result; wrap multiple success values in a result struct when needed.
- `thrown` errors remain part of the async function contract.
- Awaiting requires a valid resume strategy. Use the receiver `resumeAsync`
  pattern or `@awaitwith` where the docs and nearby code require it.
- `@noawait` means the function body must not suspend.
- Captured values that survive suspension need lifetimes compatible with the
  async frame.
- `once` callables are natural for continuations because they are consumed by
  completion.

Postponed work captures arguments and executes after the current operation
returns to the appropriate runtime context. Keep captured values small,
explicit, and lifetime-safe. Do not use postponed work as a substitute for clear
ownership transfer.

## Errors

Camp uses `thrown` error channels. A function that can fail should either handle
the error locally or expose a `thrown` parameter/result in its signature.

```camp
export int loadCount(const char[] path, thrown IoError error)
{
    const char[] text = readAllText(path, catch error);
    return parseCount(text, catch error);
}
```

Use `try`, `catch`, and `finally` according to the language docs and existing
style. Do not replace `thrown` errors with ad hoc boolean return values unless
the API intentionally uses a `tryX` shape with `out` parameters.

## Interop

Interop declarations should be narrow and explicit. Keep native spellings,
calling conventions, target-specific attributes, and raw carriers at the
boundary.

```camp
@symbol("native_read")
extern int nativeRead(
    void* handle,
    byte* buffer,
    int length);
```

Rules for generated interop code:

- Use `extern` for declarations implemented outside Camp.
- Use `@symbol` only where the native symbol name is part of the contract. On a
  type declaration it also supplies the default native prefix for generated ABI
  helpers and static members; member-level `@symbol` overrides the full member
  symbol.
- Use explicit integer widths for ABI-visible values.
- Keep ownership and cleanup paired in the Camp wrapper, not scattered through
  callers.
- Wrap raw handles in domain types when they are used outside the smallest native
  boundary.
- Do not assume host compiler, platform, package path, or local machine details.

## Standard Library Use

The standard library surface is intentionally not duplicated here. Use generated
metadata and existing imports to determine exact names, namespaces, signatures,
and error behavior.

Stable agent habits:

- Import only the namespaces the file needs.
- Use `internal` for project-only helpers, `public` for static-module API, and
  export projections for curated shared-library API.
- Prefer project helper functions over inventing new standard-library calls.
- Use `Console.writeLine` only where nearby code or metadata confirms it is
  available for the target.
- Do not invent collection types, string methods, formatting helpers, file APIs,
  or task APIs by analogy with other languages.
- Keep examples small and focused so API churn does not require broad doc edits.

## Common Anti-Patterns

| Anti-pattern | Use instead |
| --- | --- |
| Multiple same-name functions with different parameters | Distinct names or an explicit `overload` family |
| `pointer->member` | `pointer.member` |
| Treating `T[]` as owned storage | Track backing storage and lifetime explicitly |
| Returning an array view over local fixed storage | Allocate or store backing data in a valid owner |
| Passing or returning fixed-size arrays by value | Use a pointer to the fixed array or a `T[]` span view |
| Arrays of expanded values such as direct optional or delegate arrays | Use materialized `struct(T)` storage where required |
| Copying `T: any` | Add `T: copyable` and any required `sizeof(T)` |
| Casting a delegate to another delegate signature | Write an adapter with the correct signature |
| Passing expanded values through raw carriers | Convert the carrier intentionally and reconstruct the typed value |
| Storing scoped values in escaped fields or callbacks | Change ownership/lifetime or keep the value scoped |
| Hand-building interface tables in source code | Use interface implementation syntax and compiler lowering |
| Modeling async as a manually written callback frame | Write source-level `async` and valid resume annotations |
| Hiding failure in sentinel values | Use `thrown`, or an intentional `tryX` API shape |
| Adding absolute paths or local setup to committed docs | Put local-only details under `local/` |

## Final Self-Check Before Emitting Code

Before returning generated Camp code, verify:

- Every called API exists in metadata, nearby source, or docs.
- Visibility, namespace, and build directives match the package.
- No ordinary same-name overloads were introduced.
- No C/C++ member access, ownership assumptions, or implicit conversions leaked
  into the code.
- Array views, pointers, callbacks, interface values, and async frames have valid
  lifetimes.
- Generic code declares every capability it uses.
- Error channels are handled or forwarded.
- Allocations and native handles have deterministic cleanup.
- Interop details stay at the boundary.
- Example names are meaningful and avoid placeholder jargon.
