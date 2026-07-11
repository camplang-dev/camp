# Namespaces, Imports, And Visibility

## Qualified Names

Camp uses `::` to qualify names.

```camp
Std::Console.writeLine("hello");
```

A qualified name resolves through visible declarations, imports, and namespace
exports. The last identifier is the local declaration name.

## `using`

`using` imports a namespace. It may import a namespace directly, import it with
an alias, or import selected names.

```camp
using Std;
using Platform::Windows as Win;
using Std::Math { min, max };
```

Imports affect source lookup. They do not change the exported namespace of the
current module.

## `export as`

`export as` gives the module an exported namespace.

```camp
export as Samples::Text;
```

Exported declarations are emitted under that namespace in API views and
metadata.

## Visibility Keywords

`export` declares API surface intended for external consumption. `public`
declares public source surface that is visible beyond private implementation
scope. Declarations with neither keyword are private to the compilation scope
where they are visible.

`extern` declares a symbol implemented outside Camp. It is commonly used for C
functions, native types, and interop boundaries.

## Source Symbols And ABI Symbols

The source name is the Camp name used in code. The ABI symbol is the emitted
native name. Most declarations use a derived symbol. `@symbol` overrides the
native symbol when an API must bind to a specific external name.

```camp
@symbol("puts")
extern int cPuts(const char* text);
```

The metadata and compiler supplements describe emitted API and metadata naming.

## Name Lookup Summary

Name lookup starts from local declarations, parameters, receiver members, type
members, imports, and visible namespace declarations. Generic parameters and
lifetime anchors participate in the scopes where their declaration makes them
available. Ambiguous or missing names are compiler errors.

## Default Visibility

Declarations are private by default. A private declaration can support exported
surface internally, but callers outside the visibility boundary cannot name it
unless it appears through an exported API view that makes it available.

`public` is source visibility. `export` is ABI/API visibility. Use `export` for
the declarations that form a module boundary and `public` for declarations that
should be visible to other Camp source without necessarily becoming native ABI
surface.

## Exported Members Of Non-Exported Types

An exported member of a non-exported type is useful only when the containing
type is itself visible enough for consumers to name or reach it. Avoid APIs that
export isolated members of otherwise private shapes unless the compiler's API
view makes the relationship clear.

## Foreign Definitions

`extern` declarations import native or separately compiled definitions. The
source name is the Camp name. The ABI symbol is the external name, either
derived by the compiler or supplied with `@symbol`.

```camp
@symbol("strlen")
extern nuint nativeStringLength(const char* text);
```

Use extern declarations to keep native boundary details in one place. Wrap them
with ordinary Camp functions when callers should use counted arrays, `thrown`
slots, lifetimes, or safer types.

## Aliases

Aliases introduce another source name for an existing target.

```camp
export alias ByteCount = nuint;
```

Aliases help document intent without creating a new nominal type. Use `newtype`
when the API needs a distinct nominal boundary.

## Generated Views

The compiler can produce public, export, metadata, and lowered views. These
views are related but not identical. Source lookup uses source visibility and
imports. Metadata uses visibility filters. Native ABI emission uses export and
symbol rules. Do not infer one view's shape from another without checking the
relevant documentation.

## Namespace Qualification

Namespace qualification uses `::`:

```camp
Std::Console.writeLine("ready");
Std::Time::Date today = Std::Time::Date.today();
```

The namespace prefix qualifies source lookup. It is not a runtime object and
does not allocate storage. A qualified name can refer to a namespace member,
type, function, alias, enum value, or other visible declaration depending on
context.

Member access still uses `.` after the qualified name:

```camp
Std::Console.writeLine("ready");
```

`Std::Console` is the qualified type name. `.writeLine` is ordinary static
member access.

## Selected Imports

A selected import brings only named exported symbols from a namespace into the
current file's unqualified lookup.

```camp
using Std::Math { min, max };

int smaller = min(left, right);
```

Other names in the namespace remain available by qualification:

```camp
Std::Console.writeLine("ready");
```

When a selected import names a type, the type and that type's method-symbol
family are imported. This lets receiver-style methods remain usable when the
type itself is selected.

Use selected imports when a file needs a small number of helpers from a large
namespace. Use whole-namespace imports for ordinary application code where the
namespace is the file's working vocabulary.

## Import Aliases

An import alias gives a shorter local namespace name:

```camp
using Platform::Windows as Win;

Win::createWindow(title);
```

When an alias is declared, use the alias in that file. The alias affects source
lookup only; it does not change the exported namespace or emitted symbol names.

## Export Namespaces

`export as` sets the namespace under which exported declarations from the file
or module appear in API views and metadata.

```camp
export as MyCompany::Graphics;

export struct Color
{
	byte r;
	byte g;
	byte b;
	byte a;
}
```

Consumers can then qualify the exported declaration:

```camp
MyCompany::Graphics::Color color = default;
```

A module namespace is source/API structure. It is erased as a runtime concept.

## Visibility And Headers

`export` and `public` create different surfaces:

| Visibility | Source lookup | Public ABI/API output | Metadata default |
|---|---|---|---|
| private | current private scope | no | no |
| `public` | visible to broader Camp source | no public ABI by itself | with `public` or `all` metadata |
| `export` | visible and exported | yes | with `export`, `public`, or `all` metadata |

Use `public` for reusable implementation surface that other Camp code should
see without becoming a native ABI commitment. Use `export` for module boundary
surface.

Type-specific rules still apply. For example, an exported struct exposes
layout, while an exported class is opaque across the public ABI.

## Private Helpers Behind Exported APIs

Private declarations can support exported APIs:

```camp
struct ParseState
{
	nuint index;
}

export int parsePort(const char[] text, thrown ParseError error)
{
	ParseState state = default;
	...
}
```

Consumers can call `parsePort`, but they cannot name `ParseState` unless it is
made visible. This lets modules keep implementation details out of the public
surface.

## Alias Boundaries

An alias is another name for an existing target. It does not create a nominal
type:

```camp
export alias ByteCount = nuint;
```

Use aliases for target/platform spelling, convenience, or compatibility. Use
`newtype` when two values with the same representation must not be
interchanged accidentally.

Alias targets are names, not arbitrary type expressions. Alias the named
concept, then use normal type spelling at the use site:

```camp
export alias NativeChar = wchar;

NativeChar* path;
```

## Extern Visibility

`extern` does not automatically mean exported. It means the implementation is
outside the Camp body.

```camp
extern int privateNativeHelper(int value);

export extern int publicNativeEntry(int value);
```

Use `export extern` for native declarations that consumers should see. Use
plain `extern` for private foreign helpers wrapped by safer Camp APIs.

## Name Lookup Pitfalls

Ambiguity is an error. If two imports bring the same unqualified name into
scope, qualify the name or use selected imports.

```camp
using Graphics;
using Terminal;

Graphics::Color color = default;
```

Prefer qualification at module boundaries and in examples where it improves
readability. Prefer imports inside implementation files when repeated
qualification would obscure the logic.
