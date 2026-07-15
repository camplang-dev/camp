# Visibility, Namespaces, And Export Projections

## Status

Draft proposal.

## Summary

Camp currently uses `private`, `public`, and `export` as declaration
visibility levels, and uses `export as` to name a source namespace. That model
has become too overloaded. It does not clearly distinguish declarations that are
usable inside a project, declarations that are usable across statically linked
modules in one final artifact, and declarations that become part of a shared
library or executable boundary.

This proposal replaces the current model with:

- `namespace` as the source namespace declaration, replacing `export as`;
- `private` as the default file-local visibility;
- `internal` as the current-project visibility, replacing the old meaning of
  `public`;
- `public` as artifact-internal visibility, available across all statically
  linked Camp modules that make up the final artifact;
- `export` as the external ABI/API visibility, used for shared-library API
  surfaces and explicit executable entry points;
- explicit export projection declarations that let a module export selected
  public declarations, optionally under different external names.

The new model separates two concepts that are currently tangled together:

- source visibility: who can refer to the declaration while building Camp code;
- external projection: what shape and names are exposed outside the artifact.

The standard library is expected to become `public` instead of broadly
`export`. A library that uses `Std::Allocator`, `Std::List`, or other standard
types should not automatically publish the entire standard library from its own
headers. When a library wants to expose one of those types, it must project that
type deliberately.

## Motivation

### Library Headers Should Not Leak Dependencies

An image-processing library distributed as a shared library may depend on the
Camp standard library and on several static helper libraries. Today, exported
Camp API headers can easily include exported declarations from those referenced
modules. That is too broad. A consumer of the image-processing library should
see only the API the image-processing library chose to publish.

### Static Dependencies Need An Artifact-Internal Surface

A shared image-processing library may statically include a pixel-conversion
library. The image-processing library needs to call into the pixel-conversion
library, and may want to expose a few of its enums or helper functions, but it
should not automatically export the entire pixel-conversion API.

This is the role of the new `public`: visible throughout the final linked
artifact, but not visible outside the artifact unless explicitly projected.

### External APIs May Need Different Names

A low-level database library may internally use `Std::Allocator`, but its
external API might need a project-specific name such as `SuperdbAllocator`, or
might need `superdb_open` / `superdb_close` symbols. Those external names are
not necessarily the names used by the internal Camp implementation. Export
projection makes that distinction explicit.

### `export as` Is The Wrong Spelling

`export as Std::Math;` currently means "this file is in namespace Std::Math".
It does not export a symbol. Reusing `export` there makes the visibility model
harder to teach and harder to extend. This proposal renames it to:

```camp
namespace Std::Math;
```

## Old Model

The current compiler model is roughly:

| Spelling | Current meaning |
| -------- | --------------- |
| no visibility | visible within the current source file/compilation unit |
| `public` | visible within the current project/module |
| `export` | externally visible, emitted into generated API/header surfaces |
| `export as X` | declares the source namespace for a file/module |

Implementation details today reflect this model:

- declarations carry nullable `Export` and `Public` fields;
- ABI analysis maps declarations to `Private`, `Public`, or `Export`;
- project API and C header emission often filter directly on
  `definition.Export` and `definition.Public`;
- export-closure diagnostics ask whether an exported declaration exposes
  non-exported types;
- metadata modes use `export`, `public`, and `all` as output filters;
- `export as` is parsed beside `using` declarations and serialized into Camp
  API files.

This model has become too coarse for static dependencies, shared dependencies,
package APIs, and future wrapper generators.

## New Visibility Model

### Visibility Levels

| Spelling | New meaning |
| -------- | ----------- |
| no visibility | private, visible only in the current source file/compilation unit |
| `internal` | visible within the current project/module |
| `public` | visible across all statically linked Camp modules in the final artifact |
| `export` | visible outside the current shared library, or an explicit executable entry point |

`internal` is the direct replacement for the old meaning of `public`.

`public` is a new artifact-internal visibility. A public declaration can be
used by the root project and by other Camp modules that are statically linked
into the same final artifact. It does not automatically appear in the generated
external Camp API header, C header, C# wrapper, JavaScript wrapper, or metadata
export view.

`export` remains the external boundary. It is for shared-library APIs,
generated external headers/wrappers, and intentionally exported executable
symbols such as `main`.

### Terminology

This proposal uses the following terms:

- project/module: the unit described by a `.campbuild` file or equivalent
  compiler request;
- artifact: the final executable, static library, shared library, or metadata
  output being produced;
- static dependency: a Camp dependency linked into the same artifact;
- shared dependency: a Camp dependency consumed through its external API;
- export projection: a declaration that maps a `public` source declaration to
  an exported external declaration, possibly with a different name.

### Namespace Declaration

`namespace` replaces `export as`.

```camp
namespace Std;
namespace Pixel::Internal;
```

The namespace declaration is not a symbol export. It only contributes to source
name qualification and API/metadata naming. The old `export as` spelling should
be removed rather than kept as a compatibility alias.

After this change, `namespace` is a reserved word.

### `using` And Source Lookup

`using` must become an actual source lookup rule, not merely prelude metadata.

The compiler should enforce these rules before the visibility/export migration
begins:

- symbols in another namespace are not available to a file unless that namespace
  is imported, the symbol is selected by an import, or the reference uses a
  valid imported namespace alias;
- `using Namespace;` imports public/exported symbols from that namespace using
  their ordinary names, and also permits explicit `Namespace::Name`
  qualification in that file;
- `using Namespace { A, B };` imports only `A` and `B`;
- `using Namespace as N;` imports the namespace only through the alias `N`;
- unselected names are not available through a selected import;
- aliased namespaces are not also available through their original unaliased
  namespace name in that file unless separately imported.

The compiler currently adds an implicit `using Std;` when the standard library
is enabled. That behavior should remain, but it must not override explicit root
`Std` imports. If a file contains an explicit `using Std;`,
`using Std { ... };`, or `using Std as S;`, the implicit root `Std` import is
not added for that file. Imports of subnamespaces such as `using Std::Math;`
do not suppress the implicit root `Std` import.

Export projections resolve their source name through the same file-local source
lookup rules. If a declaration is imported under an alias, the projection uses
that alias spelling:

```camp
using PixelConvert as PC;

export PC::PixelFormat as ImagePixelFormat;
```

If a file uses selected imports, only selected declarations can be projected by
unqualified name:

```camp
using PixelConvert { PixelFormat };

export PixelFormat as ImagePixelFormat; // ok
export convertPixel;                    // error unless imported another way
```

The same import-aware lookup applies to ordinary Camp source. These forms should
work consistently anywhere the corresponding unqualified name would work:

```camp
using PixelConvert;
using Native::Windows as Win;

PixelConvert::PixelFormat format = default;
Win::HWND hwnd = default;
int value = PixelConvert::convert(value);
int width = PixelConvert::Image.getDefaultWidth();
```

Qualified names should be supported in type positions, expression positions,
call expressions, static member access, out-of-scope static type member access,
casts, local declarations, parameter declarations, return types, aliases,
`vtableof`, `typenameof`, metadata symbol references where applicable, and LSP
symbol navigation.

### `extern` Is Orthogonal

`extern` continues to describe implementation ownership. It does not imply
visibility.

Examples:

```camp
public extern void malloc(...);
export extern void superdb_open(...);
```

The first declaration is available to Camp modules inside the artifact. The
second declaration is part of the external API surface.

## Export Projections

Export projections let a module publish selected `public` declarations across
the external boundary.

They are declarations, not mutations of the original source declarations. Inside
the final artifact, Camp code continues to use the original declaration names.
Outside the artifact, generated API/header/metadata surfaces use the projected
external names.

### Supported Projection Targets

V1 export projection is limited to:

- class, struct, interface, newtype, enum, params, and alias types where the
  existing type kind is otherwise exportable;
- enum values, as intrinsic enum shape;
- struct instance fields, as intrinsic struct layout;
- inline constants;
- non-this functions;
- selected type members that are functions or inline/static constants.

Mutable globals and mutable static fields are not re-exportable in V1. If an
external API needs to expose mutable state, it should provide exported getter or
setter functions.

The source declaration being projected must be `public`. Re-exporting an
already `export` declaration is not allowed: once a declaration is part of a
different external ABI, wrap it explicitly if a new external surface is needed.

The same source declaration may not have more than one export projection in the
same artifact.

### Basic Forms

```camp
export PixelFormat;
export PixelFormat as ImagePixelFormat;

export convertPixel;
export convertPixel as image_convert_pixel;

export Allocator as SuperdbAllocator;
```

### Projection Grammar Sketch

Projection declarations deliberately look like declarations without full type
signatures:

```text
export QualifiedName [as ExternalName] ;
export QualifiedTypeName [member-block] [interface-list] [as ExternalName] ;
```

where:

```text
member-block   := "{" [member-projection ("," member-projection)*] "}"
interface-list := ":" InterfaceName ("," InterfaceName)*
member-projection := MemberName ["as" ExternalMemberName]
```

This sketch is descriptive, not a final parser grammar. The important ordering
rule is that the type rename comes at the end, after the member block and
interface list.

### Type Projection Forms

```camp
export Container;
export Container as px_container;

export Container {};
export Container {} as px_container;

export Container
{
	Container,
	~Container,
	getValue,
	setValue,
	getDefaultCapacity,
	DEFAULT_CAPACITY
};

export Container
{
	Container as make,
	~Container as unmake,
	getValue as value,
	setValue as put_value,
	getDefaultCapacity as get_default_capacity,
	DEFAULT_CAPACITY
} as px_container;
```

The default `export T;` exports the type and all public exportable in-scope
declared members. `export T {};` exports the type with no in-scope declared
callable or static member surface.

For generic types, the generic argument list is omitted:

```camp
export List;
```

`export List<T>;` and `export List<int>;` are invalid. A friendly diagnostic
should explain that export projections name the generic type definition, not a
constructed type.

### Member Lists

Names in a type projection member list refer to members declared inside that
type body. They do not include inherited members, out-of-scope static members,
or generated helper declarations unless the projection rule explicitly says so.

Constructors and destructors are named the same way they are written in the
type:

```camp
export Window
{
	Window,
	~Window
};
```

Member lists do not repeat `static`, `inline`, `extern`, callspecs, or types.
They are selecting source declarations, not redeclaring signatures.

If a type has overloaded methods, the member list must use the full unique
declared callable name that the compiler already uses to disambiguate overloads,
such as `writeLineString` or `writeLineInt`.

### Out-Of-Scope Static Members

Out-of-scope static type members are projected separately:

```camp
export int.MAX;
export int.MIN as minimum;
export Container.getDefaultCapacity;
export Container.getDefaultCapacity as default_capacity;
```

Projecting `T.member` does not by itself project `T`. If the member signature
depends on the owner type or any other named type, those dependencies must be
projected separately under the closed exported signature rule.

### Interfaces In The External Shape

Implemented interfaces are opt-in in the projection:

```camp
export Container: IReadable, IWritable;
export Container { getValue, setValue }: IReadable as px_container;
```

Listed interfaces must be implemented by the source type and must themselves be
projected in the same artifact. Interfaces that are implemented internally but
not listed remain hidden from the external API. The generated external API will
include only the interface accessors/conversions for the listed interfaces.

For structs, V1 still does not export implemented interfaces unless and until
the compiler's struct interface ABI rules are expanded. If a struct interface
projection is attempted before that support exists, the compiler should report
a clear diagnostic.

### Base Classes

Base-class relationships are implicit in the export view.

If both `Base` and `Derived` are projected, the exported API shows that
relationship. If only `Derived` is projected, the exported API hides the base
relationship and the external declaration appears to have no base type.

Base classes are not listed in export declarations. If a user writes a base
class in an interface relationship list or member list, the compiler should
report a friendly error:

```text
Base class relationships are exported implicitly when both types are exported; export 'Base' separately or omit it here.
```

### Structs, Classes, And Fields

Projecting a struct always projects all of its instance fields, because the
struct layout is its external shape. The type of every projected field must also
be projected explicitly.

Projecting a class never projects instance fields. Class storage remains opaque
across the external boundary.

Static functions and inline/static constants are selected by the member list.
Mutable static fields are not exportable in V1.

### Closed Exported Signature Rule

Every projected declaration must have a closed external signature.

That means every named type appearing in:

- a projected function's return type or parameters;
- a projected method's receiver, return type, parameters, thrown slots, and
  hidden ABI-significant slots such as `vtableof`;
- a projected struct's fields;
- a projected enum's underlying type when named;
- a projected newtype's underlying type or callable signature;
- a projected interface's slot signatures;
- a projected type relationship selected for export;

must also have an export projection in the same artifact, unless it is a
primitive or other built-in external type.

The compiler should not silently leak a dependency's original name into an
external API. It should report diagnostics that name the missing dependency and
the declaration that exposed it, for example:

```text
Export projection 'Superdb.open' exposes public type 'Std::Allocator'; export it explicitly, for example 'export Std::Allocator as SuperdbAllocator;'.
```

The LSP should later provide code actions to add missing projections. That LSP
work is useful but not required for the compiler feature to be correct.

### Projection Names And Symbols

If a type is renamed, projected member symbols are based on the projected type
name:

```camp
export Container { getValue } as px_container;
```

The external member symbol becomes `px_container_getValue`, unless the member is
renamed:

```camp
export Container { getValue as value } as px_container;
```

The external member symbol becomes `px_container_value`.

`@symbol` on the original declaration remains the internal ABI symbol for the
original declaration. A projection rename controls the external symbol exported
by the current artifact. If a projection must call through to an internal static
dependency function with a different symbol, the compiler may emit a forwarding
wrapper owned by the current artifact.

Projection declarations should also support explicit symbol overrides in a
future extension, but that is not required for V1.

### Direct `export` Declarations Versus Projections

Direct `export` visibility remains valid for declarations that are intentionally
owned by the current external ABI:

```camp
export int main(string[] args) { ... }
export extern int platform_entry();
```

Projection syntax is used when a `public` declaration is being exposed as part
of the current artifact's external API surface:

```camp
public int addOne(int value) => value + 1;
export addOne as sample_add_one;
```

The parser can distinguish these forms because a direct export declaration has
the ordinary declaration shape after `export`, while a projection has only a
qualified declaration name, optional member/interface selectors, and an optional
`as` name.

Direct `export` declarations will probably remain common when implementing a
shared library from scratch because they avoid redeclaring a simple API surface
as projections. Projection syntax exists for selective re-export, renaming, and
shaping public declarations from the current artifact or static dependencies.

### Forwarders

When projecting a function or method from a statically linked dependency, the
current artifact owns the external ABI. The compiler may need to generate a
small forwarding function with the projected external symbol that calls the
public source declaration.

Forwarders are expected for:

- top-level functions projected under a new name;
- member functions projected under a type rename or member rename;
- selected methods from static dependencies where the source symbol should not
  become part of the external ABI.

Forwarders are not expected for:

- type-only projections;
- enum constants;
- inline constants that can be emitted directly into API metadata/header source;
- declarations already defined by the root module with matching exported symbol
  and no rename.

Forwarders must preserve callspecs, expanded parameters, `within` parameters,
`thrown` parameters, async lowering shape, and target ABI rules.

## API, Header, And Metadata Output

### Camp API Headers

The generated Camp API header for a shared library should describe the exported
projection surface, not the internal implementation surface.

Consequences:

- unprojected `public` declarations do not appear;
- projected names and namespaces appear instead of original internal names;
- classes are emitted as `extern` external types as they are today;
- hidden base classes are omitted;
- selected interfaces appear only when listed in the projection;
- struct fields always appear for projected structs;
- member lists control which callable/static member declarations appear;
- dependency declarations are included only if they are projected.

### C Headers

Generated C headers should follow the same external projection surface as Camp
API headers. They should not include the entire standard library or all public
static dependencies.

### Metadata

Metadata modes need clearer definitions:

- `export`: the projected external API view.
- `public`: the final artifact's public source view, including declarations
  visible across static modules, but not private/internal declarations.
- `all`: the full compiler model view, subject to existing generated/private
  filtering rules.
- `none`: unchanged.

Export metadata must match the generated Camp API header and C header. If
virtuality, base relationships, names, interface relationships, or members are
hidden or transformed in the exported API, export metadata should reflect that
exported view.

Public metadata is allowed to include internal artifact names and public static
dependency declarations, because it is a Camp-aware artifact-internal view.

### Wrapper Generators

Future C#, JavaScript/TypeScript, Swift, Rust, and other wrapper generators
should consume the export projection surface by default. A wrapper generator for
testing or artifact-internal tooling may choose the public view, but that should
be explicit.

## Standard Library Migration

The standard library should no longer be broadly `export`.

Most `Std` declarations should become `public`, so they are available to Camp
projects and statically linked modules without leaking into external library
headers. True externally exported entry points or symbols may remain `export`
only when the standard library itself is being built as an external library
surface.

Libraries that need standard-library types in their external API must project
those types deliberately:

```camp
export Std::Allocator as SuperdbAllocator;
export Std::IoError as SuperdbIoError;
```

Because `Allocator` will become `public`, any module that exports a
`within allocator` method will need to re-export `Allocator` or otherwise hide
that allocator parameter behind an exported wrapper.

This migration should be staged after the compiler understands both `internal`
and the new `public` semantics, but before the final projection syntax is used
heavily in real libraries.

## Worked Examples

### Shared Library That Uses Std Internally

Source:

```camp
namespace Image;

using Std;

public enum PixelFormat
{
	RGBA8,
	BGRA8
}

public struct ImageInfo
{
	nuint width;
	nuint height;
	PixelFormat format;
}

public ImageInfo inspect(const byte[] data)
{
	...
}

export PixelFormat;
export ImageInfo;
export inspect as image_inspect;
```

Inside the artifact, the implementation can use all public `Std` declarations.
Outside the artifact, only `PixelFormat`, `ImageInfo`, and `image_inspect` are
visible. `Std::List`, `Std::Allocator`, and unrelated stdlib functions do not
appear in the generated C header, Camp API header, or export metadata.

### Re-Exporting A Dependency Type Under A Local Name

Source:

```camp
namespace Superdb;

using Std;

public Db* open(string path, within allocator, thrown DbError)
{
	...
}

export Std::Allocator as SuperdbAllocator;
export Db;
export DbError;
export open as superdb_open;
```

The internal function still speaks in terms of `Std::Allocator`. The external
API exposes that projected type as `SuperdbAllocator`. A generated forwarding
wrapper may be needed for `superdb_open` if the source function's internal ABI
symbol is not already the external symbol.

### Static Dependency With A Narrow External Surface

The root library statically links a pixel conversion package:

```camp
namespace Image;

using PixelConvert;

export PixelConvert::PixelFormat as ImagePixelFormat;
export convertToRgba as image_convert_to_rgba;
```

Only the projected enum and function become part of the root shared library's
external API. Other public helpers from `PixelConvert` remain available to the
root library during compilation and linking but are not exported.

## Compiler Areas Affected

### Parser And CST/AST

Affected work:

- add `internal` as a declaration visibility keyword;
- remove `export as` and parse `namespace`;
- reintroduce `public` with the new artifact visibility semantic;
- parse export projection declarations, including type member blocks, optional
  interface lists, and optional `as` renames;
- preserve source ranges for projection diagnostics.

Risks:

- `export` currently starts both export visibility and `export as` namespace
  parsing;
- type-qualified member syntax already exists for out-of-scope static members,
  so projection parsing must not conflict with normal declarations.

### Bindable Model

Affected work:

- replace nullable `Export`/`Public` state with a single declaration visibility
  enum plus external projection records;
- add projection model nodes that reference source declarations;
- track projected namespace/name/symbol separately from source name/symbol;
- preserve enough provenance to serialize projected API headers and metadata.

Risks:

- many passes currently test `definition.Export is not null` or
  `definition.Public is not null`;
- generated declarations need to inherit or derive visibility differently in
  internal, public, and export views.

### Name Lookup And Imports

Affected work:

- make `using` a real file-local lookup gate for namespace imports, selected
  imports, and alias imports;
- preserve implicit `using Std;` while suppressing it for explicit root `Std`
  imports in the same file;
- make `internal` behave like old `public` inside one project;
- make `public` visible across static project/package references;
- ensure shared dependencies expose only export projections;
- ensure private declarations remain file-local;
- update `using`/namespace handling to use `namespace` declarations.

Risks:

- project-reference and package-source handling already has multiple API/header
  paths;
- stale generated API headers may mask mistakes if tests do not clean outputs.

### Export Closure Analysis

Affected work:

- replace "exported declaration exposes non-exported type" with projection
  closure validation;
- validate missing type projections with source-ranged diagnostics;
- validate duplicate projections;
- validate member selection and interface selection;
- validate hidden base relationship rules.

Risks:

- struct fields, callable newtypes, `vtableof`, `thrown`, `constof`, async
  resumers, and interface accessor helpers all contribute signature-dependent
  types;
- diagnostics must point at the projection/member causing the leak, not at the
  eventual emitted header.

### C Emitter

Affected work:

- separate internal/public declarations from exported C header declarations;
- generate projection forwarders when needed;
- emit projected type/member names in the external C header;
- ensure hidden base classes and hidden interfaces do not appear externally;
- keep private headers capable of compiling the full internal artifact.

Risks:

- existing C header generation interleaves public/project API concerns;
- shared-library import/export decoration may need to apply to projection
  forwarders rather than source declarations;
- generated interface vtable accessors have different public/export shapes.

### Camp API Serializer

Affected work:

- serialize `namespace`, not `export as`;
- serialize external projection view for shared-library API files;
- serialize public view for static project/package API files when required;
- preserve projection names, member selections, hidden relationships, and
  extern class rules.

Risks:

- current serializer decides API inclusion by `definition.Export`;
- exported view may need shallow projected clone nodes rather than mutating
  source declarations.

### Metadata Emitter

Affected work:

- make export metadata match the projection surface;
- make public metadata match artifact-public surface;
- include enough projection metadata for tools to understand source declaration
  identity versus exported name;
- keep doc comments and attributes attached to projected declarations.

Risks:

- metadata has already accumulated special cases for lowered/generated types;
- wrappers and LSP may assume current `visibility: "export"` means original
  source declaration was marked `export`.

### Project Loader, Package Restore, And Build Caches

Affected work:

- static dependency builds must provide a public Camp API surface;
- shared dependency builds must provide an export projection API surface;
- cache keys must continue to separate artifact, target, variants, profile, and
  dependency link kind;
- standard library cache must be regenerated after the std visibility migration.

Risks:

- package artifacts are already sensitive to link kind and target variants;
- a dependency may be consumed both statically and shared in different builds.

### LSP

Affected work:

- recognize `namespace`, `internal`, new `public`, and projection declarations;
- completion should include public declarations from static dependencies but
  only exported projections from shared dependencies;
- hover should show source visibility and projection information clearly;
- diagnostics should surface missing export projections with useful ranges.

Risks:

- LSP currently reads generated API headers and package sources in different
  modes;
- projection code actions are desirable but can come after compiler correctness.

### Documentation

The spec, LLM guide, metadata supplement, grammar documentation, syntax
highlighting, and examples will need a coordinated overhaul.

The old docs currently teach `public` as project visibility and `export as` as
the namespace mechanism. After this proposal lands, docs must distinguish:

- file-local declarations;
- `internal` declarations;
- `public` artifact declarations;
- `export` external declarations;
- export projections;
- generated external API surfaces versus internal Camp surfaces;
- how standard library declarations are visible without being re-exported.

Because this is a broad language model change, docs should be updated in a
late implementation stage once the compiler behavior is stable, then audited
against real stdlib/API output.

## Staged Implementation Plan

Each stage should end with a full test suite pass and a commit. During the
stage, targeted tests should be used aggressively. Existing unrelated compiler
bugs found during the work should be logged in `Tests/OutstandingBugs.md` unless
they block the stage.

The stages are intentionally ordered so that test updates are concentrated
around one semantic change at a time. The first two stages are hardening and
spelling changes that should land before the visibility model itself starts
moving.

### ~~Stage 0 - Harden `using` Source Lookup~~

Goal: make compiler behavior match the documented `using` semantics before
export projections depend on file-local lookup.

Tasks:

- Add a centralized import/name-resolution service that knows the current file,
  its namespace declaration, and its `using` declarations.
- Enforce that unqualified references can see:
  - declarations in the same file/namespace according to existing visibility;
  - declarations imported by `using Namespace;`;
  - declarations explicitly selected by `using Namespace { A, B };`;
  - local aliases declared with `alias`, as today.
- Enforce that selected imports expose only selected names.
- Enforce that namespace aliases expose declarations only through the alias:

  ```camp
  using Native::Windows as Win;
  Win::CreateWindow(...);      // ok
  Native::Windows::CreateWindow(...); // error unless separately imported
  ```

- Support qualified lookup through imported namespaces and aliases for types,
  functions, globals, enum values, static type members, out-of-scope static
  type members, aliases, callspec aliases, and typespec aliases.
- Harden qualified names in source grammar and binding so `Namespace::Type`
  works in local declarations, parameter declarations, return types, casts,
  generic arguments, `typenameof`, `vtableof`, and other type positions.
- Harden qualified expression lookup so `Namespace::function()`,
  `Namespace::value`, `Namespace::Type.staticMember`, and
  `Alias::Type.staticMember` resolve consistently.
- Update hidden-symbol diagnostics so a missing import is reported as an import
  problem rather than a misleading visibility/export problem.
- Preserve the current implicit `using Std;` behavior when stdlib is enabled,
  but suppress the implicit root `Std` import for a file that explicitly writes
  `using Std;`, `using Std { ... };`, or `using Std as S;`.
- Ensure imports of subnamespaces such as `using Std::Math;` do not suppress
  the implicit root `Std` import.

Completion criteria:

- ~~Tests prove unimported namespace symbols are inaccessible.~~
- ~~Tests prove `using Namespace as Alias;` works and the original namespace is
  not available through that import.~~
- ~~Tests prove selected imports hide unselected names.~~
- ~~Tests prove namespace-qualified type names work in signatures, locals, casts,
  generic arguments, and type intrinsics.~~
- ~~Tests prove namespace-qualified functions and static type members work in
  expression/call positions.~~
- ~~Tests prove explicit root `Std` selected/aliased imports suppress implicit
  `using Std;`.~~
- ~~Tests prove ordinary files with stdlib enabled still receive implicit
  `using Std;`.~~
- ~~Full suite passes.~~

Primary risk:

- Lookup is currently scattered across type binding, method-body analysis,
  alias resolution, static member lookup, metadata symbol resolution, and LSP.
  The safest implementation is a shared resolver that can be adopted by those
  paths incrementally inside this stage.

### ~~Stage 1 - Replace `export as` With `namespace`~~

Goal: remove the overloaded namespace spelling before changing visibility
semantics.

Tasks:

- Make `namespace` a reserved word.
- Parse `namespace A::B;` in the file prelude.
- Replace `Module.ExportAs` with `Module.Namespace` or equivalent.
- Update source, tests, generated Camp API serializers, metadata, LSP, grammar,
  and syntax highlighting to use `namespace`.
- Remove `export as` support and add a migration diagnostic:

  ```text
  Use 'namespace X;' instead of 'export as X;'.
  ```

Completion criteria:

- ~~No source or golden file uses `export as` except diagnostics tests.~~
- ~~Generated API files use `namespace`.~~
- ~~LSP understands namespace declarations.~~
- ~~`namespace` cannot be used as an ordinary identifier.~~
- ~~Full suite passes.~~

Primary risk:

- `export as` appears in nearly every API/metadata golden. This stage is
  intentionally isolated so that the churn is mostly textual.

### ~~Stage 2 - Rename Old `public` To `internal`~~

Goal: introduce `internal` as the exact old meaning of `public`, and remove
source use of old `public`.

Tasks:

- Add `internal` as a declaration visibility keyword.
- Rename the internal compiler concept currently represented by `Public` to
  `Internal`, without adding the new `public` semantic yet.
- Update all source, standard library, tests, and goldens that used old
  `public` to use `internal`.
- Make the old `public` spelling invalid during this stage, with a diagnostic
  such as:

  ```text
  'public' now means artifact visibility and is not enabled in this migration stage; use 'internal' for current-project visibility.
  ```

  The implementation may keep parser scaffolding for the next stage, but no
  existing test source should rely on `public`.
- Keep metadata mode name `public` temporarily as a command-line output mode if
  required for compatibility, but document internally that it means the
  "non-private API view" until Stage 4 redefines it precisely.

Completion criteria:

- ~~Old source-level `public` is gone from compiler tests and stdlib except in
  negative tests.~~
- ~~`internal` behaves exactly like old `public` for lookup, API import, and
  metadata filtering.~~
- ~~Diagnostics for old `public` include line/column.~~
- ~~Full suite passes.~~

Primary risk:

- `public` appears in many test/golden files. This should be mostly mechanical,
  but metadata strings and command-line mode names must not be confused with
  source visibility syntax.

### ~~Stage 3 - Reintroduce `public` As Artifact Visibility~~

Goal: add the new `public` semantic without changing stdlib export policy yet.

Tasks:

- Add a true visibility enum, for example `Private`, `Internal`, `Public`,
  `Export`.
- Replace direct nullable visibility fields with the enum, preserving source
  ranges for diagnostics.
- Make `public` declarations visible across static project/package references.
- Keep `internal` visible only within the current project/module.
- Ensure shared dependency imports expose only `export`, not `public`.
- Require `export main` for executable entry points that must be externally
  visible.
- Add diagnostics for attempts to use `internal` declarations across static
  module boundaries.

Completion criteria:

- ~~Dense static-reference tests prove `public` crosses a static project
  reference and `internal` does not.~~
- ~~Shared-reference tests prove `public` does not cross a shared-library
  boundary.~~
- ~~Existing exported declarations still behave as before.~~
- ~~Full suite passes.~~

Primary risk:

- The compiler currently has project API headers that include both old
  `public` and `export`. Static and shared dependency consumption need to be
  separated carefully.

### ~~Stage 4 - Split API And Metadata Views~~

Goal: make the compiler able to produce and consume distinct internal/public
and export views before stdlib migration.

Tasks:

- Define API surface kinds explicitly:
  - project/internal analysis view;
  - static dependency public Camp API view;
  - shared dependency export Camp API view;
  - external C/header/wrapper export view.
- Update Camp API serialization to emit the correct view for static versus
  shared dependency consumption.
- Update C header generation to use only `export`.
- Update metadata filtering:
  - `export` means external export view;
  - `public` means artifact-public view;
  - `all` includes private/internal/public/export according to current all-mode
    rules.
- Ensure package and project-reference cache paths already separated by link
  kind continue to receive the correct API files.

Completion criteria:

- ~~Static dependency test: root project consumes a dependency's `public`
  declaration.~~
- ~~Shared dependency test: root project cannot consume a dependency's `public`
  declaration unless it is exported.~~
- ~~Export metadata and generated external C/Camp API headers exclude public-only
  declarations.~~
- ~~Public metadata includes public declarations.~~
- ~~Full suite passes.~~

Primary risk:

- This stage touches compiler driver, API serializer, metadata, and C emitter
  at once. It should not yet change stdlib visibility, so failures should point
  to view selection rather than source churn.

### ~~Stage 5 - Migrate The Standard Library To `public`~~

Goal: stop the standard library from being automatically exported by libraries
that use it.

Tasks:

- Change broad stdlib declarations from `export` to `public` where they are
  intended for Camp use but not necessarily external ABI publication.
- Keep genuinely external stdlib symbols exported only where the stdlib itself
  must publish them.
- Update standard library API and metadata goldens.
- Update tests that assumed std declarations appear in another library's export
  metadata/header.
- Add regression coverage proving a simple shared library using `Std` does not
  export the whole standard library.
- Add explicit `Allocator` projections to tests or samples that export
  `within allocator` signatures.

Completion criteria:

- ~~User libraries can still use std declarations normally.~~
- ~~Exported library headers no longer include unrelated std declarations.~~
- ~~Existing std run/compile tests pass.~~
- ~~Full suite passes.~~

Primary risk:

- Many current tests rely on generated std API files. This stage should be kept
  focused on std visibility and expected output changes, not projection syntax.

### ~~Stage 6 - Add Basic Export Projection Declarations~~

Goal: support top-level projection of public types, aliases, enums, inline
constants, and non-this functions.

Tasks:

- Add parser/model support for:

  ```camp
  export T;
  export T as X;
  export F;
  export F as Y;
  ```

- Resolve projection targets through the current file's `using`/alias lookup
  rules and then require that the resolved target is `public`.
- Reject private/internal/export targets with clear diagnostics.
- Forbid duplicate projections of the same declaration in one artifact.
- Implement closed signature validation for top-level functions and projected
  types without member blocks.
- Emit projected Camp API, C header, and export metadata views.
- Generate forwarding functions when a projected function needs a new external
  symbol.

Completion criteria:

- ~~Tests cover type, alias, enum, inline constant, and top-level function
  projections with and without renames.~~
- ~~Tests cover duplicate projections and missing signature dependency
  projections.~~
- ~~Export metadata uses projected names.~~
- ~~Full suite passes.~~

Primary risk:

- Forwarders need to preserve ABI details. Keep the first projection tests dense
  but limited to representative signatures before expanding to members.

### ~~Stage 7 - Add Type Member Projection Blocks~~

Goal: support projection of selected in-scope type members.

Tasks:

- Parse and bind:

  ```camp
  export T {};
  export T { member, member as M } as X;
  ```

- Select constructors, destructors, methods, static methods, and inline/static
  constants declared inside the type body.
- Exclude inherited members and out-of-scope static extensions from member
  blocks.
- Reject mutable static fields.
- Apply type renames and member renames to projected external symbols.
- Generate member forwarders as needed.
- Validate closed signatures for every selected member.

Completion criteria:

- ~~Tests cover all-members, no-members, selected-members, member renames, type
  renames, constructors, destructors, static methods, inline constants, and
  overload disambiguation.~~
- ~~Tests prove hidden class fields do not leak and struct fields always do.~~
- ~~Full suite passes.~~

Primary risk:

- Lifecycle methods and generated helpers already have special API-header
  behavior. Member projection must select the source-level declaration while
  emitting the correct external extern/create/destroy shape.

### ~~Stage 8 - Add Interface Projection Rules~~

Goal: allow projected class types to expose selected implemented interfaces.

Tasks:

- Parse and bind interface lists:

  ```camp
  export T: IFoo, IBar;
  export T { member }: IFoo as X;
  ```

- Validate that listed interfaces are implemented by the type.
- Validate that listed interfaces are also projected.
- Emit external interface accessors/conversions only for listed interfaces.
- Hide unlisted implemented interfaces in export API and metadata.
- Preserve the existing "struct implemented interfaces are not exported in V1"
  rule unless struct interface export support is explicitly added.

Completion criteria:

- ~~Tests cover projected and hidden class interface implementations.~~
- ~~Tests cover missing interface projections and unimplemented listed
  interfaces.~~
- ~~Generated API and metadata show only listed interfaces.~~
- ~~Full suite passes.~~

Primary risk:

- Interface implementation already has direct vtable and fixup-vtable rules.
  Projection must not mix up the internal `vtableof` symbols with exported
  interface accessors.

### ~~Stage 9 - Base Relationship And Dependency Closure Hardening~~

Goal: finish the edge cases around projected type shapes.

Tasks:

- Emit base relationships only when both base and derived types are projected.
- Add diagnostics when a user tries to list a base class in a projection.
- Harden closed export dependency detection for nested generic arguments,
  callable newtypes, `constof`, `scoped`, `vtableof`, `thrown`, iterators, async
  callables, and out-of-scope members.
- Ensure projection closure diagnostics point at the projection/member token
  causing the leak.

Completion criteria:

- ~~Tests cover hidden base, visible base, invalid explicit base listing, and
  nested signature dependencies.~~
- ~~No projected API surface contains an unprojected named type.~~
- ~~Full suite passes.~~

Primary risk:

- This stage is diagnostic-heavy and can easily become broad. Prefer a small
  number of dense tests with many dependency shapes.

### ~~Stage 10 - Documentation, LSP, And Final Audit~~

Goal: finish the language model and tool support after compiler behavior is
stable.

Tasks:

- Update the spec with the new visibility model, `namespace`, export projection
  syntax, dependency closure rules, interface projection rules, base-class
  projection behavior, and stdlib visibility guidance.
- Update the LLM coding guide with recommended usage:
  - use `internal` for project-only helpers;
  - use `public` for declarations meant to be shared across static modules;
  - use export projections for external API design;
  - use `export` directly only for true external entry points and declarations
    intentionally owned by the current ABI.
- Update grammar docs and syntax highlighting.
- Update metadata supplement to define export metadata as projected external
  view.
- Update LSP completion, hover, symbols, and diagnostics for the new visibility
  model and projection syntax.
- Re-read this proposal and verify every semantic rule has tests or an explicit
  deferred bug.

Completion criteria:

- ~~Documentation is internally consistent and uses `namespace`, `internal`,
  `public`, and export projections correctly.~~
- ~~LSP tests cover namespace and projection declarations at a smoke-test level.~~
- ~~No `export as` references remain outside archived historical proposals or
  explicit migration diagnostics.~~
- ~~Full suite passes on all required platforms.~~

Primary risk:

- Documentation and extras touch many files. Do this last to avoid repeated
  churn while compiler behavior is still moving.

## Test Strategy

Tests should be dense rather than numerous. This feature is broad enough that
large numbers of tiny tests will slow development unnecessarily.

Recommended high-value test groups:

- import lookup matrix:
  - no import, plain import, selected import, alias import, explicit
    qualification, implicit `Std`, suppressed implicit `Std`;
- visibility lookup matrix:
  - private, internal, public, export in same file, same project, static
    dependency, shared dependency;
- API/header matrix:
  - static dependency public API versus shared dependency export API;
- metadata matrix:
  - export/public/all views for the same source file;
- stdlib leakage regression:
  - shared library uses `Std` but generated external header/metadata does not
    expose unrelated `Std` declarations;
- basic projection matrix:
  - type/function/alias/enum/inline constant, with and without rename;
- member projection matrix:
  - all members, no members, selected members, renamed members, constructor,
    destructor, static method, inline constant;
- shape closure matrix:
  - missing projected parameter type, return type, struct field type,
    interface type, callable newtype type, and nested generic type;
- class/interface/base matrix:
  - hidden base, visible base, projected interface, hidden interface;
- diagnostics:
  - duplicate projection, non-public target, already-export target, generic
    argument list on projection, invalid base listing, unprojected dependency.

Full-suite validation should happen after each stage. Cross-platform validation
is especially important once C header emission and forwarding wrappers are
introduced.

## Resolved Design Details

These decisions are part of this proposal.

1. `export T;` does not include public out-of-scope static members whose owner
   is `T`. Out-of-scope static members are projected explicitly with
   `export T.member;`.

2. Projection declarations are allowed in any file in the module. The
   projection's namespace is the namespace of the file containing the projection
   declaration.

3. Projection member blocks select only `public` members. Export projection is
   an externalization of the artifact-public surface, not a way to reach through
   project-private members.

4. `export T;` exports all public in-scope exportable members. Private/internal
   members must first be made public if they are intended for external
   projection.

5. Direct `export` visibility remains valid on normal declarations. It is
   especially useful when implementing a shared library directly, because it
   avoids redeclaring every exported declaration as a projection.

## Non-Goals

- No automatic re-export of all dependency APIs.
- No re-export of mutable globals or mutable static fields in V1.
- No arbitrary mini-language for export selection beyond the member/interface
  projection syntax described above.
- No LSP code actions required for V1 compiler correctness.
- No package manifest policy changes beyond producing/consuming the correct
  public/export API surface for the selected dependency link kind.
- No compatibility alias for `export as` once `namespace` lands.
