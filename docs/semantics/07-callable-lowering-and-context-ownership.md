# Callable Lowering And Context Ownership

## Callable Shape Service

Callable shape logic belongs in shared services so function, delegate, once,
newtype, lambda, method-reference, async, and API surfaces agree on component
order and compatibility.

## Direct Functions

Direct functions lower to function symbols with optional call specs. Function
identity should be preserved for references and exported ABI symbols.

## Delegates

Delegates lower to target plus context. The context type may be null, generated,
or user-provided depending on the callable source.

## `once`

`once` values guarantee single invocation. Context ownership is not implied for
all once values; it belongs to producers that create and own generated context.

## Callable Newtypes

Callable newtypes are nominal wrappers around callable shapes. Compatibility
must respect both callable shape and newtype boundary rules.

## Method References

Method references bind target method plus receiver/context. Lowering must
preserve receiver constness, lifetime, virtual dispatch, and callable target
shape.

## Lambdas

Lambdas are target-typed or inferred against a callable target. Captures become
context fields when the lambda escapes or when a generated callable value needs
storage.

## Escaped Context Allocation

Escaped contexts allocate through the active or explicit allocation context.
The generated context must retain any allocator needed for cleanup.

## Capture Layout

Capture layout should be stable for tests and emission. Expanded captures must
preserve component order and lifetime facts.

## Context Deletion

Generated producers that own context, such as escaped once lambdas and
postpone, arrange context deletion at the correct invocation point.

## Default Arguments And Thunks

Callable references with default arguments may require thunks. Thunks must
preserve signature shape, generic substitutions, and source-visible diagnostics.
