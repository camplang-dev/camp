# Conversions, Raw Carriers, And Fence Casts

Camp conversion classification is a semantic contract between type checking,
target metadata, diagnostics, lowering, and C emission. The classifier must
state not only whether a conversion is possible, but which source syntax is
required.

## Conversion Levels

Camp uses these conversion levels:

| Level | Surface | Meaning |
|---|---|---|
| implicit | assignment/call with no cast | Known and safe enough for ordinary conversion. |
| explicit | `(T)value` | Known conversion that requires source acknowledgement. |
| unsafe | `(unsafe T)value` | Directly related conversion that breaks a protected contract. |
| fence-required | raw carrier fence first | Type information must be erased before recovery. |
| reconstruct-required | build a new value | Shape-changing or multi-component conversion is not a cast. |
| forbidden | none | The conversion is invalid for this target and source shape. |

An unsafe conversion is not the only dangerous conversion. A fence conversion is
also dangerous; the distinction is that a fence intentionally erases type
information through a raw carrier before recovering a type.

## Cast Forms

Ordinary casts use:

```camp
(TargetType)value
```

Unsafe casts use:

```camp
(unsafe TargetType)value
```

Lifetime casts use:

```camp
(scoped)value
(unscoped(owner))value
(escaped)value
```

They may combine with type syntax:

```camp
(escaped const char[])text
```

The conversion classifier handles type conversion. Lifetime analysis handles the
lifetime assertion. Do not let a type cast silently change lifetime facts without
the lifetime-analysis path recording that change.

## Conversion View

Annotated storage types (`void<V>` and the integer carriers `C<V>`) keep their
written identity in symbols, metadata, generic type identity and `typenameof`.
Conversion classification does not use that identity directly. Both types are
first rewritten into their **conversion view**, and the existing rules then
classify the rewritten types. There is one view operation. Conversion
classification, callable compatibility, receiver eligibility, overload
selection and initializer or literal checking all use it; none has a separate
rewrite. Never overwrite a canonical type with its view.

The view is context aware. *Stripping* an annotation erases it and leaves the
annotated type.

1. A type that is itself a non-void carrier, not behind `*` or `[]`, is
   stripped: `long<Color>` becomes `Color`. This applies only at the top of the
   type and never to a by-value position inside a callable signature.
2. The remaining type is rewritten recursively, including generic arguments,
   pointer and array elements, and callable signatures. A `void<V>` whose `V`
   is not expandable is stripped, except when it is the result type of a
   callable, at any nesting depth.
3. A carrier that survives (behind a pointer or array, as a generic argument,
   or in a callable signature) is classified as a value newtype whose
   underlying type is the carrier integer, as if declared
   `newtype LongCarrierOfColor: long`.

Because the view follows ordinary newtype rules, a pointer to a carrier
converts to a pointer to its carried type or to its carrier integer only by an
explicit cast, and `W<long<Color>>` remains a different type from `W<Color>`.
`void<Button>*`, `Button*` and `void<Button*>` classify the same way.

Adding pointee const to a pointer to the same carrier is implicit, just as for
other nominal storage: `long<Color>*` converts to `const long<Color>*`.
This preserves the carrier and payload identity and does not relax nested
pointer qualification rules or permit removing const.

| Conversion | View | Result |
|---|---|---|
| `void<Button>*` to `void<Widget*>` | `Button*` to `Widget*` | implicit |
| `void<Button>*` to `Button*` | `Button*` to `Button*` | implicit |
| `long<ColorEnum>` to `BackgroundEnum` | `ColorEnum` to `BackgroundEnum` | explicit |
| `nint<Widget*>` to `Button*` | `Widget*` to `Button*` | unsafe explicit |
| `long<Color>*` to `Color*` or `long*` | `LongCarrierOfColor*` to `Color*` or `long*` | explicit, as other value-newtype pointers |
| `fn void(long<Color>)` to `fn void(Color)` | `LongCarrierOfColor` against `Color` | `fn*` fence |

Initializers and literals are checked against the carried type, so
`int<byte> b = 300;` is out of range for `byte` and `long<Color> c = RED;` finds
`RED` through `Color`. Overload selection uses the view: a `long<Color>`
argument selects the `Color` member of an overload family.

An expandable `void<V>` is not stripped; it converts with `V` by reading and
writing its single storage object.

The view applies to reads and conversions only. Two carrier operands of `==`
or `!=` are compared as carriers, before the view could strip them, so the
comparison covers every bit of the stored integer. Eligible payloads, the
encoding of a payload in its carrier (including bit-preserving `float` and
`double` transport) and the operations a carrier offers are specified in
[Generics, Erasure, And Capabilities](06-generics-erasure-and-capabilities.md).

## Value Conversion Versus Type Rewrite

Implicit scalar conversions also apply to `in` arguments. When the source and
parameter scalar types differ, the call supplies a converted temporary with the
parameter's storage type. Forwarding the original storage is valid only when
it already has the required scalar type.

A target may define a value conversion such as `_near` data pointer to `_far`
data pointer. That conversion applies at a value boundary:

```camp
void consume(const byte* _far data);

byte* _near local;
consume(local);
```

It does not tunnel through type constructors:

| Source | Target | Rule |
|---|---|---|
| `byte* _near` | `byte* _far` | Target-defined value conversion. |
| `(byte* _near)[]` | `(byte* _far)[]` | Reconstruct the array. |
| `delegate void(byte* _near)` | `delegate void(byte* _far)` | Reconstruct or use an explicit callable value. |
| `fn void(byte* _near)` | `fn void(byte* _far)` | Callable casts do not insert argument conversions. |
| `Box<byte* _near>*` | `Box<byte* _far>*` | Generic arguments are invariant unless class rules say otherwise. |
| `(byte* _near)?` | `(byte* _far)?` | The optional's own conversion: `.value` converts as `byte* _near` to `byte* _far`; `.specified` is carried. |

This invariant is critical for ABI correctness. A direct conversion of the outer
value must not secretly rewrite element types, generic arguments, callable
signatures, or hidden context contracts.

## Target Specs And Call Specs

Targets define type specs and call specs separately:

| Spec kind | Applies to | Examples |
|---|---|---|
| type spec | written natural integers, strings, pointers, dynamic arrays, optionals and callable carriers | `_near`, `_far`, `_huge`, `_rom` |
| call spec | concrete callables | `_cdecl`, `_stdcall`, `_sysv` |

`fn*` may carry a target type spec because function pointer domains may differ.
It may not carry a call spec because it has no concrete callable signature.

`void*`, `nint`, and `nuint` may carry target type specs where the target allows
them. `untyped` may not carry a type spec or call spec.

Typespecs follow their carrier; a concrete callable's typespec follows its
parameter list: `fn _pascal nint() _far`. The callspec follows the callable
keyword. Eligibility is determined by the written carrier:

| Written carrier | Typespec allowed |
| --- | --- |
| `nint`, `nuint`, `string`, `wstring`, `astring` | Yes |
| `T*`, including `void*` | Yes, on that pointer layer |
| `T[]`, `T?` | Yes, on that wrapper |
| Concrete `fn`, `delegate`, `async`, `once` | Yes, after its parameter list |
| Raw `fn*` | Yes, after `*`; no callspec |
| Generic instantiation itself, fixed array itself | No |
| `untyped`, other primitives | No |
| Plain named class, struct, enum, newtype, generic parameter or type alias | No |

A named type's underlying representation does not grant eligibility. Eligible
wrappers have independent slots, as in `byte* _far * _near`, including wrappers
around named types. Repeating even the same spec in one slot is an error.
These syntax rules do not change target
conversion ordering, signature invariance, ABI-slot compatibility or cast rules.

## Raw Carrier Families

Camp has these raw carrier concepts:

| Carrier | Erases | Still remembers |
|---|---|---|
| `void*` | data pointee type/category | data pointer family, target domain, physical depth, qualifiers unless discarded |
| `fn*` | concrete callable signature and call spec | function pointer family and target domain |
| `nint` / `nuint` | data pointer type information through integer conversion | integer signedness and target natural integer domain |
| `untyped` | scalar carrier family and type information | only raw carrier capacity |

Raw carriers are not interchangeable. `void*` is not a function pointer carrier.
`fn*` is not callable. `nint`/`nuint` are ordinary integers after conversion.
`untyped` is the universal raw scalar fence.

## `void*` Fences

`void*` is the raw carrier for data pointer values. It erases the pointee type
and data pointer family, but remains a data pointer.

A `void*` fence preserves physical pointer depth:

```camp
byte* bytes;
PacketHeader* header = (PacketHeader*)(void*)bytes;

Widget** source;
Other** target = (Other**)(void**)source;
```

Converting through `void*` does not automatically permit:

- removing `const` or `volatile`;
- changing physical pointer depth;
- crossing target domains the target declares incompatible;
- converting a data pointer into a function pointer.

Those remain unsafe, fence-required, or forbidden according to classifier rules.

## `fn*` Fences

`fn*` is the raw carrier for function pointer values:

```camp
fn int(int) parser;
fn* raw = (fn*)parser;
fn short(short) other = (fn short(short))raw;
```

`fn*` erases result type, parameter types, callable newtype identity, call spec,
and callable lifetime annotations. It is not callable until recovered to a
concrete function type.

Function pointer target domains remain target-defined. Recovering from
`fn* _near` to a `_far` concrete function type requires a target-defined
conversion or a stronger raw escape.

## Implicit Numeric Conversions

Implicit integer conversions preserve every value of the source type. Signed
integers never implicitly become unsigned; unsigned integers require a wider
signed destination. The supported integer widenings are:

| Source | Destinations |
|---|---|
| `sbyte` | `short`, `int`, `long`, `nint` |
| `byte` | `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `nint`, `nuint` |
| `short` | `int`, `long`, `nint` |
| `ushort` | `int`, `uint`, `long`, `ulong`, `nint`, `nuint` |
| `int` | `long`, `nint` |
| `uint` | `long`, `ulong`, `nuint` |
| `nint` | `long` |
| `nuint` | `ulong` |

Natural integers are treated as between 32 and 64 bits for this conversion
policy. Integer values do not implicitly become character-code types. Character
values may widen to wider character representations or suitable integer types;
in particular, `char` widens to `wchar`, and `wchar` converts to `ushort` and
wider integers, but not `short` or `char`. Every integer type converts to
`float` or `double`, and `float` converts to `double`. Other numeric conversions
require an explicit cast. Unsuffixed literals retain their target-typing rules.
Comparisons with a fitting unsuffixed integer literal supply the other numeric
operand's type to that literal, so expressions such as `count == 0` remain
valid for an unsigned `count`.

## `nint` And `nuint`

`nint` and `nuint` are natural integer carriers for data pointer values:

```camp
Widget* pointer;
nuint bits = (nuint)pointer;
Widget* again = (Widget*)bits;
```

Converting a data pointer to a natural integer discards pointer type, constness,
volatile qualifiers, and lifetime information as part of the explicit scalar
conversion. Recovery uses the target type's written qualifiers and lifetime
facts.

As integer-carrier representations (`nint<T*>`, `nuint<T*>`), `nint` and `nuint`
are currently the only supported carriers for data pointers; this is a provisional
policy, not a settled limit (see semantics 06).

Function pointers do not portably convert through `nint` or `nuint`. Use `fn*`
for function-pointer erasure or `untyped` for a universal raw scalar escape.

## `untyped`

`untyped` is the universal raw scalar carrier. It can carry data pointers,
function pointers, `void*`, `fn*`, natural integers, and scalar values that fit
the target carrier.

Once a value is converted to `untyped`, source type information is gone.
Recovery to a representable scalar, data pointer, or function pointer type is
ordinary explicit recovery. `untyped` recovery does not require a separate
unsafe marker merely because constness, lifetime, pointer depth, target domain,
or callable signature differs; the raw escape happened at the conversion to
`untyped`.

`untyped*` is a pointer to storage whose element type is `untyped`. It is not the
universal fence. The fence type is `untyped` itself.

## Const And Volatile Overrides

Adding `const` or `volatile` is implicit when the rest of the conversion is
valid. Removing either qualifier requires `unsafe` for typed pointer conversions
and for `void*`/`fn*`-based recoveries:

```camp
const Widget* source;
const void* raw = (const void*)source;
Widget* target = (unsafe Widget*)raw;
```

Conversions through `nint`, `nuint`, or `untyped` discard the qualifier as part
of scalar erasure. Recovery from those carriers does not need an additional
unsafe marker solely for qualifier removal.

## Lifetime Overrides

Data pointer lifetime changes are lifetime casts, not runtime conversions:

```camp
Widget* pointer;
Widget* retained = (escaped Widget*)pointer;
```

The cast is the only place a lifetime is written on a local value. A local
variable's type carries no `scoped`, `unscoped(...)` or `escaped` annotation,
wherever the local is declared: ordinary declarations, `auto` locals, `for`
initializers, `foreach` variables, `out` declarations and `catch` declarations.
The lifetime of a local is inferred from the values stored in it.

Callable lifetime changes are more restrictive because callable lifetimes may
describe hidden context, receiver lifetime, result provenance, and parameter
relationships. Direct casts between concrete callable types that change callable
lifetime annotations require unsafe unless a raw function fence erases the
signature first.

## Physical Pointer Depth

Changing physical pointer depth is at least unsafe:

```camp
Widget* one;
Widget** two = (unsafe Widget**)one;

void* raw;
Widget** recovered = (unsafe Widget**)raw;
```

This applies to typed data pointer conversions and `void*` fences. It does not
apply to `nint`, `nuint`, or `untyped` recovery because those carriers already
erase pointer shape.

## Data Pointer Families

Same-depth data pointer casts are classified by pointer family:

| Family | Examples | Direct rule |
|---|---|---|
| primitive/value-newtype pointers | `int*`, `byte*`, `UserId*` | Explicit within family, subject to qualifiers and target policy. |
| struct pointers | `Header*`, `Packet*` | Explicit within family, subject to qualifiers and target policy. |
| class pointers | `Control*`, `List<int>*` | Class hierarchy and common-base rules. |
| interface pointers | `Readable*`, `Writable*` | Interface representation and vtable rules. |
| `void*` matching depth | `void*`, `void**` | Data-pointer fence and recovery. |

Cross-family casts require a matching-depth `void*` fence unless a more specific
language conversion applies.

## Class Pointer Casts

A pointer to a class implicitly converts to an exact constructed base class
pointer when the target pointer carrier conversion is also implicit:

```camp
Button* button;
Control* control = button;
```

Generic constructed class types are invariant. `Box<int>*` is not a
`Box<uint>*`. Walking a derived class's base chain substitutes the actual type
arguments at every step; deriving from `Box<int>` does not permit conversion to
`Box<uint>*`. Downcasts and casts across a common class base require unsafe when
the class relationship exists. If no class relationship exists, use a raw fence
when target policy permits it.

Inherited method receivers use the same exact base construction as pointer
conversions. A `Derived: Base<int>` receiver calls `Base<T>` members as members
of `Base<int>`, including explicit receivers, property accessors, bound method
references, and calls through `this` inside the derived class. Resolve each
base construction along the inheritance chain before substituting owner type
parameters in argument and result types; the derived class's own argument list
is not necessarily the declaring base's argument list. Receiver constness and
pointer depth still constrain the call.

Hidden generic capabilities do not change cast category. They affect whether the
resulting program is semantically correct, not which cast syntax is accepted.

## Interface Pointer Casts

An interface pointer has an interface-instance slot shape. Source `I*` is
physically comparable to a pointer to an interface slot pointer. This is why
interface casts must preserve the distinction between:

- class or struct pointer to interface conversion;
- interface-to-interface widening;
- interface narrowing or side-casting;
- raw pointer reinterpretation through a matching-depth fence.

`vtableof(T: I)` is neither `I` nor `I*`; it is a witness whose unbound
ordinary slots require `T*`. A raw pointer cast cannot convert a concrete
receiver into the valid interface carrier needed by an explicit `I` table
slot, nor can it satisfy the witness slot's source receiver check.

A non-const class pointer implicitly converts to an implemented interface
pointer. The compiler produces the correct interface-instance slot pointer; it
is not merely an object pointer reinterpretation.

A non-const struct pointer implicitly converts to an implemented interface
pointer by creating an indirect interface carrier whose context points at the
struct storage. A non-const addressable struct lvalue can also convert this way;
the carrier context points at the lvalue's address.

Const class pointers, const struct pointers, const struct lvalues, and struct
rvalues do not implicitly convert to interface pointers in v1. Interface slots
may still declare `const this` receivers as part of their callable contract, but
Camp does not yet have a const interface pointer/view feature.

Struct-to-interface conversion may require an indirect temporary carrying a
vtable pointer and context pointer. That temporary must obey lifetime rules.

## Callable Conversions

Concrete callable conversions must preserve compatible callable shape:

- callable kind;
- target type spec and call spec;
- return and parameter slots;
- callable `this` contract;
- `constof` variance where applicable;
- lifetime rules;
- thrown slots and async completion shape.

`fn*` fences erase concrete function signature. They do not make delegates,
once values, async callable values, or iterator protocol values directly
convertible without reconstruction, because those are multi-component values.

Callable newtypes preserve nominal boundaries. A callable shape may be
structurally compatible while the named newtype still requires an ascription,
cast, or source rule to cross the nominal boundary.

## Expanded And Generic Types

Expanded forms are multi-component values. A cast cannot change their component
shape. Reconstruct or materialize them instead.

Generic constructed types are invariant by default. The classifier must not
rewrite a constructed type merely because a type argument has a value
conversion. This applies to arrays, optionals, delegates, class instantiations,
and generic interface uses.

### Prepared Result Conversion Boundary

An invocation that omits a prep buffer first has the substituted mutable prep
array as its intrinsic expression type. `auto` therefore infers that array type.
Only afterward does ordinary target conversion apply. Conversions to const
array views, primitive string families, fixed storage, or another accepted text
target use the existing conversion classifier; prep does not create a separate
conversion category.

Any terminator required by an existing array-to-string-family conversion is
additional storage owned by that conversion. It is not part of the prep
method's logical required length unless the declaration's own contract says the
terminator is logical content. Checked terminator addition and storage
arithmetic occur before allocation.

## Target Conversion Policy

Targets provide conversion policy by carrier:

- data pointers;
- function pointers;
- natural integers;
- ABI slots.

Value carriers accept `implicit`, `explicit`, `unsafe`, `fence`, and
`forbidden`. ABI-slot compatibility accepts `compatible` and `forbidden`.

Compiler writers should classify in this order:

1. exact same source/resolved type;
2. source-language structural rules such as class/interface relationships;
3. overriding qualifier/lifetime/depth rules;
4. target type-spec carrier policy;
5. raw-carrier fence rules;
6. reconstruct/forbidden result.

The diagnostic should name the required source action: add an explicit cast, add
`unsafe`, use a raw fence, reconstruct the value, or change the API.

## Warnings

Warnings are appropriate only where the classifier explicitly accepts a
conversion and wants to report unnecessary or risky syntax. For example, an
`unsafe` marker on an ordinary explicit conversion can warn that unsafe is not
required.

Do not downgrade a forbidden conversion to a warning path merely to make code
emit C.

## Test Matrix

Conversion work should cover:

- implicit, explicit, unsafe, fence, reconstruct, and forbidden diagnostics;
- qualifier addition/removal;
- lifetime casts and callable lifetime casts;
- `void*`, `fn*`, `nint`, `nuint`, and `untyped`;
- target type-spec policy on data pointers, function pointers, natural
  integers, and ABI slots;
- class and interface pointer conversions;
- generic invariance;
- expanded forms and materialized storage;
- generated C on at least one target with type specs when target behavior is
  involved.
