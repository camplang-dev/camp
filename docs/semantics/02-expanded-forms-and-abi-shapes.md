# Expanded Forms And ABI Shapes

An expanded form is a source-level value that is represented by multiple
ABI-visible components. Camp keeps the set of expanded forms closed and
compiler-owned so component names, lowering, metadata, and C emission remain
predictable.

## Expanded Form Definition

A source value is an expanded form when:

- the source language treats it as one logical value;
- single-object storage uses the erased annotation `void<V>`;
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

The same source-versus-ABI distinction matters for nominal layouts. A visible
non-extern class definition exposes its instance fields to source in that
module, including inherited fields, and the internal C layout supports those
accesses. An imported class API remains opaque and does not serialize those
fields. An exposed struct, in contrast, carries its field layout across the
module/API boundary.

## Component Naming

Component names must be stable. For a binding named `items`, examples include:

| Source form | Components |
|---|---|
| `T[] items` | `items`, `items_length` |
| `int? maybe` | `maybe`, `maybe_specified` |
| `int[]? a` | `a`, `a_length`, `a_specified` |
| `Predicate? d` (delegate newtype) | `d`, `d_context`, `d_specified` |
| `delegate R(...) action` | `action`, `action_context` |
| `once R(...) action` | `action`, `action_context` |
| `async R(...) action` | `action`, `action_context` |

The first component usually has the source binding name and corresponds to the
primary carrier. Additional components use suffixes. Use the shared params
component and callable shape services rather than constructing strings locally.

Recognize supplied ABI components by their component binding or exact component
name. An ordinary argument whose identifier contains `context` is still a source
argument; its spelling must not change subsequent expanded argument positions.
When an argument has already expanded, its length component is recognized by
the argument's binding, even when the caller and callee use different parameter
names. Imported source signatures preserve every component of each argument,
including arguments before hidden allocator and expanded-return result slots.
Components generated from expressions such as slices retain their association
with the source argument even when the length is a computed expression.
When a callable or iterator receiver has already been expanded, its carrier and
context remain a bound pair even through carrier casts and when the actual
component names differ from the callee's receiver parameter names.

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

The `const` in `const T[]` qualifies the elements. Body analysis must reject
element assignments, compound assignments, and prefix or postfix updates
through that view before C emission. Slices and inferred copies of the view
preserve element constness. Reads remain valid. For an array of pointers such
as `const int*[]`, const pointees do not make the pointer elements themselves
read-only.

The source component access remains:

```camp
payload.elements;
payload.length;
```

Array element type conversions do not tunnel through the array. A direct value
conversion from `byte* _near` to `byte* _far` does not make
`(byte* _near)[]` convertible to `(byte* _far)[]`. Reconstruct or materialize
as required.

An address of an expanded array carries addresses of both its elements slot and
its length slot. When a call adds element constness to an array pointer, retain
those component addresses; qualification does not add another pointer level.

Indexing, slicing, from-end syntax, and custom `@index`/`@range` parameters
are source access rules over array-like values. Their validation and argument
rewrites are described in
[Core Expression, Statement, And Access Semantics](14-core-expression-statement-and-access-semantics.md).
Once those rules have chosen an access operation, ABI lowering still uses the
array component shape described here.
Direct `.elements` and `.length` access on a fixed-array slice reads the slice
view's offset pointer and range count, just as access through a view local does.

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

An optional is an expanded value with:

- payload component;
- specified component.

An optional holds one level. Its payload is a non-erased, non-expandable copyable
type (`int?`, `Color?`), an array (`int[]?`, `T[]?`, `void<int?>[]?`) or a
delegate shape (`Predicate?`, `delegate int()?`). A payload that is an optional,
a type parameter of any representation, `void<V>` or an integer carrier is an error
wherever it is written: `int??`, `T?`, `void<V>?` and `long<Color>?`. Because a
generic declaration cannot write `T?`, substitution never produces a nested
optional. An optional remains a valid type argument and can be held whole as the
`V` of `void<V>` storage, so `List<int?>` and `List<int[]?>` are valid.

An optional of a non-expandable payload has two components, an optional array has
the elements pointer, the `nuint` length and the presence flag, and an optional
delegate shape has the call, the context and the presence flag. Every optional
exposes `.value` and `.specified`. Optional arrays also forward `.elements`,
`.length`, indexing, and slicing; optional delegate shapes forward `.call`,
`.context`, and invocation.

The same logical component paths determine local, field, parameter, and return
component names. Reserve all expanded names against user bindings. A pointer to
an expanded value distributes recursively over its components; those components
need not be adjacent. `void<V>` instead provides one storage object when required,
and `void<V>[]` stores one complete `V` per element.
Initializing or assigning a distributive pointer from `null` defaults every
component pointer, including the pointer to an optional's presence flag.
An element of `void<V>[]` or an indexed `void<V>*` passed to `in void<V>`
forwards the address of that element's storage. It does not create a new
compound literal; evaluate the array and index expressions once.

Optional payloads must be copyable. Resolve type aliases before checking
copyability; an alias has the same eligibility as its target type.
Direct classes, fixed structs, and fixed-size
arrays require a pointer payload. Copying an optional delegate copies its target
and context, not the referenced object. A copied `once` value has its own duty of
one invocation. Arrays and fixed-size arrays cannot directly contain expanded
values, including distributive pointers to expanded values; use `void<V>[]`
storage instead.

Optional conversion must preserve optional shape. A conversion that reinterprets
the payload without respecting the optional's specified bit is invalid. The
specified component is part of the value contract, not a spare implementation
detail.

Returning a plain payload from an optional-returning function lifts that payload
to a present optional. Lowering writes `true` to the specified result component
for struct payloads as well as scalar payloads. Returning an existing optional
carries its specified component instead. `int? none = default;` defaults the payload and the flag.

The literal `null` denotes a null pointer, not an empty optional. Reject it for
every optional destination (including optional pointers, arrays, and delegates)
in declarations, assignments, arguments, and returns. Use `default` for an empty
optional; a typed pointer payload can still be lifted as an ordinary value.

`T?` converts to `U?` exactly when `T` converts to `U`, with the same
classification (implicit, explicit, or unsafe): the payload component converts as
`T` to `U` and the specified component is carried unchanged, whether or not it is
set. This is the optional's own conversion rather than a tunnel through it, so
nothing converts deeper than the payload's conversion: `int[]?` does not convert to
`long[]?`.

## Delegates And `once`

`delegate R(...)` and `once R(...)` are context-carrying callable expanded
values:

- `call`: function target;
- `context`: `void*` context pointer.

The `call` target receives the context as its first ABI argument. Source callable
syntax hides that context for ordinary calls. The context may be null,
user-provided, or compiler-generated.

When a method reference is bound to an expanded receiver such as an optional or
another delegate-like value, the compiler materializes the receiver components
into temporary storage and uses that storage as the delegate context. The
generated call target is an adapter that casts the context back to the
materialized expanded shape before calling the original method with the expanded
components.

The compiler must not silently synthesize stack materialization for an escaped
delegate target. If an expanded receiver is captured by an escaped delegate or by
a target requiring `escaped this`, the receiver must already live in suitable
escaped materialized storage; otherwise the compiler reports a diagnostic.

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

`iter T(...)` values are protocol-shaped callable values. Iterator expansion
creates state and protocol slots used by `foreach`, `yield`, cleanup, and
current-value access.

Compiler writers should treat iterator expansion as source-level iterator
semantics, not as an arbitrary delegate. The generated state may retain locals,
parameters, thrown slots, and lifetime facts across yields. It must participate
in lifecycle and lifetime checks.

Generator declaration validation happens before source bodies are rewritten
into state factories and `next` methods:

- `struct iter` and `class iter` bodies must contain a source `yield` or
  `yield break` statement somewhere;
- `yield break` lowers to iterator completion, setting the state finished and
  returning `false` from the generated `next`;
- source `return` statements are invalid inside generators;
- ordinary functions returning plain `iter T` values are not generators and keep
  ordinary `return` semantics.

When an instance generator is expanded, the generated iterator state retains the
source receiver as a state field. Rewriting `this` inside the generated `next`
method must recover that retained receiver before member lookup, so
`this.member` binds as it did in the source generator rather than against the
iterator state type.

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

A body that directly assigns its own thrown parameter, or uses that parameter
directly as a catch argument, manages the final slot value itself. Suppress
automatic default-error writes for the entire body, at explicit success returns
and implicit fall-through alike. Preserve whatever value the body or its caller
stored on paths without another write. Source `throw` and implicit propagation
do not trigger this rule merely because lowering generates assignments for
them; otherwise successful paths clear the slot automatically.

## Erased Storage With `void<V>`

`void<V>` annotates one storage object containing `V`. It retains `V` for Camp
analysis and lowers to `void` at the C boundary. `V` cannot be `void` or another
erased type directly. The removed `struct(V)` syntax is diagnosed with guidance
to use `void<V>`.

Written erased types are permitted behind pointers (including multiple depths
and constness), as dynamic array elements, as `in`/`out` parameters, as bare
results (`void<int> seven()` returns through caller storage), and as `stackalloc`
locals; a `stackalloc` local must be a `void<V>` or an erased type parameter. A
generic argument may contain erased storage behind a pointer. By-value parameters,
ordinary locals, fields, fixed-size array elements and by-value generic arguments
cannot name erased storage, and `void<V>` is never an optional payload. Validation
applies to written types before substitution.

```camp
stackalloc void<int?> stored = 3;
void<int?>* pointer = &stored;
int? copy = *pointer;
pointer.value = 4;
void<int?>[] values = stackalloc void<int?>[4];
values[0] = copy;
```

Reads produce `V`; writes store `V`. A stack storage address has type
`void<V>*`. An ordinary expandable address remains distributive and has one
pointer per component. It cannot convert to an erased storage pointer. The
reverse direction is implicit: any `void<V>*` of an expandable `V` expands to
the distributive `V*`, with one pointer per component addressing that component
inside the one storage object. A computed source pointer is evaluated once.
Returning this conversion directly uses the same component addresses as a
local initializer or argument; every returned pointer aliases the source storage.
Const erased storage expands to a const distributive pointer, including optional
and array payloads. The conversion must not remove const or volatile qualifiers.
Positional and named brace initializers of distributive pointer fields also
expand the source storage into all component addresses, evaluating it once.
Materialized members use logical component names, such as `value` and
`specified`, `elements` and `length`, or `call` and `context`. Erased arrays
index by the size of the complete object, including all components.

For non-expandable `P`, `void<P>*` and `P*`, and `void<P>[]` and `P[]`, convert
implicitly in both directions while preserving constness. Disjoint erased
pointers follow disjoint pointer conversion rules; other pointer families need
a `void*` fence. Recovery from `void*` requires an explicit cast.

At the C boundary an erased pointer is `void*`, an erased array is `void*` plus
length, an `in` erased parameter is `const void*`, and an `out` erased parameter
is `void*`. Concrete storage remains typed internally. `typenameof(void<V>)`
is the name of `V`; an annotated type can be neither an overload selector nor a
method's receiver type.

## Integer Carriers

For each integer representation `C` (`byte`, `ushort`, `short`,
`uint`, `int`, `ulong`, `long`, `nuint`, `nint`), `C<V>` is a carrier type: a
value of type `V` carried as a `C`. It has the layout of `C` and the semantics a
value of a type parameter `T: C` bound to `V` has. `V` must be a valid
argument for `T: C` on the selected target: an integer, bool or character type
of equal or smaller width, an enum or scalar newtype over an eligible type of
equal or smaller width, a `float` or `double` that fits (its exact bits are
carried and the unused high bits are zero), or, for `nint` and `nuint`, a data
pointer or string that fits the natural integer. `V` cannot be expandable, a
raw function pointer or no-value `void`. Encoding and carrier equality are
specified in
[Generics, Erasure, And Capabilities](06-generics-erasure-and-capabilities.md). Generic
substitution replaces an existing outer annotation with the destination C,
preserving its actual nominal payload and nested arguments.

Unlike `void<V>`, a carrier has a specified size. It is stored by value and may
be a local, global, field, by-value parameter, result, array element, pointer
target or generic argument. A carrier value reads as `V`; the address of carrier
storage has the written carrier pointer type. At the C boundary `C<V>` is `C`,
`C<V>*` is `C*`, and `C<V>[]` is `C*` and a `nuint` length. A pointer `V`
stored in an `nint` or `nuint` carrier is converted to and from the C integer at
each store and load. A carrier is never an `overload` selector or a receiver
type.

## Expanded Returns

Erased results are a separate transport form. A written or substituted `void<V>`
result returns `void` and takes a writable result pointer. The pointer comes after
source parameters and before compiler-supplied slots. At the call site the result
reads as `V`. An absent item is reported with a `bool` result and an `out` parameter
(`List.tryGetItem`), not with an optional erased result; `void<V>?` is an error.

Callables may return other callables. The returned callable's signature and
expanded context are distinct from the outer callable's parameters and context.
A plain `fn` result is one carrier; matching the signature of a delegate's
primary carrier does not add a context result to that function pointer.

An expanded return is represented by multiple ABI result paths. Depending on the
shape, lowering may rewrite the return into `out` components, prepared
temporaries, completion callbacks, or protocol state writes.

Compiler writers must keep result semantics source-level:

- `return values;` returns one logical value;
- `constof` and lifetime checks apply to the logical result;
- generated component assignments preserve all component facts;
- a local initialized by an expanded-return call through a callable value invokes
  that callable once and initializes all components from that invocation;
- selecting an expanded member of a call result evaluates the receiver call
  once, with its full argument ABI, before extracting the member's components;
- extracting expanded fields through `this` follows the receiver's storage
  type: a struct value receiver uses value member access, while a pointer
  receiver uses pointer member access;
- compiler-generated storage assigned by conditional branches is writable;
  constness of referenced array elements is preserved;
- a conditional selecting expanded-return calls evaluates its condition once and
  only the selected call, then takes every result component from that call;
- metadata and API headers expose the source return type.

Witness constructor results follow the same rule: their source result is
`T*` even though the shared interface table's C function pointer returns
erased `void*`. ABI expansion must not turn that erased pointer into an
`Interface*` result or alter unrelated allocator/error components.

## Generic Expanded Forms

In erased generic code, `T` may denote an expanded form after substitution.
Pointers to `T` refer to the storage form of `T`. Storage, arrays, optionals,
and delegate fields involving generic `T` must use materialized storage where a
single object is required.

Do not recursively expand `T[]`, `T[]?`, or delegate shapes inside generic erasure
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
- single-object `void<V>` storage;
- generic `T` substituted with expanded forms;
- metadata/API filtering;
- C emission for at least one native target;
- lowering dump stability.
