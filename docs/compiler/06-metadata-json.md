# Metadata JSON

## Emitting Metadata

Metadata is emitted with `--metadata`:

```sh
campc build library.camp --metadata export
campc build library.camp --metadata public
campc build library.camp --metadata all
campc build library.camp --metadata none
```

`campc dump metadata` prints metadata output and defaults to the export view
when no metadata visibility is otherwise selected.

## Visibility Modes

| Mode | Meaning |
|---|---|
| `none` | Do not emit metadata. |
| `export` | Emit exported API view. |
| `public` | Emit public source view. |
| `all` | Emit all metadata-visible source declarations. |

The export view is an API-level view. Other views are source-level views.

## Top-Level JSON Shape

Metadata JSON starts with:

```json
{
  "format": "camp.metadata",
  "version": 1,
  "module": {},
  "view": {},
  "declarations": []
}
```

`stubs` may appear when referenced declarations are needed but not emitted in
full.

## Metadata IDs

Metadata ids are opaque strings. Consumers should store and compare them as
strings rather than parsing their internal spelling.

## Declaration Objects

Declaration objects include an id, kind, name, optional symbol, optional
visibility, type-specific fields, and metadata attributes. Kinds include
aliases, types, functions, parameters, fields, enum values, and inline
constants.

## Type Objects

Type declarations include fields such as generic parameters, members, base
types, implemented interfaces, enum representation, and source-level type
metadata where applicable.

## Function And Method Objects

Function metadata includes return type, parameters, generic parameters,
receiver-like information, async/callable shape details where visible, and
metadata attributes.

## Inline Constants

Inline constants include their type and constant value when the value is
metadata-representable.

## Aliases

Alias metadata records the source target spelling, resolved target name when
available, target kind when known, and a reference to the target declaration
when it is resolved and visible.

## Attributes And Symbol Links

Documentation comments lower to metadata attributes. Symbol links are emitted as
symbol reference arrays associated with `%s` placeholders in attribute content.

## Consumer Guidance

Consumers should treat ids as opaque, tolerate additional fields in later
metadata versions, and prefer the metadata view that matches their use case.
Documentation renderers should substitute symbol references in order.
