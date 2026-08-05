# Namespace-Prefixed Default Symbols

Status: accepted  
Proposal date: 2026-08-04  
Last updated date: 2026-08-04

## Summary

This proposal changes Camp's default native symbol construction so namespaced
declarations receive namespace-derived native symbol prefixes.

The change makes source namespace separation visible in the default native ABI:

```camp
namespace PixelLib;

public class Image
{
	public int getWidth();
}

public int min(int a, int b);
public int CreateImage();
```

Default native symbols:

```text
PixelLibImage
PixelLibImage_getWidth
PixelLib_min
PixelLib_CreateImage
```

`@symbol` supplies a complete final native symbol. Namespace prefixing applies
only when a declaration is using its default native symbol.

This proposal changes native symbol construction only. It does not make the
generated native symbol spelling a Camp source name. Source lookup continues to
use source identifiers, ordinary imports, member access, and `Namespace::name`
qualification.

## Motivation

### Namespaces Should Prevent Routine Symbol Collisions

Camp source lookup treats these as distinct declarations:

```camp
namespace Std;

public int min(int a, int b);
```

```camp
namespace MyLibrary;

public int min(int a, int b);
```

Their default native symbols should also be distinct:

```text
Std_min
MyLibrary_min
```

Without namespace-prefixed defaults, a user must attach explicit symbols merely
to avoid ordinary collisions with the standard library or another static
dependency. Namespaces should provide that collision resistance by default.

### Native Library APIs Are Usually Prefixed

C-facing library APIs commonly use prefixes:

```text
SDL_CreateWindow
sqlite3_open
curl_easy_init
```

A Camp library namespace naturally provides that prefix:

```camp
namespace SuperLib;

public class Allocator
{
}

public int open();
```

Default native symbols:

```text
SuperLibAllocator
SuperLib_open
```

This gives libraries predictable default ABI hygiene while still allowing exact
native spelling through `@symbol`.

## Symbol Policy

### Category-Based Defaults

Default native symbol construction depends on declaration category:

- type declarations use namespace prefix joining;
- top-level functions, globals, and constants use `Namespace_Name`;
- type members use `Type_member`;
- generated declarations use the effective symbol of the source declaration or
  containing type that owns them.

This category split keeps type names visually distinct from top-level callable
and storage symbols:

```camp
namespace PixelLib;

public class Image
{
}

public int Image();
```

Default native symbols:

```text
PixelLibImage
PixelLib_Image
```

### Type Prefix Join Rule

Type declarations build their default native symbol by joining the namespace
prefix and type name:

```text
join(prefix, name):
    if prefix is empty: name
    if name starts with an uppercase ASCII letter: prefix + name
    otherwise: prefix + "_" + name
```

Examples:

```text
join("PixelLib", "Image") => PixelLibImage
join("PixelLib", "image") => PixelLib_image
join("pixellib", "image") => pixellib_image
```

The case test should use the first emitted identifier character after
source-to-symbol name normalization.

### Top-Level Functions, Globals, And Constants

Top-level non-type declarations insert an underscore between the namespace
prefix and declaration symbol regardless of the declaration's casing:

```camp
namespace PixelLib;

public int min(int a, int b);
public int CreateImage();
public int MaxChannels;
```

Default native symbols:

```text
PixelLib_min
PixelLib_CreateImage
PixelLib_MaxChannels
```

### Type Declarations

Type declarations use the type prefix join rule:

```camp
namespace PixelLib;

public class Image
{
}

public class image
{
}
```

Default native symbols:

```text
PixelLibImage
PixelLib_image
```

The result is the type's effective native symbol unless `@symbol` overrides it.

### Type Members

Type members use the containing type's effective native symbol as their prefix:

```text
<EffectiveTypeSymbol>_<MemberSymbol>
```

Example:

```camp
namespace PixelLib;

public class Image
{
	public int getWidth();
	public static int CreateDefault();
}
```

Default native symbols:

```text
PixelLibImage_getWidth
PixelLibImage_CreateDefault
```

Member symbols always use `_` between the containing type prefix and the member
symbol.

### Generated Iterator State

Generator factories and generated iterator state use the same category policy.

Top-level struct iterator generator:

```camp
namespace PixelLib;

public struct iter int counter(int start)
{
	yield start;
}
```

Default native symbols:

```text
PixelLib_counter
PixelLib_counterIter
PixelLib_counterIter_next
PixelLib_counterIter_destroy
PixelLib_counterIter_op_iter
```

PascalCase top-level struct iterator generator:

```camp
namespace PixelLib;

public struct iter int Values()
{
	yield 1;
}
```

Default native symbols:

```text
PixelLib_Values
PixelLibValuesIter
PixelLibValuesIter_next
PixelLibValuesIter_destroy
```

Member struct iterator generator:

```camp
namespace PixelLib;

public class Image
{
	public struct iter int scanRows()
	{
		yield 1;
	}
}
```

Default native symbols:

```text
PixelLibImage_scanRows
PixelLibImage_scanRowsIter
PixelLibImage_scanRowsIter_next
PixelLibImage_scanRowsIter_destroy
```

Struct iterator state that is part of a public/exported API must use these
effective symbols in API and metadata output. Class iterator state remains
represented through the class iterator protocol and opaque class identity.

### Other Generated Declarations

Generated declarations should derive default symbols from the effective native
symbol of the source declaration or containing type that owns them.

This includes:

- lifecycle helpers;
- create/delete/init helpers;
- vtable and interface helpers;
- lambda and delegate adapters;
- async frames and continuations;
- iterator state methods and protocol adapters;
- generated formatting or interpolation helper stubs.

Generated helpers participate in collision analysis even when they are not part
of source API metadata.

## `@symbol`

`@symbol` supplies the complete final native symbol for the declaration it is
attached to.

```camp
namespace PixelLib;

@symbol("NativeImage")
public class Image
{
	public int getWidth();
}

@symbol("native_min")
public int min(int a, int b);
```

Effective native symbols:

```text
NativeImage
NativeImage_getWidth
native_min
```

The explicit symbol on `Image` becomes the effective native type symbol and
therefore the prefix for default member/helper symbols. The explicit symbol on
`min` is used exactly as written.

## Export Projections

Export projections use the projected source name to compute default exported
symbols when no explicit symbol is present.

```camp
namespace SuperLib;

public class Allocator
{
}

export Allocator;
```

Default exported symbol:

```text
SuperLibAllocator
```

Renamed type projection:

```camp
export Allocator as MemoryArena;
```

Default exported symbol:

```text
SuperLibMemoryArena
```

Renamed function projection:

```camp
namespace SuperLib;

public int createAllocator();

export createAllocator as CreateAllocator;
```

Default exported symbol:

```text
SuperLib_CreateAllocator
```

This proposal does not introduce projection-level `@symbol` syntax. Export
projections therefore use the projected source name and the current namespace
to compute a projected default symbol. A future proposal may add an explicit
projection-level symbol override if a separate projected ABI spelling is
needed.

## Extern Declarations

Extern declarations without `@symbol` use the same default symbol policy:

```camp
namespace Posix;

public extern int getpid();
```

Default native symbol:

```text
Posix_getpid
```

Extern wrappers for existing platform APIs should use `@symbol` when the native
ABI spelling is not the Camp default:

```camp
namespace Posix;

@symbol("getpid")
public extern int getpid();
```

Native symbol:

```text
getpid
```

## Diagnostics

Required diagnostics include:

- default native symbol collision after namespace prefixing and `@symbol`
  overrides are applied;
- explicit `@symbol` collision;
- generated helper symbol collision;
- projection symbol collision after projected-name symbol construction;
- extern declaration without `@symbol` that appears to intend a known platform
  ABI symbol, if the compiler has enough target/platform knowledge to warn
  reliably.

## Compiler Impact

Implementation areas:

- declaration model:
  - store each declaration's effective namespace;
  - store default native symbol separately from effective native symbol;
- symbol computation:
  - centralize default native symbol construction;
  - implement category-based namespace symbol policy;
  - apply `@symbol` as a complete final symbol override;
  - derive generated helper symbols from owning declaration/container effective
    symbols;
- export projections:
  - compute default exported symbols from projected names;
  - integrate symbol construction with projection visibility and dependency
    checks;
- C emission:
  - consume effective native symbols rather than reconstructing names locally;
- metadata/API output:
  - preserve source names and namespaces;
  - include effective symbols where symbol metadata is emitted;
  - avoid exposing generated helper declarations as source API;
- collision analysis:
  - validate collisions after effective native symbols are computed.

There should be one canonical effective-native-symbol path for source
declarations, with `@symbol` taking precedence.

## Test Plan

### Symbol Tests

- namespace-prefixed symbols for top-level functions;
- namespace-prefixed symbols for globals and inline constants;
- top-level PascalCase functions use `Namespace_Function`;
- PascalCase type declarations use joined namespace prefixes;
- lowercase type declarations insert `_`;
- lowercase namespace/name joins insert `_`;
- no-namespace declarations keep unprefixed default symbols;
- `@symbol` supplies the complete final symbol;
- type member symbols use the effective type symbol prefix;
- generated helper symbols use their owning declaration/container prefix.

### Iterator Tests

- top-level struct iterator factory uses `Namespace_name`;
- top-level struct iterator state uses type prefix joining;
- PascalCase generator factory and generated state follow different category
  rules;
- member iterator generator state uses the containing type effective symbol;
- exported struct iterator state appears in API/metadata with effective symbols;
- class iterator state uses opaque/API-hidden representation as appropriate.

### Projection And Extern Tests

- bare type projection uses namespace-prefixed type symbol;
- renamed type projection uses projected name as symbol basis;
- renamed function projection uses `Namespace_Function`;
- projection-level `@symbol` is not part of this proposal and is therefore not
  tested;
- extern declarations without `@symbol` use namespace-prefixed defaults;
- extern declarations with `@symbol` use exact native symbols.

### Collision Tests

- same-named functions in different namespaces no longer collide by default;
- same effective native symbol diagnoses after prefixing;
- explicit `@symbol` collisions diagnose;
- generated member/helper symbols participate in collision checks.

## Documentation Plan

Update living documentation only:

- language guide:
  - symbols and `@symbol`;
  - namespace guidance for library prefixes;
  - export projection symbol examples;
  - extern wrapper guidance for platform ABI names;
  - iterator generated state naming where API/metadata is affected;
- semantics supplements:
  - metadata/API/symbol model;
  - C emission symbol construction;
  - generated declaration symbol ownership;
  - generated iterator state visibility;
  - collision analysis;
- compiler docs:
  - metadata JSON symbol fields if effective-symbol output changes.

The namespace documentation should recommend shallow namespace use: libraries
usually use one namespace, executables often use none, and nested namespaces are
best reserved for adapting or extending another named API surface.

Do not update accepted or rejected completed proposals and do not update prior
release notes as part of this work. Those files are historical records.

## Compatibility

This is a preview-language breaking change.

Expected changes:

- default native symbols for namespaced declarations change;
- generated C snapshots that assert symbol names must be updated;
- declarations relying on old unprefixed extern/import names need explicit
  `@symbol`;
- API/metadata snapshots that expose struct iterator state or emitted symbols
  must be updated.

## Recommendation

Adopt this change while Camp remains in preview.

Namespace-prefixed default symbols fix a real collision problem and make the
default native ABI match normal C library prefix conventions. `@symbol` remains
the exact-spelling tool for platform APIs and deliberate ABI design.
