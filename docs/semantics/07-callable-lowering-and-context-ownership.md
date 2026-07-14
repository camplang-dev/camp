# Callable Lowering And Context Ownership

This supplement describes how callables are represented and lowered. It is
about the compiler's internal shape rules, generated context ownership, and ABI
contracts for `fn`, `delegate`, `once`, `iter`, `async`, callable newtypes,
method references, and lambdas.

User-facing syntax appears in
[Functions, Methods, And Callables](../language/06-functions-methods-and-callables.md)
and [Async, Await, And Deferred Calls](../language/15-async-await-and-deferred-calls.md). This document
focuses on the rules compiler writers must keep consistent.

## Callable Shape

Callable shape logic belongs in shared services so function, delegate, once,
newtype, lambda, method-reference, async, and API surfaces agree on component
order and compatibility.

The compiler's normalized callable shape records:

- callable kind, such as `fn`, `delegate`, `once`, `iter`, or `async`;
- target type specifier when present;
- call specifier when present;
- return type;
- ordered parameter slots, including `in`, `out`, `thrown`, and `within`
  modifiers;
- explicit callable `this` contract when present.

Any compiler path that compares callables should use the shared callable-shape
service or an equivalent centralized helper. Do not compare callable strings by
ad hoc splitting in a feature-specific pass.

## Shape Expansion

Callable comparison must account for expanded forms. A source parameter such as
`byte[] data` may lower to multiple ABI parameters, and a `delegate` value is a
pair of components. Callable shape comparison should expand parameter shapes
when the comparison is about ABI compatibility, and preserve source spelling
when the comparison is about metadata or diagnostics.

`constof` variance is a special compatibility mode. It allows a source callable
slot to satisfy a target callable slot when the only difference is a legal
`constof` relation in the correct input/output position. See
[Constof And Signature Compatibility](04-constof-and-signature-compatibility.md).

## Direct Functions

Direct functions lower to function symbols with optional call specs. Function
identity should be preserved for references and exported ABI symbols.

An ordinary function value is a `fn` callable. It has no hidden context. A
function symbol can be passed as a function pointer when the target callable
shape matches after target specs, call specs, expanded parameters, return type,
and `constof` rules are applied.

Function pointer values are treated as escaped for lifetime purposes: the
function symbol itself does not depend on a local closure context.

Raw `fn*` is not the same as a concrete `fn` type. A concrete `fn` records
signature, call spec, target spec, and lifetime relations. `fn*` is a raw carrier
described in the conversion supplement.

## Delegates

Delegates lower to target plus context. The context type may be null, generated,
or user-provided depending on the callable source.

The canonical lowered delegate-like value is:

- `call`: a concrete `fn` value whose first parameter is the hidden context
  pointer when a context is needed;
- `context`: a pointer to hidden context storage, usually represented as
  `void*` at the call boundary.

When invoking a delegate, lowering rewrites:

```camp
callback(value)
```

into a call of the stored `call` component with the stored `context` inserted as
the first argument, followed by the source arguments.

The compiler must keep delegate values as multi-component values. A delegate as
a whole does not cast to `fn*`, `nint`, or `untyped`; low-level code that needs
the target must operate on the call component and preserve/rebuild the context
component deliberately.

## `once`

`once` values guarantee single invocation. Context ownership is not implied for
all once values; it belongs to producers that create and own generated context.

The compiler should distinguish the source type guarantee from a particular
lowered ownership policy:

- a `once` value promises that the callable contract is single-use;
- a generated producer such as `postpone` may allocate and own a context;
- invocation of an owning generated once value may include context cleanup;
- a borrowed or imported once value does not automatically imply the compiler
  may delete its context.

Any lowering that deletes a once context must be tied to a producer that created
that context and knows the allocator/cleanup method to use.

## Callable Newtypes

Callable newtypes are nominal wrappers around callable shapes. Compatibility
must respect both callable shape and newtype boundary rules.

Newtype callable shape is derived from the newtype's underlying callable or
iterator type plus its declared parameter list. If the newtype has an explicit
callable `this` parameter, that receiver contract becomes part of the shape.

Callable ascription connects a declaration to a callable newtype or interface
slot. The validator must ensure:

- receiver-bearing declarations do not ascribe receiverless callable newtypes;
- receiverless declarations do not ascribe delegate-like receiver-bearing
  shapes;
- call spec and target spec match;
- parameter and return shapes match after source-level `constof` rules;
- unsupported ascription cases diagnose before lowering.

Metadata should expose the source callable newtype relationship, not the
generated helper functions used to implement it.

## Method References

Method references bind target method plus receiver/context. Lowering must
preserve receiver constness, lifetime, virtual dispatch, and callable target
shape.

An unbound static method reference can lower like an ordinary function symbol.
An instance method reference needs a receiver. Depending on the target callable,
the receiver may become:

- an explicit first parameter for direct lowered calls;
- a delegate context pointer for a bound method reference;
- an interface instance slot for interface methods;
- a virtual-dispatch context for virtual class calls.

Explicit callable `this` parameters are part of the callable type. A target
callable with `const this` requires a method callable on a const receiver. A
target callable with `escaped this` requires a receiver context that satisfies
the escaped lifetime requirement.

Virtual and interface method references should not bypass their dispatch
mechanisms merely because a concrete implementation is visible. The generated
call must preserve the source semantics of dynamic dispatch.

## Lambdas

Lambdas are target-typed or inferred against a callable target. Captures become
context fields when the lambda escapes or when a generated callable value needs
storage.

Lambda lowering creates:

- a generated function containing the lambda body;
- zero or more generated context fields for captures;
- a generated context local or allocation when captures are needed;
- a delegate/once/async initializer when the target is context-carrying;
- a direct method reference when the target is a capture-free `fn`.

Capturing lambdas require a context-carrying target. The compiler must diagnose
a capturing lambda assigned to a plain `fn` target, because there is no context
slot to carry captured state.

For delegate-like targets, the generated function receives an initial `_context`
parameter. The body casts that parameter to the generated context type, then
rewrites captured variable references to context field reads.

## Capture Collection

Capture collection should walk the source lambda body before lowering mutates
the tree. It must distinguish:

- local variables;
- parameters;
- `this`;
- generated variables that should not be captured as user state;
- unsupported captures that need diagnostics.

Capture field order should be stable. Stable order keeps lowered dumps, C
output, and diagnostics predictable.

## Scoped Versus Escaped Contexts

For a scoped context, captured values can be stored as pointers to existing
storage when the lifetime analyzer proves the delegate cannot escape. The
context local can live on the stack-like source scope.

For an escaped context, captured values must be stored by value or in storage
that itself satisfies the escaped lifetime requirement. The generated context is
allocated through the current allocation context when one is needed.

Lifetime analysis decides whether captured pointer-bearing values may enter the
context. Callable lowering must preserve those decisions rather than
reclassifying captured values after the fact.

## Escaped Context Allocation

Escaped contexts allocate through the active or explicit allocation context.
The generated context must retain any allocator needed for cleanup.

When a generated delegate context owns cleanup work, the lowering may add an
allocator field to the context. That field allows the generated callable body or
cleanup path to use the same allocator later, after the source statement that
created the context has finished.

Allocator capture is needed when:

- the lambda body uses the inherited allocator;
- generated cleanup will delete captured state;
- a `postpone` or escaped once producer needs to destroy its context at
  invocation time.

The allocator argument order must remain compatible with the ordinary
`within`-argument lowering rules.

## Capture Layout

Capture layout should be stable for tests and emission. Expanded captures must
preserve component order and lifetime facts.

Captured values with expanded forms should either be captured as the original
source value when that is meaningful, or as the correct ordered component list.
Mixing source-order captures with ABI-order parameters is a common source of
miscompiled delegates.

Generated field names should avoid collisions with user names and C reserved
words. They should still be readable in dumps so compiler tests can assert the
intended capture behavior.

## Context Deletion

Generated producers that own context, such as escaped once lambdas and
postpone, arrange context deletion at the correct invocation point.

Deletion must happen after the source callable body has consumed the context and
after any thrown/result paths have copied out values that need the context.
Generated cleanup should run on all source-visible exit paths where the
language promises cleanup.

Do not attach deletion to a type merely because it is `once`; attach deletion to
the generated producer/owner relationship.

## Default Arguments And Thunks

Callable references with default arguments may require thunks. Thunks must
preserve signature shape, generic substitutions, and source-visible diagnostics.

Default arguments are inserted before callable lowering flattens instance
calls, delegate calls, interface dispatch, `within` arguments, `sizeof`
arguments, and `vtableof` arguments. If a thunk is generated to adapt a
callable reference with defaults, it must:

- use the target callable shape;
- preserve generic substitutions;
- forward `thrown`, `out`, and `within` slots in the correct order;
- apply default expressions with the same semantics as a direct call;
- preserve source ranges for diagnostics.

## Interface And Virtual Calls

Interface calls lower through vtable slots and an interface instance context.
Virtual class calls lower through the class vtable field and generated slot
functions. Callable lowering should treat these as dispatch-preserving method
references, not as plain direct function references.

For generic interface calls, `vtableof(T: Interface)` supplies the slot table
and the receiver is converted to the interface instance shape. See
[Interface VTables And Dynamic Dispatch](09-interface-vtables-and-dynamic-dispatch.md).

## Async Callable Values

Async callable shapes are source-level callable values whose lowering includes
completion callbacks and resumer behavior. Lambdas assigned to async targets are
lowered through the callable machinery first, then async-specific lowering
rewrites await/resume behavior. See
[Async Resumption Lowering](08-async-resumption-lowering.md).

Callable lowering must preserve:

- the source async callable kind in metadata;
- callback and thrown/result slot ordering;
- hidden context ownership for captured async lambdas;
- allocator capture for postponed or escaped async work.

## Diagnostics

Callable diagnostics should name both the source callable and the required
target shape. Important diagnostic categories include:

- capturing lambda without a context-carrying target;
- lambda target type cannot be inferred;
- invalid callable ascription;
- receiver/`this` mismatch;
- call spec or target spec mismatch;
- delegate signature cast that requires reconstruction;
- escaped lambda capture lifetime failure;
- ambiguous overload selected from an untyped lambda;
- invalid delegate context use where a plain `fn` was expected.

The source range should usually be the lambda expression, callable type,
ascription marker, method reference, or cast that introduced the incompatible
shape.

## Test Surface

Callable changes should cover:

- direct `fn` values and raw `fn*` fences;
- delegate invocation and component order;
- once producer ownership and context cleanup;
- callable newtype ascription;
- explicit callable `this`;
- const/lifetime-sensitive callable compatibility;
- lambdas with and without captures;
- escaped and scoped capture diagnostics;
- method references, virtual methods, and interface methods;
- default arguments through callable references;
- metadata output for callable newtypes and async attributes.

## Implementation Anchors

Primary implementation points include:

- `CallableShapeService.cs` for parsing and comparing callable shapes;
- `BindableNodeAnalyzer.Callables.cs` for source callable shape construction;
- `BindableNodeAnalyzer.Lowering.Lambdas.cs` for lambda/context lowering;
- `BindableNodeAnalyzer.Lowering.InstanceCalls.cs` and
  `BindableNodeAnalyzer.Lowering.Interfaces.cs` for dispatch rewriting;
- `BindableNodeAnalyzer.Lowering.Expressions.cs` for delegate invocation and
  hidden argument insertion;
- `BindableNodeAnalyzer.ConstOf.cs` for callable `constof` compatibility;
- callable, lambda, delegate, async, and interface fixtures under
  `tests/Diagnostics` and `tests/Metadata`.
