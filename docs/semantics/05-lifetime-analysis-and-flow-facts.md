# Lifetime Analysis And Flow Facts

This supplement describes the compiler's lifetime fact model. It is written for
compiler authors, not for ordinary language users. User-facing lifetime syntax
is documented in [Lifetimes, Allocation, And `within`](../language/07-lifetimes-allocation-and-within.md);
this document explains how those source rules are represented, propagated, and
diagnosed inside the compiler.

Lifetime analysis tracks a small set of facts for pointer-bearing values so
code cannot accidentally store short-lived references in escaped storage,
return references tied to local state, capture scoped state in escaped callable
contexts, or pass allocator and cleanup values through incompatible lifetime
surfaces.

The lifetime analyzer must remain aligned with:

- [Conversions, Raw Carriers, And Fence Casts](03-conversions-raw-carriers-and-fence-casts.md)
  for casts that discard, preserve, or rewrite lifetime information;
- [Constof And Signature Compatibility](04-constof-and-signature-compatibility.md)
  because call-site substitution solves constness and lifetime relations across
  the same signature surface;
- [Callable Lowering And Context Ownership](07-callable-lowering-and-context-ownership.md)
  because delegate and once contexts carry hidden pointer lifetime;
- [Construction, Destruction, And Allocation](10-construction-destruction-and-allocation.md)
  because constructors, `new`, `delete`, and cleanup paths create and consume
  escaped values.

## Tracked Types

The compiler only attaches lifetime facts where the type can carry pointer-like
state or where the storage shape forces lifetime reasoning.

Tracked source/resolved shapes include:

- ordinary data pointers;
- raw function pointers and concrete `fn` types;
- `delegate`, `once`, `async`, and iterator-like callable values with hidden
  context;
- arrays and array slices;
- fixed arrays and fixed storage, because elements may be pointer-bearing and
  addresses may be taken from inline storage;
- optionals whose element type is pointer-bearing;
- `string`, `astring`, and `wstring`;
- generic type parameters that are erased enough that the compiler cannot prove
  they are pointer-free;
- structs/classes/newtypes when their resolved shape or fields make them
  pointer-bearing for the operation being analyzed.

Do not restrict lifetime analysis to syntactic `*` types. Arrays, delegate
contexts, generic values, and initializer-retained fields are the places where
the most important diagnostics usually come from.

## Fact Form

Lifetime annotations bind to a normalized fact containing:

- a **kind** such as `escaped`, `scoped`, `unscoped`, `default`, `null`, or
  `unknown`;
- zero or more **anchors** such as a parameter name or `this`;
- a **source** label used in diagnostics and debugging, such as `parameter`,
  `slot`, `field`, `call`, `new`, or `initializer`.

The compiler serializes many facts as compact strings such as:

```text
scoped(buffer):parameter
escaped:field
unscoped(this):return default
```

The exact source labels are compiler-internal, but their meaning matters:
diagnostics should be able to distinguish a value tied to an argument from one
tied to a local, a field, a string literal, or an allocation result.

## Lifetime Kinds

`escaped` means the value is safe to store in escaped storage. Globals, static
fields, escaped fields, allocation results whose allocator contract returns
escaped memory, function pointer values, and string literals commonly produce
escaped facts.

`scoped` means the value is tied to a local scope, local storage, or a specific
source anchor. A plain local address often becomes a scoped value. A parameter
defaults to a scoped value anchored to the parameter name unless its type or
annotation says otherwise.

`scoped(anchor)` means the value may be used as long as `anchor` remains valid.
For example, a slice derived from `buffer` should stay tied to `buffer`, and a
method result may be tied to `this`.

`unscoped(anchor)` is the signature-level escape valve for values that are not
restricted to the default scoped relationship. It does not mean "safe forever";
it means the value is not constrained by the omitted default relation except
where the anchor list says otherwise.

`default` and `null` are benign values for many lifetime checks. They should not
make a retained aggregate become scoped.

`unknown` is a conservative fact used when the compiler cannot prove the source
of a pointer-bearing value. It should not be treated as `escaped`. If a check
requires a definite escaped fact, an unknown fact should either report a
specific diagnostic or preserve uncertainty until a later check can report one.

## Anchors

Anchors are source-visible names used by `scoped(...)`, `unscoped(...)`, and
`constof(...)` relationships. The binder must reject missing anchors and anchors
that are not valid in the current signature or body context.

Valid lifetime anchors are normally:

- ordinary value parameters;
- `in` parameters when the address of the parameter is taken;
- explicit receiver anchor `this`;
- declared anchors introduced by generated callable or interface surfaces when
  those surfaces expose source-equivalent relationships.

Invalid anchor uses should be diagnosed at the annotation, not later at the
first call site. Examples that must be rejected include anchors on `within`
parameters when they are not valid lifetime sources, anchors to thrown slots in
positions that cannot observe them, missing names, and dependent anchors that
would require circular substitution.

## Slot Facts And Value Facts

The compiler tracks slot facts and value facts separately.

A **slot fact** describes the storage location: where a variable, field,
parameter, or declaration target is allowed to store pointer-bearing values.

A **value fact** describes the value currently read from that storage or
produced by an expression.

This distinction is essential. A local slot may be scoped to the local name, but
its current value may be an escaped string literal. A global slot is escaped
storage, but assigning a scoped local pointer into it must still fail. A field
slot inside an escaped class must be escaped even when a particular initializer
starts from `null`.

Compiler code should never collapse the two concepts into one property simply
because many declarations initialize both facts to the same value.

## Declaration Defaults

Default facts are assigned during declaration analysis:

- parameters of tracked types default to `scoped(parameterName):parameter`;
- the implicit or explicit receiver defaults to a scoped receiver fact unless
  an explicit receiver lifetime overrides it;
- source input positions therefore default to scoped facts, while source output
  positions default to `unscoped(this)` for receiver-bearing declarations and
  `unscoped` for receiverless declarations unless a more specific relation is
  written or implied by the feature;
- globals and static fields of tracked types use escaped slot facts;
- class instance fields in escaped classes use escaped slot facts;
- instance receivers in escaped classes default to escaped receiver facts, and
  escaped interface contracts require implementation receiver lifetimes that
  can satisfy the escaped interface pointer;
- instance fields may explicitly require `escaped`, but non-escaped field-level
  lifetime annotations should be rejected;
- local tracked slots default to a scoped fact anchored to the local name when
  one exists;
- function pointer values default to escaped value facts because function
  symbols are not stack-owned closure contexts;
- fixed storage remains tracked even when the resolved element type alone would
  otherwise look scalar.

When a declaration has an initializer, the slot fact still describes the storage
and the initializer determines the initial value fact where possible.

## Expression Facts

Expression lifetime facts are propagated through body analysis:

- `null` produces a `null` fact.
- String literals produce escaped facts.
- Array literals produce scoped facts because their backing storage is local to
  the expression/lowering context unless the value is immediately copied into
  longer-lived storage.
- `default` produces a default fact when the result type is pointer-bearing.
- Address-of produces a scoped fact tied to the storage or `in` parameter being
  addressed when that anchor is available.
- Indexing a slice should keep the target fact and mark the source as an element
  or slice view.
- Member access should keep the target fact unless the member is known escaped
  storage.
- Casts with explicit lifetime forms apply the cast fact; ordinary casts keep
  the operand fact unless the conversion supplement says the carrier discards
  lifetime.
- Calls substitute the callee's return lifetime template through the call-site
  argument facts.
- `new` derives its value fact from the allocator or allocation helper return
  contract; if that cannot be resolved, it remains unknown rather than escaped.
- `init` and initializer lists combine retained facts from pointer-bearing
  constructor arguments and fields.
- Capture-free lambdas can be treated as escaped callable values; captured
  lambdas depend on the target callable lifetime and generated context
  ownership.

Expression facts should follow rewrites. If an expression is rewritten into a
generated node, later checks should recover the fact from the rewritten node or
the original expression provenance.

## Call-Site Relation Solving

Calls solve relationships among:

- ordinary arguments;
- receiver/`this`;
- return value;
- `out` parameters;
- thrown slots;
- generic substitutions;
- lifetime anchors;
- `constof` anchors.

The analyzer builds a call context from the source signature and actual
arguments. Parameter checks compare actual argument facts with the parameter
template. Return checks substitute the return template through the same context.
`out` arguments are storage writes, so the target slot receives a value fact
compatible with the callee's `out` parameter relation.

The return default for receiver-bearing declarations is important: an ordinary
method returning a pointer-bearing value without an explicit lifetime relation
is treated as tied to `this`, not as escaped. This protects fluent and accessor
patterns from accidentally returning references to short-lived receiver state
through a global-looking type.

The same substitution machinery handles interface conversions. A struct-to-
interface conversion that materializes a scoped temporary carrier cannot satisfy
an escaped interface pointer unless the implementation surface itself provides a
valid escaped receiver relation. A class interface conversion can usually
produce a stable interface pointer through stored class state, but the class
receiver lifetime still determines whether the produced interface pointer may
escape.

## Prepared Result Lifetimes

A transformed prep call without `(new)` produces storage equivalent to a scoped
initialized array. Its value fact must prevent escape through returns, fields,
captured delegates, async frames, iterator frames, conditional joins, loop-carried
state, or other storage that would reject the equivalent scoped array.

`(new)` on a direct transformed prep call uses ordinary allocated lifetime and
ownership. Its fact comes from the selected `within` allocator and existing
allocation contract, and cleanup uses the matching ordinary delete path. The
direct-result rule prevents a member, index, slice, or method chain from losing
the owning reference before it can be captured or attached to `finally delete`.
No special prep lifetime cast or lifetime category exists.

Receiver and explicit argument facts are solved once and retained across the
sizing and writing calls. A generated temporary or protocol call must not
weaken those facts or replace the source prepared expression as the diagnostic
provenance.

## Assignment And Storage

Assignment checks whether a value fact can be stored in the target slot fact.
The common high-risk case is storing a non-escaped value into escaped storage:

```camp
struct Holder
{
	escaped char[] text;
}

void saveLocal(Holder* holder)
{
	char[16] local;
	holder.text = local[..]; // must diagnose: scoped slice into escaped field
}
```

Compiler writers should apply the same storage rule to:

- global variables;
- static fields;
- escaped instance fields;
- out arguments whose callee may retain the value;
- lambda/delegate contexts that escape;
- async and iterator frames that outlive the current statement;
- constructor-retained fields.

## Fields

Field-level lifetime annotations are intentionally narrow. Instance fields that
claim a lifetime must be escaped, and the field type must be pointer-bearing.
The analyzer should reject scoped field annotations because instance storage can
move through construction, assignment, allocation, and escape paths where the
original lexical scope does not remain meaningful.

Class fields in an escaped class are escaped storage even without explicit
field-level annotations. Static fields are escaped storage. Ordinary struct
fields derive their behavior from the storage containing the struct.

## Return, Yield, And Delete

Return expressions are checked against the function result lifetime. A result
whose template is tied to a parameter can return a view of that parameter; a
plain return from a local cannot be accepted as escaped.

Yield expressions are checked against iterator result lifetimes. Iterator state
may retain local values across yields, so the analyzer must treat lifted locals
and current-value slots as generated storage with real lifetime consequences.

`delete` is both a lifetime and ownership operation. The target value must
satisfy the free surface of the selected allocator or destructor helper. A
scoped pointer cannot be silently passed to an escaped free parameter.

## Construction And Retention

Constructor analysis must account for retained pointer-bearing inputs. A
constructor can be syntactically ordinary but still store arguments into fields,
or an initializer list can directly populate pointer-bearing fields. The
compiler combines retained facts:

- if all relevant retained values are escaped, the constructed value is escaped
  with respect to retained data;
- if any retained value is scoped, the constructed value is scoped to the
  union of those anchors;
- default, null, and unknown facts do not create an escaped proof.

This is what prevents code from constructing a heap object that stores a pointer
to local stack storage unless an explicit and valid lifetime conversion appears
at the right boundary.

## Delegates And Captures

Delegate and once values carry a hidden context pointer. Lifetime annotations on
context-carrying callable values apply to that hidden context. Capturing lambdas
therefore need two checks:

- whether the target callable form may carry a context;
- whether captured values may be stored in that context.

An escaped delegate context must not retain scoped locals. A scoped delegate
context may capture by address when lowering can prove the context does not
escape the lexical region. Capture-free lambdas may lower to function symbols
and are not tied to local storage.

The callable-lowering supplement describes the generated context layout and
deletion rules.

## Async And Iterator Frames

Async state machines and iterator generators lift values into generated frames.
Those frames can outlive the source statement, so lifetime analysis must run on
the source semantics before lowering and must preserve enough facts for
generated fields and cleanup code.

Important restrictions include:

- async bodies that can suspend must not retain stack-only values in the frame;
- iterator generator bodies cannot use init-array construction in a way that
  would lift temporary array storage across yields;
- yielded references must be valid for the iterator protocol surface;
- cleanup code generated for `finally`, delegates, and destructor paths must
  not delete or free values through incompatible lifetime contracts.

## Generic Boundaries

Generic `T: any` values are treated conservatively because they may be
pointer-bearing. Returning, storing, copying, or capturing them can require
capabilities described in
[Generics, Erasure, And Capabilities](06-generics-erasure-and-capabilities.md).

For example, a generic function cannot return a pointer-bearing value tied to an
`in` parameter as if it were escaped just because the exact `T` is erased. The
lifetime analyzer must operate on the erased shape visible to the generic body
and require explicit capabilities where the body needs layout, copy, or
interface-dispatch facts.

## Casts

Lifetime-changing casts are classified by the conversion supplement:

- changing a data-pointer lifetime is an explicit cast;
- changing a callable lifetime is unsafe because callable lifetimes can describe
  hidden context, receiver lifetime, result provenance, and argument/result
  relationships;
- raw carrier fences such as `nint`, `nuint`, `void*`, `fn*`, and `untyped` may
  discard lifetime facts and recover with the target's written lifetime.

The lifetime analyzer should consume the conversion classifier's result instead
of duplicating cast policy.

## Diagnostics

Diagnostics should state which value cannot be stored, returned, yielded,
deleted, or captured, and identify the lifetime relation that causes the
conflict.

Good diagnostics name both sides:

- the value fact, such as a local, parameter, receiver, or scoped call result;
- the target requirement, such as escaped field storage, an escaped out
  parameter, an escaped delegate context, or a return slot.

Diagnostics should prefer source ranges on the expression that causes the
unsafe movement. For assignments, point at the assigned value when the value is
the problem and at the target when the target lifetime declaration is invalid.
For call arguments, point at the argument expression. For invalid lifetime
annotations, point at the annotation.

## Test Surface

Lifetime changes should be covered by focused diagnostics or lowering tests for:

- invalid anchors;
- invalid annotation placement;
- storing scoped values into escaped fields;
- constructor retention of local storage;
- return, yield, and delete mismatches;
- lifetime casts;
- generic boundaries;
- lambda capture escape;
- within allocator lifetime;
- iterator and async frame retention.

When a compiler change touches lifetime facts, inspect the generated lowered
dump as well as diagnostics. Many regressions show up as an accepted program
whose generated context, frame, or retained field has lost the intended fact.

## Implementation Anchors

Primary implementation points include:

- `BindableNodeAnalyzer.LifetimeFacts.cs` for fact creation, propagation, and
  checks;
- `BindableNodeAnalyzer.MethodBody.Semantics.cs` and
  `BindableNodeAnalyzer.Flow.cs` for body/flow analysis hooks;
- `BindableNodeAnalyzer.Lowering.Lambdas.cs`,
  `BindableNodeAnalyzer.Expansion.Iterators.cs`, and async lowering code for
  generated contexts and frames;
- `BindableNodeAnalyzer.ConstOf.cs` for signature anchor interaction;
- diagnostic fixtures under `tests/Diagnostics/lifetime_*.camp` and related
  expected files.
