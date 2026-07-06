# Camp Feature Proposal: `constof(anchor)`

Status: proposed compiler feature  
Audience: LLM implementation agent working in the Camp compiler

## 1. Goal

Add a source-level dependent const qualifier:

```camp
constof(anchor)
```

`constof(anchor)` lets a Camp API expose a caller-visible constness relation without granting the callee mutation rights. It is primarily for borrowed interior pointers, borrowed spans, and related `out` results whose mutability should match the caller's original argument.

This feature also replaces any existing compiler behavior that derives return-value constness from lifetime annotations. Lifetimes and constness must be independent: use `scoped(anchor)` for lifetime and `constof(anchor)` for caller-visible constness.

## 2. Non-goals

`constof(anchor)` must not:

- grant mutation rights inside the callee;
- imply any lifetime relationship;
- allocate, copy, pin, or change the runtime value;
- change ABI representation or generated symbol names;
- make C headers caller-dependent.

Inside the callee, every `constof(...)` slot is checked as ordinary `const`.

## 3. Syntax

Add `constof` as a type declarator keyword with the same placement behavior as `const`:

```camp
constof(source) T*
constof(source) T[]
T* constof(source)
SomeType<constof(source) T*>
(constof(source) T*)expr
(scoped(source) constof(source) T*)expr
```

Grammar-level shape:

```text
TypeDeclarator := ... | 'constof' '(' Identifier ')'
```

`constof(anchor)` is valid only where the named `anchor` is in parameter-name scope. This includes function and method signatures, callable signatures where parameter names are available, and the corresponding function or method body. Return types may reference later parameters in the same signature; resolve after the full signature is bound.

`this` is a valid anchor when the receiver is in scope and is declared or inferred as ordinary `const this`.

## 4. Anchor validity

The `anchor` must resolve to one non-output formal parameter or receiver.

The anchor is invalid if it is:

- not in scope;
- an `out` parameter;
- a `thrown` parameter;
- a `within` parameter;
- declared with `constof(...)` rather than ordinary `const`;
- not declared with ordinary `const`;
- ambiguous because its type contains more than one ordinary `const` slot.

For v1, require exactly one ordinary `const` slot on the anchor formal. Store that slot as the anchor's dependent const bit. This avoids ambiguity in types such as `const T* const`.

Examples:

```camp
// OK: one ordinary const slot on source.
scoped(source) constof(source) T* first<T: any>(
	const T[] source,
	sizeof(T));

// OK: receiver is the anchor.
scoped(this) constof(this) T[] slice<T: any>(
	const T[] this,
	@range nuint index = 0,
	nuint count = ^0,
	sizeof(T));

// ERROR: anchor is not ordinary const.
constof(source) T* bad(T[] source);

// ERROR: anchor itself is dependent.
constof(left) T* bad(const T[] source, constof(source) T[] left);
```

## 5. Caller-visible substitution

At each call site, compute a boolean constness bit for each anchor before applying the ordinary conversion to the formal parameter type.

- If the actual argument is statically const in the anchor's const slot, the bit is `const`.
- If the actual argument is statically mutable in that slot, the bit is `mutable`.
- Mutable-to-const conversion used to bind the anchor formal does not change this bit.

Then substitute each `constof(anchor)` use in the public call signature:

- anchor bit `const` -> substitute ordinary `const` in that use slot;
- anchor bit `mutable` -> substitute no `const` in that use slot.

Example:

```camp
byte[] mutableBytes = ...;
const byte[] constBytes = ...;

byte[] a = mutableBytes.slice(1..4);        // result: byte[]
const byte[] b = constBytes.slice(1..4);    // result: const byte[]
```

## 6. Callee implementation view

For body analysis, normalize every `constof(anchor)` slot to ordinary `const`.

This applies to:

- parameters;
- returns;
- `out` parameter value types;
- locals;
- generic arguments;
- array element types;
- casts;
- any other type position where `constof(...)` is accepted.

The callee must never mutate through a `constof` value.

```camp
void use<T: any>(const T[] left, constof(left) T[] right, sizeof(T))
{
	// Body view:
	// left  : const T[]
	// right : const T[]

	right[0] = default; // ERROR: callee view is const
}
```

## 7. `constof` on parameters

A non-output parameter whose type contains `constof(anchor)` imposes a caller-side equality check.

For each `constof(anchor)` slot in that parameter, the actual argument supplied for that parameter must have the same static constness bit as the anchor actual before conversion.

This is stricter than ordinary assignability.

```camp
scoped(left) constof(left) T* choose<T: any>(
	const T[] left,
	constof(left) T[] right,
	bool useRight,
	sizeof(T));

T[] mutableA = ...;
T[] mutableB = ...;
const T[] constA = ...;
const T[] constB = ...;

choose(mutableA, mutableB, true); // OK
choose(constA, constB, true);     // OK
choose(mutableA, constB, true);   // ERROR: right is const, left is mutable
choose(constA, mutableB, true);   // ERROR: right is mutable, left is const
```

Inside the callee, both `left` and `right` are still treated as const. The parameter relation exists so a `constof(left)` return or `out` value may be derived from either parameter.

## 8. Return values

A return type containing `constof(anchor)` is a caller-visible dependent result. The caller sees the substituted type. The callee body checks the result as ordinary `const` and must prove or assert that the value is const-correct for the anchor.

A returned expression may satisfy a `constof(anchor)` result when it is one of:

1. a non-const value not derived from const storage;
2. `anchor` itself;
3. a direct view, field address, subobject address, or interior pointer derived from `anchor`;
4. a value derived from another parameter constrained with `constof(anchor)`;
5. the result of a call whose return type is itself `constof(x)` and where `anchor`, or a direct view/subobject of `anchor`, was supplied for `x`;
6. an explicit `constof(anchor)` cast.

Lifetime remains separate. A borrowed return normally needs both annotations:

```camp
scoped(source) constof(source) T* first<T: any>(
	const T[] source,
	sizeof(T));
```

`scoped(source)` describes validity. `constof(source)` describes caller-visible mutability.

## 9. `out` parameters

For `constof`, an `out` parameter is a produced result and should be treated like a return value.

```camp
void first<T: any>(
	const T[] source,
	out scoped(source) constof(source) T* item,
	sizeof(T));
```

Caller-visible behavior:

```camp
T[] mutableItems = ...;
const T[] constItems = ...;

T* p;
const T* q;

first(mutableItems, out p); // OK: produced type is T*
first(constItems, out q);   // OK: produced type is const T*
first(constItems, out p);   // ERROR: would store const T* into T*
```

If a call omits a trailing `out` parameter and binds it as a result, infer the substituted caller-visible type:

```camp
auto p = first(mutableItems); // p: T*
auto q = first(constItems);   // q: const T*
```

Callee-body behavior:

```camp
void first<T: any>(
	const T[] source,
	out scoped(source) constof(source) T* item,
	sizeof(T))
{
	item = (scoped(source) constof(source) T*)source.elements; // OK assignment to result slot
	*item = default; // ERROR: produced pointer is const in callee view
}
```

The assignment `item = ...` writes the caller-provided result slot. It does not grant mutation through the produced pointer.

## 10. Locals

A local whose type contains `constof(anchor)` is allowed only inside a body where `anchor` is in scope. The local's implementation view is const, and any assignment to the local must satisfy the same provenance-or-cast rule as a `constof(anchor)` return/out result.

```camp
void demo<T: any>(const T[] source, sizeof(T))
{
	constof(source) T* p = (constof(source) T*)source.elements;
	*p = default; // ERROR: callee view is const
}
```

This is uncommon because `auto` usually preserves the ordinary callee-view type. The main use is to carry a dependent provenance tag to a later return/out assignment.

## 11. Explicit casts

A cast to a type containing `constof(anchor)` is an explicit assertion that the expression is safe to expose with the caller-visible constness of `anchor`.

```camp
return (scoped(source) constof(source) T*)raw;
```

Rules:

- The anchor must be valid under the anchor rules above.
- The cast has no runtime effect beyond any existing representation cast rules.
- The cast does not allocate, copy, pin, or change lifetime.
- The cast does not grant local write access; the expression's body view remains const.
- The cast attaches a `constof(anchor)` provenance tag that may satisfy a `constof(anchor)` return, `out`, or local assignment.

This is the escape hatch for low-level functions where the compiler cannot prove the derivation.

## 12. ABI and metadata

Lowering:

- `constof(anchor)` lowers as ordinary `const` in ABI-facing types.
- No generated symbol name changes.
- No wrapper, thunk, allocation, or runtime metadata is required.
- C headers expose the conservative ordinary-const form.

Source-level Camp API metadata must preserve `constof(anchor)` exactly in type strings or structured type metadata so Camp-aware callers and tools can recover the dependency.

## 13. Remove lifetime-derived constness

Delete or disable any existing compiler rule that determines result constness from lifetime annotations such as `scoped(anchor)`. After this feature:

```camp
scoped(source) T* f(const T[] source);                 // lifetime only; no caller-dependent constness
scoped(source) constof(source) T* g(const T[] source); // lifetime + caller-dependent constness
```

The first declaration must not automatically become const-correct for mutable callers. The author must write `constof(source)`.

## 14. Implementation checklist

1. Add `constof` to lexical/parser handling as a type declarator with one identifier argument.
2. Extend the type qualifier/declarator model with `DependentConst(anchorName)`.
3. During signature binding, resolve dependent const anchors after the full parameter list and receiver are known.
4. Validate anchors: in scope, non-output, ordinary const, not dependent, and exactly one ordinary const slot for v1.
5. Add a call-site substitution pass that computes anchor constness from actual arguments before mutable-to-const conversion.
6. Enforce exact constness equality for non-output `constof(anchor)` parameters.
7. Normalize all `constof` slots to ordinary `const` for callee body mutation/type checks.
8. Track `constof(anchor)` provenance tags for anchor-derived expressions and explicit casts.
9. Use the provenance-or-cast rule for returns, `out` assignments, and `constof` local assignments.
10. Treat `out` parameters containing `constof` as produced results; explicit out targets must be valid for the substituted produced type, and omitted trailing `out` bindings infer the substituted type.
11. Lower `constof` to ordinary `const` for ABI/C output while preserving it in Camp source metadata/API output.
12. Remove lifetime-derived constness behavior and add diagnostics pointing authors to `constof(anchor)`.

## 15. Required diagnostics

Implement clear diagnostics for at least these cases:

```camp
// Unknown anchor.
constof(missing) T* f(const T[] source);

// Anchor is not ordinary const.
constof(source) T* f(T[] source);

// Anchor is output-only.
void f(out const T[] source, out constof(source) T* result);

// Anchor itself is dependent.
void f(const T[] source, constof(alias) T[] alias, out constof(alias) T* result);

// Parameter constness mismatch.
choose(mutableItems, constItems, true);

// Callee mutation through dependent value.
void f<T: any>(const T[] source, constof(source) T[] view, sizeof(T))
{
	view[0] = default;
}

// Missing dependent constness after removing lifetime-derived constness.
scoped(source) T* bad<T: any>(const T[] source, sizeof(T));
```

## 16. Suggested tests

Add positive tests for:

- parsing `constof(anchor)` in prefix and postfix const positions;
- `constof(this)` on array receiver methods;
- mutable anchor call producing mutable return type;
- const anchor call producing const return type;
- omitted trailing `out` inferring substituted type;
- explicit `out` accepting/rejecting substituted produced type;
- `constof` parameter equality checks;
- explicit `constof` casts satisfying return/out provenance;
- body mutation rejection through `constof` values;
- metadata/API output preserving `constof(anchor)`;
- C output using conservative ordinary `const` and unchanged symbols.

Add negative tests for:

- unknown anchor;
- non-const anchor;
- `constof` anchor;
- `out`, `thrown`, or `within` anchor;
- ambiguous multi-const anchor;
- constness mismatch on `constof` parameter;
- returned/assigned `constof` value without derivation or explicit cast;
- legacy lifetime-derived constness no longer occurring.
