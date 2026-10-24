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

`scoped(anchor)` means the value points into the anchor's own memory: it may be
used only as long as `anchor` remains valid. For example, a slice derived from
`buffer` stays tied to `buffer`, and a method result that points into the
receiver's inline storage is `scoped(this)`.

`unscoped(anchor)` means the value is something the anchor retains: it was
stored in the anchor, so it lasts as long as the anchor's content does, not as
long as the anchor's own memory. A pointer or slice held in a field of the
receiver is `unscoped(this)`. It does not mean "safe forever"; it means the value
is not constrained by the default scoped relation except where the anchor list
says otherwise.

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

Ordinary, `in`, `out`, and `within` parameters may name ordinary input values,
`in` inputs (including their address), or an available `this` receiver. Neither
`out`, `thrown`, nor `within` slots may themselves serve as anchors: their values
are output or hidden allocation channels rather than observed lifetime inputs.
Thrown slots cannot name activation-bound anchors, because thrown values must
outlive the throwing activation. A dependency cycle, including an explicit
self-reference, is circular and is rejected on each participating annotation.
An acyclic chain of dependencies on ordinary inputs is valid.

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
- pointers to an escaped class or escaped interface type use escaped slot and
  value facts wherever they appear, without annotation (see Escaped Types);
- a by-value escaped interface also uses escaped facts: its representation is
  a pointer to a vtable, even though its source type has no `*`;
- class instance fields in escaped classes use escaped slot facts;
- instance receivers in escaped classes default to escaped receiver facts, and
  escaped interface contracts require implementation receiver lifetimes that
  can satisfy the escaped interface pointer;
- instance fields may explicitly require `escaped`, but non-escaped field-level
  lifetime annotations should be rejected;
- local tracked slots default to a scoped fact anchored to the local name when
  one exists, and their value facts are inferred from what is stored in them:
  lifetime annotations are rejected on the type of a local variable (including
  `auto`, `for` initializers and `foreach` variables), and a lifetime cast on
  the stored value is the explicit proof;
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
  or slice view. An element read is retained by that array and may be stored
  back into the same array, including through a local copy. Storing it into an
  unrelated array requires a relation such as `unscoped(destination)` on the
  source. This does not extend the lifetime of local backing storage.
- Member access should keep the target fact unless the member is known escaped
  storage. A fixed inline field instead borrows its containing storage, even
  when the field's elements contain no pointers. Slices of that field and
  `scoped(this)` calls on array elements keep the owner's relation. A retained
  input declared `unscoped(destination)` may therefore supply interior views
  to that destination; local backing storage still cannot escape.
- Casts with explicit lifetime forms apply the cast fact; ordinary casts keep
  the operand fact unless the conversion supplement says the carrier discards
  lifetime.
- Calls substitute the callee's return lifetime template through the call-site
  argument facts.
- `new` always yields an `escaped` value fact. Because the fact does not depend
  on the allocator, the `malloc` or `alloc` that a `new` lowers to must itself be
  declared to return `escaped`; otherwise the `new` is a diagnostic.
- Constructor-shaped type calls and initializer lists combine retained facts
  from pointer-bearing constructor arguments and fields.
- `stackalloc` produces a scoped stackalloc-backed fact tied to the current
  activation segment. Slices, views, casts that preserve carriers, locals, and
  prepared/interpolated results derived from that storage preserve the
  stackalloc-backed origin.
- Implicit scoped runtime interpolation carries the same stack storage origin
  as explicit `stackalloc` interpolation. Local copies preserve it, so returning
  an implicit interpolated local is rejected just like returning explicit stack
  storage. Constant text and `new` interpolation retain their existing lifetimes.
  A conditional retains stack storage provenance from either arm, including when
  the other arm is constant text or allocated storage.
- A catch variable of pointer-bearing type receives an `escaped` fact, because a
  thrown value outlives the frame that threw it. A caught pointer may therefore
  be deleted.
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

A value receiver borrows its storage for the call. For input retention checks
its current value fact does not replace that storage lifetime. Scoped local
inputs can satisfy a receiver's retention requirement when their lexical scope
contains the receiver's scope. An inner local cannot satisfy the requirement of
an outer receiver.

The same retention rule applies to ordinary arguments declared
`unscoped(anchor)`. An input that is not a local of the calling function
outlives that function's local destinations. This includes parameter values,
fields of by-value or pointer parameters, and copies that preserve their
provenance. Two unrelated parameter values still need an explicit relation;
an inner local still cannot be retained by an outer local destination.

A result substitutes the receiver according to what the result declares. A
`scoped(this)` result takes the receiver's address, so a result that points into
a local receiver cannot outlive that local. A default or `unscoped(this)` result
takes the receiver's content fact, which for a local struct is fixed by what it
was initialized from: a view held in a field that came from a caller-provided
value keeps that lifetime when read through an accessor, exactly as reading the
field directly does. Methods that return `this` or `classtype*` name the receiver
itself and keep the receiver's own lifetime.

`out` values follow the same address-versus-content rule as results. A default
or `out unscoped(this)` parameter receives what the receiver retains; a
`out scoped(this)` parameter receives a value tied to the receiver's storage.
Calling either form on a local value or one of its inline fields preserves
that distinction. Retained local backing storage still cannot escape.

The same rule applies to any pointer argument. Passing the address of a local
struct to an unscoped result or output relation contributes the struct's
retained content fact, rather than the lifetime of its address. Passing a
pointer alias preserves this rule. A result pointing into the pointee's own
memory, such as `&input.flags`, must declare `scoped(input)` and keeps the
argument's address lifetime. A pointee retaining a local buffer cannot make
that buffer escape through an unscoped result.

An explicit receiver lifetime overrides the default receiver fact. `unscoped(anchor)
this` declares that the receiver is retained by `anchor`, a parameter of the same
method, so the receiver must outlive whatever is passed for `anchor`:

```camp
uint copyViews(unscoped(destination) this, NameView[] destination);
```

Inside the body, `this` carries `unscoped(destination)`. A value that points into
the receiver, such as the `scoped(this)` result of a call on one of its inline
fields, therefore outlives `destination` and may be stored into it. At a call
site the receiver's fact is compared with the fact of the argument passed for the
anchor, as for an `unscoped(anchor)` parameter: a receiver that is a local
ending before the destination is rejected, a receiver that outlives the
destination is accepted. The anchor must name an input parameter of the method;
an unresolved or circular anchor is rejected at the declaration.

The return default for receiver-bearing declarations is `unscoped(this)`: an
ordinary method returning a pointer-bearing value without an explicit lifetime
relation is treated as returning something the receiver retains, not as escaped.
The body is checked against that promise. A result that points into the
receiver's own memory, such as the address of a field, a slice or element of an
inline fixed-size field, the receiver's own address, or the `scoped(this)` result
of a call on the receiver or its inline fields, is rejected unless the method
declares the result `scoped(this)`. This protects fluent and accessor patterns
from accidentally returning references to short-lived receiver state through a
global-looking type.

The same substitution machinery handles interface conversions. A struct-to-
interface conversion that materializes a scoped temporary carrier cannot produce
an escaped interface pointer, so converting scoped struct storage to an escaped
interface is rejected at the conversion. A class interface conversion can
usually produce a stable interface pointer through stored class state, but the
class receiver lifetime still determines whether the produced interface pointer
may escape, and only an escaped source can produce a pointer to an escaped
interface.
For a struct witness default method, the generated adapter may create such a
scoped carrier for the duration of the default call. That carrier must not
escape through the default's receiver or result. A class witness default uses
the object's stored interface slot, subject to the class receiver's lifetime.

## Prepared Result Lifetimes

A transformed prep call without `(new)` produces storage equivalent to a scoped
initialized array. Its value fact must prevent escape through returns, fields,
captured delegates, async frames, iterator frames, conditional joins, loop-carried
state, or other storage that would reject the equivalent scoped array.
This fact is attached when the transformed call is registered, before an
assignment checks the result, and copies retain its stack storage provenance.
Local storage in the same activation may retain the result. A field reached
through a parameter cannot retain that scoped result.

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
An inline field of an `out` struct parameter is checked against the parameter's
output relation, just like assigning the whole struct. The default method
output relation and explicit `out unscoped(this)` both allow receiver-retained
data to be written field by field. Data from an unrelated input needs an
appropriate output annotation; a view into the receiver's own memory needs
`out scoped(this)`.
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
fields derive their behavior from the storage containing the struct. A field
whose type is a pointer to an escaped type is escaped storage in any containing
type, because every value it can hold is escaped.

## Escaped Types

Instance receivers in escaped classes default to escaped receiver facts.
Escaped interface contracts, explicit member receiver requirements, implementation
compatibility, conversions and calls follow
[Receiver Lifetime Requirements](09-interface-vtables-and-dynamic-dispatch.md#receiver-lifetime-requirements).

`escaped class` and `escaped interface` put the lifetime on the type instead of
on each declaration that mentions it. Escaped does not mean the instance lives
on the heap or outlives any particular value; it means the instance's lifetime
is not bound to the call stack. Static storage, heap allocation, reference
counting, and other ownership policies all qualify. Ownership itself (when the
instance is deleted, released, or reset) stays the program's responsibility, as
it does for every escaped value.

The analyzer applies three rules:

- **Pointers are escaped by type.** A parameter, local, result, field, or
  generic substitution of type `T*`, where `T` is an escaped class or escaped
  interface, carries an escaped fact as if written `escaped T*`. Writing
  `escaped` on such a type is accepted and redundant. Writing `scoped` or
  `unscoped(anchor)` on it is rejected, because the type already fixes the
  lifetime.
  A by-value escaped interface value has the same rule: it is implicitly a
  pointer to an escaped vtable. Copying or passing that value does not copy
  vtable storage into an activation frame. Taking its address also exposes the
  slot holding that pointer, so the slot itself must be escaped storage.
- **Conversions need an escaped source.** A conversion that produces a pointer
  to an escaped type, such as a struct converted to an escaped interface it
  implements or a class pointer converted to an escaped interface or escaped
  base class, is rejected when the source is not escaped. A struct adapter over
  local storage is the usual example: it cannot become an escaped interface
  pointer. A global struct or an instance allocated with `new` can.
- **Instances are not declared in a frame.** Storage of an escaped class itself
  cannot be declared in an activation frame. A local or `fixed` local of an
  escaped class, and `stackalloc` of an escaped class or interface, are rejected
  at the declaration. A local escaped interface value may copy an existing
  escaped vtable pointer or hold `default`; an inline vtable initializer would
  create frame-owned backing storage and is rejected. Define that vtable in
  static storage instead.

A struct or non-escaped class cannot declare a by-value field of an escaped
class, including an element of fixed inline storage. Only an escaped class may
contain an escaped class by value. Pointer fields and escaped interface values
are allowed, and static fields do not place an instance inside the containing
object.

Escapedness is fixed at the root of a class hierarchy. A class derived from an
escaped class must also be declared `escaped`, and an escaped class cannot
derive from a non-escaped Camp class. Existing extern and shadow class boundary
rules still apply. An escaped interface may inherit ordinary interfaces, but an
interface inheriting an escaped interface must itself be declared `escaped`.

A struct conversion to an escaped interface uses a carrier stored in the
implementing struct and a static object vtable. The carrier therefore shares
the source storage lifetime; it does not point into the conversion's activation
frame. Conversion does not allocate or transfer ownership of the source.

The standard library's `Allocator` is an escaped interface. Every
`Allocator*`, including the implicit type of `within allocator`, is therefore
escaped, and a heap construction that retains its allocator needs no lifetime
annotation on the `within` parameter. A custom allocator lives in static
storage or is allocated with `new`; an arena on the stack would hand out
pointers into a frame while claiming they are escaped, so it cannot convert to
`Allocator*`.

## Return, Yield, And Delete

Return expressions are checked against the function result lifetime. A result
whose template is tied to a parameter can return a view of that parameter; a
plain return from a local cannot be accepted as escaped.
An array literal's local backing storage cannot be returned as a view, either
directly or through one or more local view copies.

Yield expressions are checked against iterator result lifetimes. Iterator state
may retain local values across yields, so the analyzer must treat lifted locals
and current-value slots as generated storage with real lifetime consequences.
`stackalloc` storage is not lifted: it lives only in one resume activation. A
yield whose expression is the address of such storage, or of an alias still
holding that address, is rejected at the yield, including in generic and erased
code. Yielding the value by copy, or a pointer to storage that outlives the
iterator (for example an `escaped` parameter), remains legal.

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

For `new`, every retained argument and initializer fact must be escaped (or a
benign null/default value), because the constructed object is escaped. Retained
facts are collected and combined as for value construction; an ordinary scoped
constructor input cannot be retained and does not impose this heap requirement.

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

A bound method reference carries its receiver as the callable's hidden context.
Its value fact preserves the receiver's lifetime through inferred and explicit
copies. An escaped delegate target requires an escaped receiver; a scoped target
may borrow the receiver only within its lifetime. An unscoped target must satisfy
the same anchor relations as other context-bearing values. Taking a method on a
local value borrows that local's storage, even when the value contains no pointers.

## Async And Iterator Frames

Async state machines and iterator generators lift values into generated frames.
Those frames can outlive the source statement, so lifetime analysis must run on
the source semantics before lowering and must preserve enough facts for
generated fields and cleanup code.

A pointer-bearing local may copy a retained iterator parameter or a value
derived from it, including after a yield. Lifting the local into the same
iterator state must preserve that lifetime relation; it does not make the
value escaped. This permission does not extend to activation-local stack
storage or temporary arrays.

Important restrictions include:

- async bodies that can suspend must not retain stackalloc-backed or otherwise
  stack-only values in the frame;
- iterator generator bodies cannot retain stackalloc-backed or temporary array
  storage across yields;
- yielded references must be valid for the iterator protocol surface;
- cleanup code generated for `finally`, delegates, and destructor paths must
  not delete or free values through incompatible lifetime contracts.

## Generic Boundaries

Members of generic types are lifetime-checked like other members. Conservative
treatment applies to values whose type is a type parameter, not to every value
in a generic body. Known pointer-bearing fields, parameters, and local values
must satisfy the usual storage and receiver lifetime checks.

Copying an `in T` value is distinct from taking the address of its parameter
storage. Preserve that address origin for escape checks without rejecting
ordinary generic copies. A slot's written lifetime does not become part of its
generic payload or change the operand of its layout capability.

Named `stackalloc` storage, including `stackalloc void<V>`, is unassigned after
each `yield` or `await`. Assigning it again permits subsequent reads. Its storage
is allocated once on entry to each resume activation and must not become a
retained pointer field. Dynamic stackalloc array views remain subject to the existing suspension
restrictions. Heap-backed annotated pointers may be retained; their allocation
lifetime and destructor contract survive lifting and apply to cleanup on both
normal exhaustion and early iterator destruction.

Void-represented generic T values are treated conservatively because they may be
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
