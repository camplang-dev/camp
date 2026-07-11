# Metadata, API Surface, And Symbols

## API Header Model

API headers expose source declarations needed by downstream Camp compilations.
They are not lowered C ABI dumps, and they should not expose generated helpers
unless those helpers are part of the source API contract.

## Metadata View Model

Metadata views are `export`, `public`, and `all`. Export view is API-level.
Public and all views are source-level.

## Export/Public/All Filtering

Filtering must be consistent across metadata, API headers, and visibility
analysis. Referenced declarations not emitted in full may appear as stubs.

## Symbol Names

Source names and ABI symbols are separate. `@symbol` overrides emitted native
symbols but should not change source lookup.

## Metadata IDs

Metadata IDs are stable opaque strings within a metadata document. They are
derived from declaration kind, source name, and containment enough for stable
consumer references.

## Doc Comment Translation

Doc comments lower to metadata attributes before emission. Child targets attach
to parameter, receiver, type parameter, field, method, or enum-value metadata.

## Stubs

Stubs represent referenced declarations that are not emitted in full. Consumers
should treat stubs as identity records, not full declaration records.

## Type And Function Object Details

Type objects describe kind, generic parameters, fields, functions, interfaces,
enum values, aliases, and attributes. Function objects describe return type,
parameters, generic parameters, callable modifiers, and attributes.

## Generated Versus Source Declarations

Metadata is source-level by default. Generated declarations should be omitted
unless they represent exported source API or are necessary stubs.
