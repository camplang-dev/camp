# Camp `constof(anchor)` Supplement: Conversions, Signature Compatibility, and Lambdas

Status: supplement to the implemented `constof(anchor)` feature  
Audience: LLM implementation agent working in the Camp compiler  
Scope: this document covers conversion, callable/signature compatibility, lambda inference, anchor name binding, and related tests. It intentionally does not restate the base `constof(anchor)` proposal except where needed to disambiguate these rules.

## 1. Implementation policy

For every rule below:

1. Add positive and negative test coverage unless equivalent checked-in tests already exist.
2. If the new tests already pass, no implementation change is required for that rule.
3. If a test fails, implement the smallest compiler change needed to satisfy the rule.
4. Do not silently weaken existing non-`constof` rules, nominal type rules, lifetime rules, or ABI-shape rules.
5. Keep explicit unsafe casts permissive as described in this supplement. Unsafe casts will be reviewed as a separate future effort.

## 2. Terms

This supplement uses these terms:

- **Ordinary const**: the existing `const` qualifier in a specific type slot.
- **Dependent const**: `constof(anchor)` in a specific type slot.
- **Mutable slot**: the same type slot with no `const` or `constof` qualifier.
- **Target signature**: the required signature, such as a callable storage type, callable `newtype`, interface method contract, or lambda target type.
- **Candidate signature**: the implementation/source signature being checked against the target.
- **Input position**: a value consumed by the callable, including ordinary parameters, `in` parameters, receiver or callable `this`, property setter value parameters, and property/indexer input parameters.
- **Output position**: a value produced by the callable, including ordinary return values, `out` payloads, property getter results, async completion non-`thrown` results, and iterator yield/current slots.

A `thrown` slot is not an ordinary output position for these compatibility rules.

## 3. Assignment and storage conversions

Apply these rules when converting an expression into a storage/result location whose type contains a const slot affected by ordinary `const` or `constof(anchor)`. This includes locals, fields, explicit `out` targets, `out` assignments inside the callee, return expressions, conditional/common-type target conversions, and materialized storage/component assignments.

The conversion classification is slot-specific. All other type compatibility checks still apply.

| Source slot | Target slot | Implicit? | Meaning |
|---|---:|---:|---|
| mutable | ordinary `const` | yes | ordinary mutable-to-const widening |
| mutable | `constof(anchor)` | yes | safe produced-value assignment; caller may later see mutable only because the value is actually mutable |
| `constof(anchor)` | ordinary `const` | yes | widening; the caller only receives a const view |
| ordinary `const` | `constof(anchor)` | no | narrowing; explicit cast required |
| `constof(anchor)` | mutable | no | narrowing; explicit cast required |
| `constof(anchorA)` | `constof(anchorB)` | yes only when the bound anchor identity is the same | otherwise explicit cast required |

Examples:

```camp
void demo<T: any>(const T[] source, sizeof(T))
{
	T* mutablePtr = getMutablePointer();
	const T* constPtr = getConstPointer();

	constof(source) T* a = mutablePtr;                 // OK
	const T* b = a;                                    // OK

	constof(source) T* c = constPtr;                   // ERROR: const -> constof is narrowing
	constof(source) T* d = (constof(source) T*)constPtr; // OK: explicit assertion

	T* e = a;                                          // ERROR: constof -> mutable is narrowing
	T* f = (T*)a;                                      // OK only as explicit unsafe cast
}
```

When computing an un-targeted common type, prefer the safe widened result:

```camp
auto x = condition ? dependentPtr : constPtr;
// infer ordinary const, not constof
```

A dependent result in such an expression requires an explicit target or explicit cast:

```camp
constof(source) T* x = condition
	? dependentPtr
	: (constof(source) T*)constPtr;
```

## 4. `constof` parameter passing is not ordinary storage conversion

The base feature's equality rule for non-output `constof(anchor)` parameters remains distinct from storage assignment conversion.

For an input parameter declared with `constof(anchor)`, the caller must supply an actual whose static constness in the affected slot equals the static constness of the actual supplied for `anchor`, before ordinary parameter conversion.

```camp
void use<T: any>(
	const T[] source,
	constof(source) T[] other,
	sizeof(T));

T[] mutableA;
T[] mutableB;
const T[] constA;
const T[] constB;

use(mutableA, mutableB); // OK
use(constA, constB);     // OK
use(mutableA, constB);   // ERROR
use(constA, mutableB);   // ERROR
```

Do not replace this equality check with the storage-conversion lattice from section 3.

## 5. Signature compatibility: const/constof variance

Apply these rules to callable/signature compatibility for:

- interface method implementation;
- callable `newtype` ascription;
- assigning function, method, `fn`, `delegate`, `once`, `iter`, `async`, or `async iter` values to callable storage;
- target-typed lambdas;
- anonymous callable type compatibility.

Do not apply these relaxed rules to virtual override compatibility; see section 6.

### 5.1 Output positions are covariant

In an output position, a candidate may produce a more precise dependent-const result when the target only promises ordinary const.

| Target output | Candidate output | Compatible? |
|---|---:|---:|
| ordinary `const` | `constof(anchor)` | yes |
| `constof(anchor)` | ordinary `const` | no, unless an explicit unsafe callable cast is used |
| `constof(anchorA)` | `constof(anchorB)` | yes only when anchors correspond after parameter-position mapping |

Example:

```camp
newtype fn const T* ConstGetter<T: any>(const T[] source, sizeof(T));

constof(source) T* getInterior<T: any>(const T[] source, sizeof(T))
	: ConstGetter<T>
{
	return (constof(source) T*)source.elements;
}
```

The ascription is compatible because callers through `ConstGetter` only see `const T*`.

The reverse direction is not implicitly compatible:

```camp
newtype fn constof(source) T* DepGetter<T: any>(const T[] source, sizeof(T));

const T* getConstOnly<T: any>(const T[] source, sizeof(T))
	: DepGetter<T>; // ERROR: candidate cannot satisfy mutable-call result promise
```

### 5.2 Input positions are contravariant

In an input position, a candidate may accept ordinary const where the target requires `constof(anchor)`. The caller through the target still obeys the dependent-const equality requirement, while the implementation only needs a const view.

| Target input | Candidate input | Compatible? |
|---|---:|---:|
| `constof(anchor)` | ordinary `const` | yes |
| ordinary `const` | `constof(anchor)` | no, unless an explicit unsafe callable cast is used |
| `constof(anchorA)` | `constof(anchorB)` | yes only when anchors correspond after parameter-position mapping |

Example:

```camp
newtype fn void DepConsumer<T: any>(
	const T[] source,
	constof(source) T[] other,
	sizeof(T));

void consumeConst<T: any>(
	const T[] source,
	const T[] other,
	sizeof(T))
	: DepConsumer<T>
{
}
```

The ascription is compatible because the implementation accepts all calls that the target permits.

The reverse direction is not implicitly compatible:

```camp
newtype fn void ConstConsumer<T: any>(
	const T[] source,
	const T[] other,
	sizeof(T));

void consumeDependent<T: any>(
	const T[] source,
	constof(source) T[] other,
	sizeof(T))
	: ConstConsumer<T>; // ERROR: candidate imposes extra caller relation
```

### 5.3 `out` payloads

For signature compatibility, an `out` parameter's payload type is an output position.

```camp
newtype fn void ConstOut<T: any>(
	const T[] source,
	out const T* result,
	sizeof(T));

void first<T: any>(
	const T[] source,
	out constof(source) T* result,
	sizeof(T))
	: ConstOut<T>
{
	result = (constof(source) T*)source.elements;
}
```

This is compatible because callers through `ConstOut` receive only `const T*`.

At a call site, explicit `out` storage is checked separately after caller-visible `constof` substitution:

```camp
void first<T: any>(
	const T[] source,
	out constof(source) T* result,
	sizeof(T));

T[] mutableItems;
const T[] constItems;
T* p;
const T* q;

first(mutableItems, out p); // OK
first(mutableItems, out q); // OK: produced T* widens to const T*
first(constItems, out q);   // OK
first(constItems, out p);   // ERROR
```

Omitted trailing `out` binding infers the substituted result type, not the unresolved `constof(...)` form.

### 5.4 Special output/input surfaces

Apply the same input/output classification after ordinary language rewrites:

- property getter results are output positions;
- property setter values and index arguments are input positions;
- async completion non-`thrown` result parameters are output positions;
- iterator yielded/current slots are output positions;
- ordinary async/iterator parameters are input positions;
- `thrown` parameters and `catch` values are excluded from these `constof` variance rules.

## 6. Virtual overrides remain exact

Virtual/abstract override compatibility must remain exact with respect to ordinary `const` versus `constof(anchor)` qualifiers.

Do not apply the section 5 variance rules to `override` or `sealed override` validation.

Exactness means:

- ordinary `const` must match ordinary `const`;
- `constof(anchor)` must match `constof(anchor)`;
- source and base anchors are compared by corresponding parameter identity, not raw spelling;
- parameter names may differ only if each `constof(...)` still binds to the corresponding parameter position in the base slot.

Examples:

```camp
abstract class Base<T: any>
{
	abstract const T* get(const T[] source, sizeof(T));
}

class Bad<T: any>: Base<T>
{
	override constof(source) T* get(const T[] source, sizeof(T)); // ERROR
}
```

```camp
abstract class DepBase<T: any>
{
	abstract constof(source) T* get(const T[] source, sizeof(T));
}

class Bad2<T: any>: DepBase<T>
{
	override const T* get(const T[] source, sizeof(T)); // ERROR
}
```

The reason is operational simplicity: an override body must be able to call the base implementation without adding casts solely because override compatibility used variance.

## 7. Explicit callable signature casts

Explicit casts between callable signatures are allowed for now without `constof`-specific compatibility checks.

This includes unsafe directions such as:

- candidate output ordinary `const` cast to target output `constof(anchor)`;
- candidate input `constof(anchor)` cast to target input ordinary `const`;
- casts where `constof` anchors do not correspond;
- casts between callable signatures where the only incompatibility is ordinary const versus `constof` variance.

The target callable type must still parse and bind as a valid type. Existing non-`constof` restrictions that the compiler already enforces for explicit casts may remain. Do not add new `constof` safety checks to explicit callable casts in this work item.

The cast must not generate wrappers, thunks, allocations, or ABI changes.

Example:

```camp
fn constof(source) T*(const T[] source, sizeof(T)) dep =
	(fn constof(source) T*(const T[] source, sizeof(T)))getConstOnly;
```

This is intentionally unsafe and permitted for now.

## 8. Lambda target typing and inference

### 8.1 Target-typed lambda parameters

When a lambda has a target callable type and a parameter omits its type, infer the full target parameter type, including ordinary `const` and `constof(...)` qualifiers.

```camp
newtype fn void DepConsumer<T: any>(
	const T[] source,
	constof(source) T[] other,
	sizeof(T));

DepConsumer<byte> f = (source, other) =>
{
	// source: const byte[]
	// other: constof(source) byte[] in the lambda signature
	// body view remains const
};
```

When a lambda parameter explicitly writes a type, the compiler must use the written type as the candidate signature. Do not inject missing `const` or `constof` into an explicitly typed lambda parameter.

The explicit type is then checked against the target using the section 5 compatibility rules:

```camp
DepConsumer<byte> ok = (
	const byte[] source,
	const byte[] other) =>
{
}; // OK: candidate input const can satisfy target input constof(source)

DepConsumer<byte> bad = (
	const byte[] source,
	byte[] other) =>
{
}; // ERROR: explicit mutable other cannot satisfy target constof(source)
```

### 8.2 Target-typed lambda returns

When a lambda is target-typed, return expressions are checked against the target output type. A target return or `out` payload containing `constof(anchor)` uses the same provenance-or-explicit-cast rule as an ordinary function body.

```camp
newtype fn constof(source) T* Finder<T: any>(const T[] source, sizeof(T));

Finder<byte> f = (source) =>
{
	return (constof(source) byte*)source.elements;
};
```

### 8.3 `auto` lambda inference

When a lambda is assigned to `auto` and the compiler infers a `fn` or `delegate` type, infer `constof(...)` only from explicit spelling in the lambda itself.

```camp
auto f = (
	const byte[] source,
	constof(source) byte[] other) =>
{
	return 0;
}; // inferred callable parameter type contains constof(source)
```

Do not infer `constof(...)` from body provenance alone.

For an `auto` lambda whose intended return type contains `constof(anchor)`, at least one return expression must explicitly cast to the intended dependent type, and all return paths must be compatible with that inferred type.

```camp
auto first = (const byte[] source) =>
{
	return (constof(source) byte*)source.elements;
};
```

Likewise, if an `auto` lambda is intended to infer an ordinary const return from a mutable expression, the return expression should be explicitly cast to the ordinary const type rather than relying on target typing that does not exist.

```camp
auto asConst = (byte* value) =>
{
	return (const byte*)value;
};
```

## 9. `constof` anchor name binding

Bind every `constof(anchor)` to a parameter identity before comparing signatures. Do not compare raw anchor identifier text across signatures.

### 9.1 Anonymous callable and callable newtype compatibility

In an anonymous callable type or lambda-inferred callable type, `constof(name)` refers to that callable type's own declared parameter named `name`.

When comparing to a callable `newtype` or storage type whose parameter names differ, compare the bound anchor positions.

```camp
newtype fn constof(source) byte* Getter(
	const byte[] source);

fn constof(items) byte*(const byte[] items) localGetter;

Getter g = localGetter; // OK: both anchors bind to parameter #0
```

### 9.2 Lambda body scope

Inside a lambda expression, `constof(name)` resolves only against the lambda's own parameter list, including the lambda's own explicit callable `this` if present.

It must not resolve to parameters of an enclosing function, method, or lambda. Captured variables are not valid `constof` anchors.

```camp
const byte[] outer;

auto bad = () =>
{
	return (constof(outer) byte*)null; // ERROR: outer is not a lambda parameter
};

auto good = (const byte[] source) =>
{
	return (constof(source) byte*)source.elements; // OK
};
```

If a lambda parameter shadows an outer parameter, `constof(name)` refers to the lambda parameter.

## 10. `constof(this)` in context-carrying callable types

Allow `constof(this)` in context-carrying callable types that declare an explicit callable `this` parameter. The explicit callable `this` is a synthetic hidden-context input parameter and may be used as a dependent-const anchor.

Examples of valid type surfaces:

```camp
newtype delegate constof(this) byte* DataAccessor(const this);

delegate constof(this) byte*(const this) accessor;
```

For a bound method reference, the `this` constness bit is determined from the receiver expression used to form the callable value.

```camp
struct Buffer
{
	byte* elements;

	constof(this) byte* data(const this) : DataAccessor
	{
		return (constof(this) byte*)this.elements;
	}
}

Buffer mutableBuffer;
const Buffer constBuffer;

auto a = mutableBuffer.data; // calling a returns byte*
auto b = constBuffer.data;   // calling b returns const byte*
```

If the compiler does not currently represent a source-level dependent constness bit for a callable value's hidden context, add one as type-checker metadata only. This metadata must not affect ABI layout, generated symbols, or callable lowering.

Test both direct calls and bound callable values. If a callable value with `constof(this)` is assigned through an explicit unsafe callable cast, section 7 applies.

## 11. Newtype and nominal boundaries

`constof` conversion does not cross `newtype` nominal boundaries implicitly.

If a value crosses a value `newtype` or callable `newtype` boundary, the ordinary explicitness rule for that boundary remains in force. A constness conversion may be widening, but the nominal conversion is still explicit unless existing Camp rules already allow it.

```camp
newtype BytePtr: const byte*;

void demo(const byte[] source)
{
	constof(source) byte* p = (constof(source) byte*)source.elements;
	BytePtr b = p;          // ERROR unless an existing newtype rule permits it
	BytePtr c = (BytePtr)p; // OK explicit nominal cast
}
```

## 12. Required test coverage

Add or verify tests for these categories.

### 12.1 Conversion tests

Positive:

- mutable expression assigned to `constof(anchor)` storage;
- `constof(anchor)` expression assigned to ordinary `const` storage;
- same-anchor `constof(anchor)` assigned to same-anchor `constof(anchor)` storage;
- explicit cast from ordinary `const` to `constof(anchor)` storage;
- explicit cast from `constof(anchor)` to mutable storage.

Negative:

- ordinary `const` assigned to `constof(anchor)` storage without cast;
- `constof(anchor)` assigned to mutable storage without cast;
- `constof(anchorA)` assigned to `constof(anchorB)` without cast when anchors differ;
- common-type inference inventing `constof(...)` without an explicit target or cast.

### 12.2 Signature compatibility tests

Positive:

- target output ordinary `const`, candidate output `constof(anchor)`;
- target input `constof(anchor)`, candidate input ordinary `const`;
- `out` payload output covariance;
- interface implementation using these two variance directions;
- callable `newtype` ascription using these two variance directions;
- function/method/delegate assignment using these two variance directions;
- anonymous callable compatibility with renamed anchor parameters.

Negative:

- target output `constof(anchor)`, candidate output ordinary `const` without explicit callable cast;
- target input ordinary `const`, candidate input `constof(anchor)` without explicit callable cast;
- mismatched `constof` anchors that do not correspond by parameter position;
- accidentally applying relaxed compatibility to virtual overrides.

### 12.3 Override tests

Positive:

- exact override where base and derived both use ordinary `const`;
- exact override where base and derived both use corresponding `constof(anchor)`;
- base call from an exact `constof` override body.

Negative:

- base ordinary `const`, override `constof(anchor)`;
- base `constof(anchor)`, override ordinary `const`;
- base `constof(anchorA)`, override `constof(anchorB)` where anchors do not correspond after parameter-position mapping.

### 12.4 Lambda tests

Positive:

- target-typed lambda omitting parameter types infers `constof` parameters from target;
- target-typed lambda with explicit ordinary `const` parameter satisfies target `constof` input;
- `auto` lambda with explicitly written `constof` parameter infers a callable type containing `constof`;
- `auto` lambda with return expression explicitly cast to `constof(lambdaParam)` infers dependent return;
- lambda parameter names differing from callable `newtype` parameter names still match by anchor position.

Negative:

- explicitly typed lambda parameter missing required constness;
- `auto` lambda inferring `constof` return from provenance without explicit cast;
- `constof(...)` in lambda body resolving to enclosing function parameter;
- captured variable used as `constof` anchor;
- lambda anchor name mapped by raw identifier text instead of bound parameter position.

### 12.5 `constof(this)` tests

Positive:

- direct method call with `constof(this)` return from mutable receiver returns mutable type;
- direct method call with `constof(this)` return from const receiver returns const type;
- bound callable value with `constof(this)` preserves receiver-derived constness;
- context-carrying callable `newtype` with explicit `const this` and `constof(this)` binds correctly.

Negative:

- `constof(this)` in a callable type with no explicit callable `this`;
- `constof(this)` incorrectly resolving to an enclosing method receiver from inside a receiverless lambda;
- callable context constness metadata affecting ABI or generated symbols.

### 12.6 Explicit callable cast tests

Positive:

- explicit callable cast from ordinary-const output to dependent-const output compiles;
- explicit callable cast from dependent-const input to ordinary-const input compiles;
- explicit callable cast with mismatched dependent anchors compiles;
- generated lowering remains wrapper-free and symbol-stable.

### 12.7 Special-surface tests

Positive and negative tests should cover:

- property getter output covariance;
- property setter input contravariance;
- async completion result output covariance if async signature checking is active;
- iterator yield/current output covariance if iterator signature checking is active;
- `thrown` exclusion from `constof` variance;
- `newtype` nominal boundary preservation.

## 13. Implementation notes

Recommended implementation strategy:

1. Represent each `constof(anchor)` as a qualifier node bound to a parameter identity, not as a raw string.
2. During callable/signature binding, create a parameter-identity table for each signature before resolving `constof` anchors.
3. In signature compatibility, pass a variance mode into type comparison: invariant, input, or output.
4. Route virtual override validation through invariant comparison only.
5. Route interface implementation, callable ascription, callable assignment, and target-typed lambda compatibility through input/output-aware comparison.
6. Keep explicit callable casts on a permissive path that binds the target type but skips `constof` compatibility rejection.
7. Keep ordinary expression/storage assignment on the conversion lattice in section 3.
8. Keep non-output `constof` parameter calls on the equality check in section 4.
9. Keep lambda `constof` lookup scoped to the lambda parameter table; do not search enclosing function parameters.
10. Preserve all existing ABI erasure behavior: `constof(...)` continues to lower as ordinary `const`.
