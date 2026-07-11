# Expanded Forms And ABI Shapes

## Expanded Form Definition

An expanded form is a source type or parameter surface represented by multiple
lowered components. The source language treats the form as one value, while
binding and lowering expose component shapes for calls, storage, ABI, and C
emission.

## Arrays

`T[]` expands to an element pointer and a length. A target type specifier on an
array carrier applies to the carrier components, not to the element type.
Element-type conversions do not tunnel through the array carrier.

## Delegates And `once`

Delegates expand to callable target plus context. `once` uses a compatible
callable carrier but has single-invocation semantics. Escaped lambda and
postpone producers can own generated context and arrange deletion.

## Iterators

Iterators expand to protocol state and callable slots used by `foreach`,
`yield`, cleanup, and lowered state machines. Iterator expansion must preserve
element type, thrown slots, and captured lifetime facts.

## Async Callable Shapes

Async callables expand to callback-shaped functions. Awaitable completion
callbacks return `void`, have no `out` parameters, may contain one thrown slot,
and have at most one ordinary success value.

## Grouped Params

Grouped params are lowered to generated structs or parameter carriers where a
source surface requires a named multi-component value.

## Thrown Params

`thrown(T)` participates in callable shape and parameter expansion. Lowering
must preserve propagation, catch arguments, async completion, and iterator
behavior.

## Component Naming

Generated component names must be stable for dumps, C emission, metadata, and
tests. Component naming should go through shared services rather than local
string construction.

## API Surface Versus Lowered Shape

API headers and metadata should expose the source declaration surface unless a
lowered helper is itself part of the exported contract. Lowering dumps may show
implementation shape; user-facing docs should not depend on that shape.
