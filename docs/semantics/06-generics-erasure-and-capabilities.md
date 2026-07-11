# Generics, Erasure, And Capabilities

## Generic Parameter Binding

Generic parameters are bound in type and function scopes. Type arguments are
applied through substitution while preserving nominal boundaries and source
syntax for diagnostics.

## Constraints

Constraints determine which operations are legal. `any` permits the type to be
named. `copyable` permits copying. Interface constraints permit interface
calls when the required vtable capability is available.

## Erased Versus Materialized Values

Some generic values are erased at source analysis boundaries. Operations that
need layout, size, vtable, type name, construction, destruction, or copy
capability must request the corresponding capability explicitly.

## `T: any`

`T: any` does not imply copyability, size availability, default filling, array
stride, or destructor availability. Report specific capability diagnostics.

## `T: copyable`

`T: copyable` permits copy operations but does not imply unrelated capabilities
such as default construction or interface dispatch.

## Size, VTable, And Type Name Capabilities

`sizeof(T)`, `vtableof(T: Interface)`, and `typenameof(T)` parameters transport
capabilities into generic bodies. Lowering must thread those values through
generated calls and helper declarations.

## Generic Arrays And Iterators

Generic arrays need element stride and lifetime-safe element handling. Generic
iterators need element type, state, and cleanup behavior that remains valid
under erasure.

## Generic Construction And Destruction

Generic construction and destruction require the generated helper surface or
capability values needed to allocate, initialize, finalize, and free the value.

## Static Members

Generic static members belong to constructed generic contexts. Exported static
members must preserve API visibility and generated symbol uniqueness.

## Diagnostics

Diagnostics should tell the author which capability to add and which operation
requires it.
