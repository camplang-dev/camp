# `constof` And Signature Compatibility

`constof(anchor)` expresses dependent constness: a type slot is const exactly
when a named anchor argument or receiver is const at the call site. It is a
source-level contract. C emission erases it to ordinary const where needed, but
semantic analysis and metadata must preserve the relationship.

## Terms

Use these terms consistently:

| Term | Meaning |
|---|---|
| ordinary const | A `const` qualifier in a specific type slot. |
| dependent const | `constof(anchor)` in a specific type slot. |
| mutable slot | The same type slot with no `const` or `constof`. |
| anchor | A parameter or receiver whose call-site constness drives substitution. |
| target signature | The required callable/interface/newtype/lambda signature. |
| candidate signature | The implementation or source callable being checked. |
| input position | A value consumed by the callable. |
| output position | A value produced by the callable. |

`thrown` slots are not ordinary output positions for `constof` variance.

## Anchor Binding

`constof(anchor)` binds `anchor` in the current signature scope. Valid anchors
include parameters and receivers where the source grammar permits them.

During declaration binding:

- anchors must resolve before body analysis;
- anchor names in lambda bodies must name lambda parameters, not captured outer
  variables;
- metadata should preserve the source anchor spelling;
- diagnostics should identify the unresolved or invalid anchor.

For receiver relationships, `constof(this)` binds to the receiver or callable
`this` parameter. A method with no receiver cannot use `constof(this)`.

## Caller-Visible Substitution

At a call site, each anchor has a caller-visible const fact. For a parameter:

```camp
void share<T: any>(const T[] source, constof(source) T[] view, sizeof(T));
```

the actual for `view` must match the actual constness of `source` in the
affected slot:

```camp
T[] mutableItems;
const T[] constItems;

share(mutableItems, mutableItems); // OK
share(constItems, constItems);     // OK
share(mutableItems, constItems);   // error
share(constItems, mutableItems);   // error
```

This equality check is distinct from ordinary assignment/storage conversion. Do
not replace it with a simple "can convert to target type" check.

## Callee View

Inside the callee, `constof` is a relationship, not a new mutable qualifier. The
implementation may use values according to the declared contract, but it cannot
assume a mutable view merely because some callers pass mutable values.

When lowering erases `constof` to ordinary C constness, preserve the source fact
for:

- call-site substitution;
- return and `out` checking;
- callable compatibility;
- metadata and API output;
- diagnostics.

## Storage Conversion Lattice

Storage/result conversion into a slot affected by ordinary const or
`constof(anchor)` follows this slot-specific lattice:

| Source slot | Target slot | Implicit? |
|---|---:|---:|
| mutable | ordinary `const` | yes |
| mutable | `constof(anchor)` | yes |
| `constof(anchor)` | ordinary `const` | yes |
| ordinary `const` | `constof(anchor)` | no |
| `constof(anchor)` | mutable | no |
| `constof(anchorA)` | `constof(anchorB)` | yes only for the same bound anchor |

The `mutable -> constof(anchor)` case is accepted because storing a mutable
value into a dependent-const result is safe: if the caller later sees a mutable
view, the value is actually mutable. The reverse is narrowing and requires an
explicit assertion.

Examples:

```camp
constof(source) byte* dependent = mutablePointer;
const byte* widened = dependent;

constof(source) byte* narrowed = (constof(source) byte*)constPointer;
byte* raw = (unsafe byte*)dependent;
```

## Common Type Inference

When computing an untargeted common type, prefer the safe widened result:

```camp
auto value = condition ? dependentPointer : constPointer;
```

The common type should be ordinary `const`, not dependent const, unless a target
type or explicit cast supplies the dependent relationship.

## Parameter Passing Equality

For a non-output parameter declared with `constof(anchor)`, the caller's actual
must have the same static constness as the anchor actual in the corresponding
slot before ordinary parameter conversion.

This rule applies to ordinary calls, method calls, property setters, indexers,
target-typed lambdas, callable values, interface calls, and async visible
parameters after source-level callable shape normalization.

## Return And `out` Results

Return values and `out` payloads are output positions. After call-site
substitution, they produce the caller-visible type.

```camp
constof(source) byte* first(byte[] source);

byte[] mutableBytes;
const byte[] constBytes;

byte* mutableFirst = first(mutableBytes);
const byte* constFirst = first(constBytes);
```

Explicit `out` targets are checked after substitution:

```camp
void first(byte[] source, out constof(source) byte* result);

byte* mutableResult;
const byte* constResult;

first(mutableBytes, out mutableResult); // OK
first(mutableBytes, out constResult);   // OK by widening
first(constBytes, out constResult);     // OK
first(constBytes, out mutableResult);   // error
```

An omitted trailing `out` binding should infer the substituted result type, not
an unresolved `constof(...)` type.

## Callable Variance

Callable and signature compatibility applies `constof` variance:

- output positions are covariant;
- input positions are contravariant;
- anchor identities are compared by corresponding parameter/receiver position,
  not raw source spelling alone.

Output covariance permits a candidate to produce a more precise dependent result
than the target promises:

```camp
newtype fn const byte* ConstGetter(const byte[] source);

constof(source) byte* first(const byte[] source) : ConstGetter;
```

Input contravariance permits a candidate to accept ordinary const where the
target imposes a dependent-const equality relation:

```camp
newtype fn void DepConsumer(
	const byte[] source,
	constof(source) byte[] other);

void consumeConst(const byte[] source, const byte[] other) : DepConsumer;
```

The reverse directions are not implicitly compatible because they either
promise a result the candidate cannot supply or impose an extra input relation
the target did not require.

## Surfaces That Use Callable Compatibility

Apply these rules to:

- callable newtype ascription;
- assignment or conversion of `fn`, `delegate`, `once`, `iter`, and `async`
  values;
- anonymous callable type compatibility;
- target-typed lambdas;
- interface implementation matching;
- interface default method target matching;
- property getter/setter function compatibility where the function is treated as
  a callable surface.

Do not apply them blindly to every type comparison. Ordinary storage conversion
and call-site dependent-const equality have separate rules.

## Overload Selector Shape

When a callable surface participates in overload-family compatibility, the
overload selector shape is part of the source callable contract. A compatible
candidate must preserve:

- whether the declaration is an overload-family entry;
- the single selector parameter name;
- the selector callable-parameter position;
- the source parameter shape before the selector.

The pre-selector shape includes names, modifiers, resolved source types, and
calling-semantics attributes such as `@index` and `@range`. This lets a family
share stable keys or indexes before the selector while keeping the selector's
meaning identical for interface implementation, virtual override matching,
callable newtype ascription, metadata, and language-service display.

Callable compatibility may still apply its ordinary variance and `constof`
rules to compatible parameter slots, but it must not move the overload selector
or silently match an ordinary declaration against an overload-family entry.

For a prep-bearing overload family, the selector must precede the prep slot.
The modifiers are mutually exclusive and selectors cannot default, while every
post-prep slot must be defaultable or compiler-supplied. Call analysis therefore
selects the concrete entry and completes generic and `constof` substitution
against the declaration before deciding whether omission of prep transforms
the invocation.

Prep is part of source callable compatibility. A prep mutable-array slot may
satisfy the corresponding ordinary mutable-array slot because the target may
ignore the stronger contract; the reverse requires an unsafe conversion.
Method references retain this declaration-shaped scalar result and prep slot.
When an invocation transforms, the same resolved `constof` substitutions and
anchor relationships are reused for both generated protocol calls.

## Virtual Overrides Remain Exact

Virtual and abstract override matching is exact with respect to ordinary const
versus `constof(anchor)`. Do not apply callable variance to override validation.

Exactness means:

- ordinary `const` matches ordinary `const`;
- `constof(anchor)` matches `constof(anchor)`;
- anchor correspondence is positional, so parameter renaming can be accepted
  only when the same positional relationship is preserved;
- overload selector presence, position, selector name, and pre-selector shape
  are preserved;
- source and ABI vtable slot shape remain stable.

This is necessary because override slots are ABI slots. A derived override must
not silently narrow or widen the virtual contract.

## Lambda Target Typing

A target-typed lambda inherits `constof` expectations from its target callable
shape. Lambda parameter names become valid anchors inside the lambda body.

```camp
fn constof(input) byte*(const byte[] input) selector =
	(input) => (constof(input) byte*)input.elements;
```

If the lambda is inferred without a target, the inferred callable type must be
compatible with the eventual target. Diagnostics should point either to the
lambda signature/body anchor use or to the assignment/call site that supplied an
incompatible target.

## Callable `this`

Context-carrying callable types may have a callable `this` parameter. A
`constof(this)` relationship in that callable type binds to the hidden context
receiver. Lowering must preserve the relationship through delegate/context
expansion and bound method references.

When forming a bound method reference, receiver constness contributes the
call-site anchor fact. If the target callable requires a `constof(this)` result,
the bound receiver determines whether callers see mutable or const output.

## Metadata And API Output

Metadata should preserve source spelling:

```json
{
  "returnType": "constof(source) byte*"
}
```

C and ABI output may erase `constof(anchor)` to ordinary `const`, but API headers
and metadata should describe the Camp source contract. Consumers comparing
metadata signatures should resolve anchors positionally.

## Diagnostics

Diagnostics should state:

- which anchor is involved;
- whether the failure is call-site equality, storage narrowing, callable
  compatibility, lambda target typing, interface matching, or override exactness;
- which slot lost or over-constrained constness;
- when an explicit cast or unsafe cast is required.

Avoid messages that merely say two long type strings differ when the actionable
issue is a dependent-const relationship.

## Test Expectations

`constof` changes need coverage for:

- anchor binding and unresolved anchors;
- call-site equality for ordinary parameters and receivers;
- storage conversion lattice;
- return and explicit/omitted `out` results;
- callable newtype ascription;
- target-typed lambdas;
- interface implementation and default slot targets;
- virtual override exactness;
- metadata source spelling;
- C/API erasure where applicable.
