# Metadata JSON

Camp metadata JSON is a source-level description of the declarations exposed by
a compilation. It is meant for documentation generators, API browsers, package
tools, editor features, and other consumers that need Camp declarations rather
than lowered C implementation details.

Metadata is not a C ABI dump. It omits most generated helpers, generated state
types, local declarations, function bodies, and inactive conditional branches.

## Emitting Metadata

Metadata is selected with `--metadata`:

```sh
campc build src/library.camp --artifact static --metadata export
campc build src/library.camp --artifact none --metadata public
campc build src/library.camp --artifact none --metadata all
campc build src/tool.camp --metadata none
campc dump metadata src/library.camp --metadata all
```

Static and shared library builds default to `export` metadata. Executable,
C-only, and dump builds default to `none`, except that `campc dump metadata`
uses the `export` view when no explicit metadata view is selected.

Metadata output cannot be combined with non-metadata dump modes or XML output.
The compiler driver also has an API-inspection mode used by tests and tooling;
that mode is distinct from metadata JSON.

## Visibility Modes

| Mode | View level | Meaning |
|---|---|---|
| `none` | none | Do not emit metadata JSON. |
| `export` | `api` | Emit the projected external API surface. |
| `public` | `source` | Emit `export`, `public`, and `internal` source declarations. |
| `all` | `source` | Emit all metadata-visible source declarations in the compilation. |

`export` is the API-level view. It filters out implementation details that are
not part of the exported source contract and applies export projections. That
means projected names, projected member lists, projected interface lists, and
visible base-class relationships describe what external callers can actually
name. `public` and `all` are source-level views and include more of the source
declaration tree.

API header inputs are not re-emitted as declarations of the current compilation.
For example, metadata for a project that imports `Std` does not include the
standard library's own declarations unless those declarations belong to the
current source module.

## File Naming

Build metadata artifacts are named:

```text
<project>_api.json
```

The project name is `--name` when supplied. Otherwise it is inferred from the
first root source file or package layout. The metadata artifact is written into
the same artifact directory as generated C and API files.

`campc dump metadata` writes the JSON to standard output instead of creating an
artifact file.

## Top-Level JSON Shape

The top-level shape is:

```json
{
  "format": "camp.metadata",
  "version": 1,
  "module": {
    "name": "TextLibrary",
    "namespace": "Text"
  },
  "view": {
    "visibility": "export",
    "level": "api",
    "generated": false
  },
  "declarations": [],
  "stubs": []
}
```

Fields:

| Field | Meaning |
|---|---|
| `format` | Always `camp.metadata`. |
| `version` | Metadata schema version. The documented version is `1`. |
| `module.name` | Logical metadata module/project name. |
| `module.namespace` | Present when source uses `namespace`. |
| `view.visibility` | `export`, `public`, or `all`. |
| `view.level` | `api` for export view, `source` for public/all views. |
| `view.generated` | `false` for the primary metadata view. |
| `declarations` | Fully emitted declarations. |
| `stubs` | Optional identity-only records for referenced declarations outside the selected view. |

Consumers should reject unknown `format` or unsupported major schema versions,
but should tolerate additional fields in schema-compatible versions.

## Metadata IDs

Every emitted declaration has an opaque `id`:

```json
{
  "id": "struct:Text::Buffer",
  "kind": "struct",
  "name": "Buffer"
}
```

Ids are deterministic and readable, but consumers must not derive semantics by
parsing them. Use them as stable references inside one metadata document.

References use the same id string:

```json
{ "ref": "struct:Text::Buffer", "text": "Buffer" }
```

If a referenced declaration is outside the selected view, the serializer may add
a matching record to `stubs`.

## Common Declaration Fields

Most declaration objects may contain:

| Field | Meaning |
|---|---|
| `id` | Opaque metadata id. |
| `kind` | Declaration kind, when not implied by the containing array. |
| `name` | Source-level Camp name. |
| `symbol` | Flattened emitted symbol when different from `name`. |
| `visibility` | `export`, `public`, or `internal` for visible declarations. |
| `extern` | `true` for extern declarations or synthesized API externs. |
| `metadata` | Metadata attributes from doc comments or source attributes. |

Child arrays omit redundant `kind` values where the container already implies
the child kind. For example, a function parameter in a `parameters` array does
not need `"kind": "parameter"`.

## Declaration Kinds

Top-level and nested declaration kinds include:

| Kind | Notes |
|---|---|
| `alias` | Includes target spelling and resolved target data when available. |
| `class` | May include base types, implemented interfaces, fields, and functions. |
| `struct` | Includes fields and functions; source interfaces are emitted as interfaces. |
| `interface` | Includes base interfaces and function slots. |
| `enum` | Includes underlying type and values. |
| `newtype` | Includes underlying value type or callable-newtype shape. |
| `params` | Includes component metadata. |
| `function` | Includes return type, parameters, type parameters, attributes, and modifiers. |
| `variable` | Includes type and inline/constant data when applicable. |

Generated declarations are omitted unless they represent an exported source API
surface, such as synthesized constructor/destructor declarations for exported
concrete classes or synthesized exported interface accessors.

## Type Declarations

Class and struct declarations may include:

- `modifier`, such as `abstract`, `virtual`, `sealed`, or `escaped` where
  source-visible;
- `baseTypes`;
- `interfaces`, including interface refs and exported vtable symbols when
  available;
- `fields`;
- `functions`;
- `typeParameters`.

In the `export` view, class instance fields are omitted because exported class
layout is not a source API commitment. Exported static inline fields may still
appear when selected by the declaration or projection. Struct fields are emitted
when the struct itself is emitted because a struct's fields are part of its
value shape.

When a type is exported through a projection, metadata uses the projected
external name and selected members. A projection can hide class base
relationships by omitting the base type from the exported surface. If both base
and derived classes are exported or projected, the relationship is visible.
Projected class interfaces appear only when listed in the projection and when
the interface is also exported or projected.

Interface implementation metadata may look like:

```json
{
  "interfaces": [
    {
      "type": "Drawable",
      "ref": "interface:Text::Drawable",
      "symbol": "Shape_Drawable"
    }
  ]
}
```

`symbol` is present when an exported vtable symbol is available.

## Enum Values And Inline Constants

Enum values include their computed numeric value after Camp constant evaluation:

```json
{
  "kind": "enum",
  "name": "Mode",
  "underlyingType": "uint",
  "values": [
    { "name": "Open", "type": "Mode", "value": 1 },
    { "name": "Closed", "type": "Mode", "value": 2 }
  ]
}
```

Inline constants are emitted as variable or field records with:

- `inline: true`;
- `value`, when the value is metadata-representable;
- `static: true` for type-scoped inline constants.

Ordinary `const` variables have storage and do not receive a computed metadata
`value` merely because their type is const.

## Callable Newtypes

Callable newtypes use source-level callable shape fields:

```json
{
  "kind": "newtype",
  "name": "TextPredicate",
  "callableType": "fn",
  "returnType": "bool",
  "parameters": [
    { "name": "value", "type": "const char[]" }
  ]
}
```

`callableType` may be `fn`, `delegate`, `once`, `iter`, `async`, or
`async iter`. Concrete callable types preserve callspec and target-spec spelling
in source-level type strings. Context-carrying callable implementation details,
such as hidden context parameters, are not exposed as user parameters.

## Function And Method Metadata

Function objects may include:

- `modifier`, such as `static`, `constructor`, `destructor`, `override`, or
  `sealed`;
- `iterator` for generator declarations;
- `async: true`;
- `callspec`;
- `returnType`;
- `ascription` for callable newtype ascription;
- `interfaceImplementation` for explicit interface slot implementation;
- `interfaceSlotInitializer` for defaulted or optional interface methods;
- `propertyName`, `propertyIndexer`, `propertyIndexParams`, and
  `propertyValueParam`;
- `typeParameters`;
- `parameters`;
- `metadata`.

Async metadata is source-level. It reports `async` and source attributes such as
`@awaitwith` and `@noawait`; it does not expose generated async frames,
resumption helpers, or completion thunks.

An interface implementation marker may appear as:

```json
{
  "name": "writeString",
  "returnType": "void",
  "interfaceImplementation": {
    "interfaceRef": "interface:Text::Writer",
    "interface": "Writer",
    "slot": "writeString",
    "slotRef": "interface:Text::Writer/function:writeString"
  }
}
```

An interface slot initializer may report whether the slot is a function target
or null/default slot:

```json
{
  "name": "flush",
  "interfaceSlotInitializer": {
    "kind": "function",
    "source": "defaultFlush",
    "target": "defaultFlush",
    "targetRef": "function:Text::defaultFlush"
  }
}
```

## Parameters And Generic Parameters

Parameter records may contain:

| Field | Meaning |
|---|---|
| `name` | Source parameter name. |
| `modifier` | `in`, `out`, `within`, `thrown`, or another source modifier. |
| `type` | Source-level parameter type. |
| `capability` | `sizeof`, `typenameof`, or `vtableof`-style capability marker. |
| `targetType` | Type that a capability parameter describes. |
| `interfaceType` | Interface type for `vtableof`. |
| `defaultValue` | Source text for a default value. |
| `overload` | `true` for overload selector parameters. |
| `metadata` | Parameter doc or metadata attributes. |

Special capability parameters keep both the ABI-carried type and the source
target type. For example:

```json
{
  "name": "typenameof_T",
  "capability": "typenameof",
  "type": "string",
  "targetType": "T"
}
```

Generic type parameters include `name`, `constraint`, and `metadata`.

## Type Strings

Type-bearing metadata fields use source-level Camp spelling where a source type
was written. This is important for source contracts that differ from lowered C:

- `constof(source) char*` remains dependent-const source text;
- `this` returns are represented as their source contract where visible;
- `classtype*` remains class-relative source text;
- `fn* _far`, `void* _near`, and concrete callspecs preserve target spelling;
- `async` and callable-newtype shapes remain source-level.

Consumers comparing signatures should resolve source constructs such as
`constof(anchor)` against the containing signature rather than relying only on
raw text.

## Metadata Attributes

Doc comments and metadata attributes are emitted in `metadata` arrays:

```json
{
  "metadata": [
    {
      "name": "summary",
      "content": "Writes %s.",
      "symbols": [
        { "ref": "struct:Text::Buffer", "text": "Buffer" }
      ]
    }
  ]
}
```

The first positional string argument becomes `content`. Named arguments are
emitted by name. `symbolof(...)` references become symbol reference objects.
Symbol arrays correspond to `%s` placeholders in `content` in order.

Literal percent signs in doc-comment content are escaped as `%%` before metadata
emission so `%s` can be used as the symbol-link placeholder convention.

Doc comments themselves are described in the language reference. The important
metadata rule is that they lower to ordinary metadata attributes before JSON is
written.

## Aliases

Alias records may contain:

- `target`: source-level target spelling;
- `resolvedTarget`: resolved target name;
- `targetKind`: `type`, `function`, `method`, `newtype`, `alias`, `callspec`,
  `typespec`, or `primitive`;
- `targetRef`: id of the emitted or stubbed target declaration.

If the resolver cannot cheaply determine `targetKind` or `targetRef`, metadata
omits those fields rather than failing serialization.

## Stubs

`stubs` contains identity-only declarations referenced from the selected view but
not emitted in full:

```json
{
  "stubs": [
    {
      "id": "struct:Text::Buffer",
      "kind": "struct",
      "name": "Buffer"
    }
  ]
}
```

Consumers should use stubs to resolve links and references, not as complete
declaration records.

## Consumer Guidance

Metadata consumers should:

- check `format` and `version`;
- treat `id` and `ref` as opaque;
- tolerate absent optional fields;
- tolerate additional fields;
- infer child kinds from container names when `kind` is absent;
- render metadata attributes by `name`, `content`, and optional symbol arrays;
- use `symbol` only when a generated symbol is explicitly needed;
- prefer source-level type strings for Camp documentation and API display.

Consumers should not expect metadata JSON to include lowered params components,
hidden receiver/context parameters, generated iterator or async state, interface
thunks, virtual-dispatch helpers, local declarations, function bodies, or C
statement bodies.
