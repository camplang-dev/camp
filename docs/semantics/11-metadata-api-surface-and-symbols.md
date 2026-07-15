# Metadata, API Surface, And Symbols

This supplement describes the source-level API and metadata model. The command
line metadata format is summarized in
[Metadata JSON](../compiler/06-metadata-json.md); this document explains the
compiler invariants behind that output.

The central rule is that metadata and Camp API headers describe the source API,
while C emission describes the lowered ABI. These are related, but they are not
the same document.

## API Header Model

API headers expose source declarations needed by downstream Camp compilations.
They are not lowered C ABI dumps, and they should not expose generated helpers
unless those helpers are part of the source API contract.

An API header should preserve enough information for another Camp compilation
to:

- resolve exported, projected, and artifact-public declarations;
- bind generic parameters and constraints;
- type-check calls, construction, interface dispatch, and `vtableof`;
- understand callable newtypes and async source shape;
- see attributes and documentation that belong to the source API;
- avoid depending on private generated helper symbols.

API headers may include source declarations that lower to multiple ABI
components. For example, an async function remains source-level `async` even
though C emission uses a completion callback. An array parameter remains an
array parameter in the API header even though ABI lowering passes pointer and
length components.

## Metadata View Model

Metadata views are `export`, `public`, and `all`. Export view is API-level.
Public and all views are source-level.

The top-level metadata document records:

- format name;
- version;
- module name/namespace;
- view visibility;
- whether the view is API-level or source-level;
- declarations;
- stubs for referenced declarations not emitted in full.

`export` is the view consumers should use to understand the external native/API
boundary. It applies export projections: projected names, selected members,
projected interface lists, hidden class fields, and base relationships that are
visible only when both sides are exported. `public` is useful for artifact-
internal language tooling and documentation. `all` is useful for compiler
diagnostics, tests, and internal inspection, but still should not pretend
generated lowering helpers are ordinary source declarations.

## Export/Public/All Filtering

Filtering must be consistent across metadata, API headers, and visibility
analysis. Referenced declarations not emitted in full may appear as stubs.

Filtering should consider:

- top-level `export`, `public`, and `internal`;
- export projection declarations and their selected names/members/interfaces;
- type members with member-level visibility;
- source declarations referenced by exported signatures;
- base classes/interfaces needed to interpret a declaration;
- callable newtype targets;
- parameter and type parameter metadata;
- inline constants that are part of the source surface.

Filtering should not leak private generated declarations merely because an
exported declaration lowers through them. Use stubs where a referenced identity
is useful but the full declaration is outside the selected view.

Export filtering is stricter than public filtering. If an exported declaration
or selected projection member mentions a source type, that type must be visible
in the same exported surface as a declaration or projection unless the metadata
serializer can represent it as a valid external primitive or built-in shape. The
compiler should diagnose missing projected dependencies instead of serializing
unnameable internal source types.

## Generated Versus Source Declarations

Metadata is source-level by default. Generated declarations should be omitted
unless they represent exported source API or are necessary stubs.

Generated declarations include:

- expanded array/delegate/params helper components;
- interface thunks and temporary carrier structs;
- virtual dispatch thunks and vtable helper functions;
- async frames, continuations, and completion helpers;
- lambda context structs and generated lambda functions;
- postponed-call context types;
- destructor/delete/create/init helpers not visible in source;
- materialized generic return storage helpers.

Some generated declarations still have ABI symbols and appear in emitted C. That
does not make them metadata declarations. If tooling needs ABI-level inspection,
it should use dumps or C emission artifacts, not source metadata.

## Symbol Names

Source names and ABI symbols are separate. `@symbol` overrides emitted native
symbols but should not change source lookup.

Compiler code should keep these name concepts distinct:

- **source name:** what the programmer writes and what lookup sees;
- **callable name:** the overload/ascription-aware function identity;
- **invoker name:** the name used for call syntax and overload families;
- **symbol name:** the native symbol, possibly affected by `@symbol`;
- **full callable symbol:** type-qualified callable symbol where generated;
- **C identifier:** an emitted identifier safe for the selected C target;
- **API name:** source-level name exposed to Camp consumers;
- **metadata name:** stable display/identity name in metadata.

Do not use `@symbol` to affect source lookup, metadata IDs, overload
resolution, interface conformance, or callable ascription. It affects emitted
native symbol identity.

## Symbol Collisions

The analyzer validates symbol collisions separately from source-name
collisions. A source declaration, generated component, or expanded field may
emit a C identifier that collides with another emitted identifier even when the
source names are legal.

Collision checks must include:

- top-level symbols;
- type member symbols;
- generated expanded-form components;
- inline constants and static fields;
- interface vtable variables and helper thunks;
- virtual dispatch helpers;
- `@symbol` overrides;
- C reserved words and target reserved identifiers.

When a collision is caused by a generated component, the diagnostic should name
the source declaration that owns the component.

## Metadata IDs

Metadata IDs are stable opaque strings within a metadata document. They are
derived from declaration kind, source name, and containment enough for stable
consumer references.

IDs are not ABI symbols and not source syntax. They should be stable across
metadata serialization for the same source API, but consumers should treat them
as opaque. Compiler changes should not make tools parse IDs for semantic
meaning that is already available in structured fields.

Common ID paths include top-level declaration IDs and child IDs such as:

- type parameters;
- fields;
- functions;
- parameters;
- enum values;
- params components.

When a generated struct represents a source interface for metadata purposes,
the serializer should use the source interface identity rather than leaking the
generated carrier identity.

## Doc Comment Translation

Doc comments lower to metadata attributes before emission. Child targets attach
to parameter, receiver, type parameter, field, method, or enum-value metadata.

The doc-comment translator should:

- preserve source text in a normalized form;
- attach block/line comments to the intended declaration;
- resolve child-target comments to child metadata records;
- report unresolved child targets;
- represent known documentation attributes structurally;
- reject unknown or malformed doc-comment attributes with source ranges.

Documentation metadata is source API. It should not attach to generated
lowering helpers except where the generated declaration represents the source
declaration's metadata identity.

## Attributes

Attributes appear in metadata when they are source attributes visible in the
selected view. Important source attributes include:

- `@symbol`;
- async attributes such as `@awaitwith` and `@noawait`;
- documentation attributes translated from doc comments;
- lifecycle, extern, callable, and interface markers represented by structured
  fields where the serializer has first-class support.

Compiler writers should prefer structured metadata fields over opaque attribute
strings when tools need to understand the feature. Opaque attributes are useful
for source preservation, but core language semantics should be represented
directly.

### `@symbol`

`@symbol("Name")` overrides the emitted native symbol for declarations where a
native symbol is meaningful. Declaration analysis accepts it on:

- enum declarations;
- enum values;
- class, struct, interface, and newtype declarations;
- global variables, including global inline constants;
- static fields, including static inline constants;
- functions and methods.

It is rejected on aliases, parameters, generic parameters, and instance fields.
The attribute requires exactly one string literal. The string must be a valid
identifier and must not be a reserved Camp word or reserved C word.

For a type declaration, the override is the type's effective native symbol.
Default emitted names for ABI-visible generated helpers, interface/vtable
helpers, lifecycle helpers, virtual helpers, and static members should use that
effective type symbol as their prefix. A member-level `@symbol` overrides the
full emitted member symbol and takes precedence over the containing type's
default prefix.

`@symbol` does not affect source lookup, metadata IDs, overload resolution,
interface conformance, callable ascription, or documentation targets. Metadata
and Camp API output should preserve the source name and the `@symbol`
attribute, and metadata should include the symbol name when it differs.

### `symbolof(...)`

`symbolof(...)` is valid only in metadata attribute arguments. It resolves a
source declaration reference to the declaration's emitted symbol name. The
resolver should search the source/metadata declaration graph, including child
declarations where the attribute surface permits them, and diagnose unresolved
references at the `symbolof` expression.

`symbolof` is not a runtime expression and must not be accepted in ordinary
function bodies, constant expressions, or emitted C expressions.

### `@notsupported`

`@notsupported` marks a function or method as unavailable for source calls. It
is not valid on constructors, destructors, aliases, types, fields, parameters,
or generic parameters. It accepts at most one positional string reason and does
not accept named arguments.

Call analysis should diagnose calls to unsupported functions unless the current
source context is itself an accepted unsupported/declaration-only surface.
Metadata should preserve the unsupported marker and reason so tools can explain
availability without trying the call.

### Lifecycle And Async Attributes

Attributes such as `@awaitwith`, `@noawait`, and generated lifecycle markers
should be represented with first-class metadata fields when possible. Internal
generation aids such as allocator-aware create helpers should not be treated as
general source attributes unless the language surface explicitly exposes them.

The compiler recognizes `@createWithAllocator` on type declarations during
generated class create-helper construction. When present, the generated create
path carries an allocator parameter even if the constructor surface did not
otherwise declare a `within` parameter. This is a lifecycle generation hook, not
a general metadata attribute for ordinary API authors. Metadata and API output
should not encourage consumers to depend on it unless a future source feature
makes that dependency explicit.

## Property Metadata

Property and indexer information is derived from accessor functions. The
metadata serializer should identify:

- accessor kind: getter or setter;
- property name for named properties;
- indexer marker for nameless `get`/`set`;
- index parameter names;
- setter value parameter name.

For async accessors, the metadata property name is derived from the source
accessor name after removing the accessor prefix and the trailing `Async`
suffix where the compiler recognizes that suffix. For setters whose value
parameter expands into multiple ABI components, metadata records the source
value parameter name, not the name of an expanded trailing component.

This information belongs to the accessor function metadata. Do not synthesize
source field declarations for properties, and do not omit the underlying
function declaration data needed to type-check or document the API.

## Stubs

Stubs represent referenced declarations that are not emitted in full. Consumers
should treat stubs as identity records, not full declaration records.

A stub may include:

- ID;
- kind;
- name;
- symbol when different from source name.

It should not claim to include complete type parameters, fields, functions, or
attributes. If a consumer needs the full declaration, it should request a view
that includes it or compile with the API header/source that owns it.

## Type Object Details

Type objects describe kind, generic parameters, fields, functions, interfaces,
enum values, aliases, and attributes.

The serializer should preserve:

- class/struct/interface/newtype/enum/params kind;
- generic parameters and constraints;
- base classes/interfaces;
- implemented interface metadata;
- fields, including static, fixed, inline, and constant values;
- methods/functions, including constructors/destructors;
- enum values and fixed enum underlying types;
- newtype underlying type and callable-newtype surface;
- source visibility and extern status.

Generated fields such as virtual vtable pointers or stored `vtableof` fields
should be hidden unless the source API explicitly exposes them.

## Function Object Details

Function objects describe return type, parameters, generic parameters, callable
modifiers, and attributes.

The serializer should preserve:

- source result type, including `async` result shape;
- parameter list and parameter modifiers;
- generic parameter list;
- callable type family for callable newtypes;
- call spec and target spec where source-visible;
- `constructor` or `destructor` lifecycle role where present;
- async flag and source async attributes;
- interface implementation metadata;
- callable ascription metadata;
- default values where they are source API.

For async functions, metadata keeps the source async shape and omits generated
completion helpers. For functions with expanded forms, metadata keeps the
source-level parameter and result spelling unless the view explicitly documents
lowered ABI.

## Capability Parameters

Special capability parameters must be structured so tools can distinguish them
from ordinary user parameters:

- `sizeof(T)` lowers to a `nuint`-like ABI value and records target type `T`;
- `typenameof(T)` records the type whose name is carried;
- `vtableof(T: Interface)` records both the target type and interface type and
  lowers to `const Interface*`;
- `within` parameters remain allocation-context parameters, not ordinary data
  values.

Metadata should preserve enough source information for downstream Camp code and
LLM agents to reconstruct valid calls without relying on generated C names.

## Inline Constants

Inline constants and fixed enum values appear as source constants. The metadata
value should preserve the constant kind and value rather than forcing consumers
to parse source text.

Supported inline values include scalar constants, enum values, scalar/pointer
newtype constants, pointer null, function-pointer null, and string-like values
as supported by the compiler. Unsupported inline constant types should diagnose
during analysis, not disappear from metadata.

The constant evaluator should remain intentionally small and deterministic.
Supported integer expressions include literals, references to earlier constants
in the same constant-evaluation graph, casts, parenthesized expressions, and
the unary/binary integer operators implemented by the compiler. Do not treat
ordinary function calls, target preprocessor expressions, or generic
per-instantiation code as inline constant evaluation.

Inline constants are emitted to C as typed macro-style constants when they are
part of the exported/native surface. They also appear in metadata as source
constants. Metadata is the canonical structured view; C spelling is an ABI
artifact.

## Enum Metadata And Symbols

Enums are nominal integer types with fixed values. Metadata should preserve the
enum's underlying type, source enum values, computed constant values, and any
symbol overrides. The compiler's default underlying type is `uint` unless the
source declares another supported integral type.

Exported C headers represent Camp enums as typedef-based integer types plus
named value macros/constants, not as C `enum` declarations. This preserves the
chosen underlying representation and avoids relying on target C enum width
rules.

The enum declaration's emitted symbol is the base symbol for the exported C
typedef. Each enum value receives its own emitted symbol. Without an explicit
value-level `@symbol`, declaration analysis derives value symbols from the enum
symbol plus the source value name. A value-level `@symbol` overrides the entire
emitted value symbol.

Target-typed enum value lookup is a source analysis feature. Metadata should
still record the enum value as a child of the enum, not as a free global
constant merely because C emission uses macro-like names.

## API Versus ABI Inspection

If a tool needs to understand source declarations, use metadata or API headers.
If it needs to understand emitted symbols, generated helper functions, component
expansion, or target C spellings, use lowered dumps, ABI surface inspection, or
C emission artifacts.

Keeping this distinction prevents tools from depending on unstable internal
helpers and prevents source docs from becoming a duplicate ABI reference.

## Diagnostics

Metadata/API diagnostics should identify:

- invalid or unresolved doc-comment targets;
- unknown documentation attributes;
- source declarations that cannot be emitted in the selected view;
- symbol collisions;
- invalid `@symbol` placement;
- unsupported inline constant metadata;
- generated/source identity mismatches;
- referenced declarations that require stubs.

Diagnostics should point to the source declaration, attribute, doc-comment
target, or symbol override that caused the problem.

## Test Surface

Metadata/API changes should cover:

- export/public/all filtering;
- generic self-links;
- doc comments and child targets;
- async source attributes and async callable newtypes;
- `constof` source spelling;
- inline constants and fixed enums;
- callable newtype/value receiver metadata;
- interface implementation markers;
- raw function pointer source shape;
- stubs for referenced declarations;
- namespace and export projection behavior.

## Implementation Anchors

Primary implementation points include:

- `MetadataJsonSerializer.cs` for JSON output, IDs, filtering, stubs, and
  declaration serialization;
- `DocCommentTranslator.cs` for doc-comment lowering;
- `SymbolNameService.cs` and analyzer declaration validation for source/symbol
  naming;
- `AbiSurface.cs` for ABI-oriented inspection distinct from metadata;
- `CCodeEmitter.cs` for emitted symbol and header behavior;
- metadata fixtures under `tests/Metadata` and API/header fixtures under tests.
