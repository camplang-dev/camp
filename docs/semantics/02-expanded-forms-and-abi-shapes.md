# Expanded Forms And ABI Shapes

An expanded form is a source-level value that is represented by multiple
ABI-visible components. Camp keeps the set of expanded forms closed and
compiler-owned so component names, lowering, metadata, and C emission remain
predictable.

## Expanded Form Definition

A source value is an expanded form when:

- the source language treats it as one logical value;
- ordinary storage may need a materialized `struct(T)` form;
- parameter passing and returns may expose multiple ABI components;
- member access exposes compiler-defined components such as `.length` or
  `.context`;
- user code cannot define new forms with the same behavior.

Expanded forms are not tuples and are not hidden arbitrary structs. They have
fixed language semantics and fixed component naming rules.

## Source Surface Versus ABI Surface

The source surface is what users write:

```camp
void writeAll(const byte[] bytes);
```

The ABI surface has components:

```text
bytes
bytes_length
```

The first component keeps the declared parameter name. Later components use a
fixed suffix derived from the logical component name. The source still treats
`bytes` as the array value and exposes `bytes.elements` and `bytes.length`.

Compiler writers must preserve both views:

- source diagnostics should speak in source terms;
- ABI collision checks must reserve component names;
- named arguments may use source parameter names and visible component names
  according to the source rules;
- C emission must use the ABI components;
- metadata and API headers should prefer the source form.

## Component Naming

Component names must be stable. For a binding named `items`, examples include:

| Source form | Components |
|---|---|
| `T[] items` | `items`, `items_length` |
| `T? maybe` | `maybe`, `maybe_specified` |
| `delegate R(...) action` | `action`, `action_context` |
| `once R(...) action` | `action`, `action_context` |
| `async R(...) action` | `action`, `action_context` |

The first component usually has the source binding name and corresponds to the
primary carrier. Additional components use suffixes. Use the shared params
component and callable shape services rather than constructing strings locally.

## Arrays

`T[]` is a span-like expanded value:

- `elements`: pointer to element storage;
- `length`: `nuint` element count.

A parameter written:

```camp
void send(const byte[] payload);
```

has ABI components conceptually equivalent to:

```camp
void send(const byte* payload, nuint payload_length);
```

The source component access remains:

```camp
payload.elements;
payload.length;
```

Array element type conversions do not tunnel through the array. A direct value
conversion from `byte* _near` to `byte* _far` does not make
`(byte* _near)[]` convertible to `(byte* _far)[]`. Reconstruct or materialize
as required.

## Target Specs On Expanded Carriers

A target type spec on an expanded array applies to the carrier components, not
to the element type:

```camp
int[] _near nearValues;
```

Conceptually:

| Component | Carrier domain |
|---|---|
| `nearValues.elements` | `int* _near` |
| `nearValues.length` | `nuint _near` |

By contrast, `(int* _near)[]` is an ordinary array carrier whose elements are
near pointers. Preserve this distinction in type binding, conversion
classification, and C emission.

## Fixed-Size Arrays

`T[n]` is not an expanded form. It is inline storage with one storage identity.
It may expose `.elements` and `.length` for array-like use, and it may convert to
a matching span where the language permits, but it does not introduce ABI
component bindings in the containing scope.

A pointer to a fixed-size array is a pointer to that storage object. It must be
dereferenced before fixed-array indexing, slicing, or span conversion rules
apply.

## Optionals

`T?` is an expanded value with:

- payload component;
- specified component.

The payload uses the payload type's source semantics. If the payload type is
itself an expanded form, compiler writers must avoid recursive ad hoc expansion
and use the materialized storage representation when one storage object is
required.

Optional conversion must preserve optional shape. A conversion that reinterprets
the payload without respecting the optional's specified bit is invalid. The
specified component is part of the value contract, not a spare implementation
detail.

## Delegates And `once`

`delegate R(...)` and `once R(...)` are context-carrying callable expanded
values:

- `call`: function target;
- `context`: `void*` context pointer.

The `call` target receives the context as its first ABI argument. Source callable
syntax hides that context for ordinary calls. The context may be null,
user-provided, or compiler-generated.

`once` has the same broad carrier shape as a delegate but a different semantic
contract: it is intended for single invocation. Producers that allocate and own
generated once context, such as escaped once lambdas and `postpone`, must arrange
cleanup at the correct invocation point.

## Async Callable Values

An `async R(...)` callable value is also context-carrying at the source value
level, but its call target is callback-shaped. The expanded callable value has:

- `call`: function target;
- `context`: `void*` context pointer.

The function target receives:

1. context;
2. visible source arguments;
3. completion callback;
4. completion context.

The completion callback returns `void`, receives its own context first, and then
receives completion result slots. Completion shape is described in
[Async Resumption Lowering](08-async-resumption-lowering.md).

## Iterators

`iter T(...)` and `async iter T(...)` are protocol-shaped callable values.
Iterator expansion creates state and protocol slots used by `foreach`, `yield`,
cleanup, and current-value access.

Compiler writers should treat iterator expansion as source-level iterator
semantics, not as an arbitrary delegate. The generated state may retain locals,
parameters, thrown slots, and lifetime facts across yields. It must participate
in lifecycle and lifetime checks.

## Grouped Params

`params` declarations can define source-level grouped values that lower to
component shapes. When a source API wants a named multi-component value, the
compiler may use generated structs or parameter carriers to keep ABI components
stable while preserving the source form.

Component order is part of the ABI. Generated components must be produced
through shared params-component helpers so call lowering, default arguments,
dumps, and C emission agree.

## Thrown Slots

`thrown(T)` participates in function, async completion, iterator, and callable
shapes. It is a source-level error propagation slot with ABI consequences.

Lowering must preserve:

- thrown argument names, including the implicit `error` name where applicable;
- catch argument binding;
- async completion error slots;
- iterator error/current behavior;
- default propagation through calls and returns.

Do not treat thrown slots as ordinary output values for `constof` variance or
ordinary return covariance.

## Materialized Storage With `struct(T)`

`struct(T)` materializes an expanded form into ordinary one-address storage. It
is required when code needs:

- a field or local storing an expanded value as one object;
- an array element type for an expanded form;
- a pointer to the whole expanded value;
- erased generic storage for a possibly expanded `T`;
- `sizeof(T)` of an expanded form.

The materialized fields match logical component names, not necessarily ABI
parameter names:

```camp
struct(byte[]) stored;
stored.elements;
stored.length;
```

Lowering may expand materialized storage back into components when passing to a
source parameter of the expanded form.

## Expanded Returns

An expanded return is represented by multiple ABI result paths. Depending on the
shape, lowering may rewrite the return into `out` components, prepared
temporaries, completion callbacks, or protocol state writes.

Compiler writers must keep result semantics source-level:

- `return values;` returns one logical value;
- `constof` and lifetime checks apply to the logical result;
- generated component assignments preserve all component facts;
- metadata and API headers expose the source return type.

## Generic Expanded Forms

In erased generic code, `T` may denote an expanded form after substitution.
Pointers to `T` refer to the storage form of `T`. Storage, arrays, optionals,
and delegate fields involving generic `T` must use materialized storage where a
single object is required.

Do not recursively expand `T[]`, `T?`, or delegate shapes inside generic erasure
without checking whether `T` itself is expanded. Use the generic capability and
materialization rules in
[Generics, Erasure, And Capabilities](06-generics-erasure-and-capabilities.md).

## API, Metadata, And Dumps

API headers and metadata should expose source expanded forms unless a generated
helper is itself an exported source API declaration. Lowering dumps may show ABI
components and generated helpers. C emission must use ABI components.

This gives three legitimate views of the same program:

- source/API view: `const byte[] payload`;
- lowering view: component bindings and helper calls;
- C view: concrete C pointer/length parameters.

Do not collapse these views into one representation. Each output surface serves
a different consumer.

## Test Expectations

Expanded-form changes should normally have tests for:

- source component access;
- ABI component names and collision diagnostics;
- named and positional argument behavior;
- default arguments with expanded parameters;
- materialized `struct(T)` storage;
- generic `T` substituted with expanded forms;
- metadata/API filtering;
- C emission for at least one native target;
- lowering dump stability.
