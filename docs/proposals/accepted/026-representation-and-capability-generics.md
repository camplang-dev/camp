# Proposal 026: Representation-And-Capability Generics And Generic Ascription

Proposal date: 2026-10-13

Last updated date: 2026-10-15

Status: Accepted and implemented (DEV-020). Historical record; canonical semantics live in docs/semantics/06-generics-erasure-and-capabilities.md, docs/semantics/07-callable-lowering-and-context-ownership.md and the Camp coding guide.

## Summary

Camp generic parameters select a representation, not a category of available
operations. Operations that require additional knowledge obtain it through
capability parameters.

```camp
// Declaration fragments illustrating the proposed syntax.
void borrow<T>(T* value);
bool equal<T: int>(T left, T right);
T choose<T>(in T left, in T right, bool useLeft, sizeof(T));
void visit<T>(T* value, vtableof(T: Visitor));
string describe<T>(typenameof(T));
```

`Visitor` in these fragments denotes an illustrative interface. Fragments
describe proposed contracts, not complete executable examples.

- `<T>` selects unspecified-size, or *void*, representation.
- `<T: R>` selects one of Camp's supported integer representations.
- Explicit `<T: void>` and the former `any`, `copyable`, and `implements`
  generic constraints are rejected.
- `sizeof(T)` supplies checked copy/layout capability; `vtableof(T: I)`
  supplies a typed witness; `typenameof(T)` supplies type-name information.
- Void-representation inputs remain indirect. No capability permits a direct
  by-value input, even for a known-size, copyable concrete argument.
- Non-void representations support full-carrier bitwise equality, inequality,
  assignment, and default assignment. Fitting floating-point payloads are
  admitted with explicitly bitwise, not floating-point, equality.
- Substitution preserves the argument's underlying source identity and selects
  the destination representation. It does not retain or nest a previous carrier.
- Generic ascription compares constructed signatures, bound generic parameters,
  and capability contracts. Explicitly specialized references preserve their
  complete nominal callable identity.

The proposal defines language behavior, not a compiler architecture or work
plan. All normative statements below describe the proposed language. Existing
canonical semantics continue to govern until adoption.

## 1. Motivation And Goals

The existing model combines several unrelated questions in a constraint:
representation, copy permission, admissible arguments, and interface dispatch.
Explicit capabilities then supply some of those facts again. Constructed
callable types expose inconsistencies when the representation, nominal type,
and physical signature are treated as the same thing.

The goals are to:

1. Give declarations one representation rule and one substitution rule.
2. Make copying, dispatch, and naming independent, explicit requirements.
3. Preserve erased generic execution: a generic declaration has a stable
   calling contract, not a new implementation for each type argument.
4. Make declared erased types and substituted generic types agree.
5. Specify generic interfaces, callable newtype ascription, and method
   references completely enough that acceptance and inferred types do not
   depend on generic parameter spelling.
6. Preserve Camp's nominal boundaries, explicit storage, lifetime discipline,
   and C-facing ABI control.
7. Give every investigated ascription category a deliberate accepted or
   rejected outcome, rather than treating every historical rejection as a
   feature to enable.

### Non-Goals

This proposal does not introduce:

- monomorphization, dependent object layouts, or implementation-per-instantiation;
- by-value void-representation inputs or automatic unknown-size parameter copies;
- interface unions, multiple witnesses for one parameter, or concrete-type
  interface reimplementation/overriding;
- first-class universally generic `fn` or `delegate` values;
- target-directed inference of omitted method-reference type arguments;
- implicit adapters for mismatched signatures or representation-changing arrays;
- generic arithmetic, ordering, or bitwise operators on integer carriers;
- a layout-only capability for non-copyable arguments;
- new function-pointer storage through integer carriers or new `untyped`
  function-pointer support;
- automatic capability retention in structs, additional constructors, or
  constructor-forwarding chains;
- reflection, deep copying, automatic ownership transfer, or implicit
  constructor/destructor authority from `sizeof`;
- a redesign of casts, fences, target specs, calling conventions, allocation,
  suspension, or source visibility beyond the explicit changes below;
- compatibility modes retaining the former constraint syntax.

## 2. Terms And Independent Semantic Facts

For a generic parameter `T`, distinguish:

| Fact | Meaning |
|---|---|
| Binder | The declaration and ordinal that introduce T; its spelling is not its identity. |
| Representation R | Void or a specified integer storage/call representation. |
| Argument A | The source type argument, preserving its actual nominal identity and nested arguments. |
| Represented type R<A> | A stored or callable-signature occurrence after substitution. |
| Read view | The ordinary value exposed by a read or direct call result under erased-value rules. |
| Capability | Operand-specific evidence and, where needed, runtime data authorizing an operation. |

`void` as a representation means unspecified-size storage. It is not the
ordinary no-result `void` type and is not an admissible actual type argument.

A represented type must not be replaced globally by its read/conversion view.
Callable signatures, pointer/array element storage, and nominal identities need
the annotation even where a value read exposes A. Conversely, substitution must
not make every inferred scalar local an erased-storage local.

Within an open generic body, T does not acquire the operations of a particular
eventual A. The body is checked against its declared representation and available
capabilities. A call with `int` cannot grant undeclared arithmetic or copying.

## 3. Declarations And Argument Eligibility

### 3.1 Representation Syntax

| Declaration | Meaning |
|---|---|
| `<T>` | Void representation. |
| `<T: R>` | Specified integer representation R. |
| `<T: void>` | Error; write `<T>`. |
| `<T: any>`, `<T: copyable>`, `<T: implements I>` | Error; select a representation and declare capabilities where needed. |

The integer representations are `byte`, `ushort`, `short`, `uint`, `int`,
`ulong`, `long`, `nuint`, and `nint`, with their target-defined sizes.
Representation aliases, where accepted by the ordinary type rules, resolve to
these representations; they do not create a new capability or representation
family. The colon inside `vtableof(T: I)` remains a witness relationship.

Rules apply uniformly to generic types, interfaces, callable newtypes, and
methods. Omission always means void; there is no context-dependent nint default.

### 3.2 Admissible Arguments

Void representation admits any otherwise legal actual type, including ordinary
structs, classes, pointers, optional values, and expanded array/delegate shapes.
Admissibility does not imply copyability or permission to use every position.
Namespaces, open generic definitions, and no-value `void` are not actual types.

Integer representation admits a scalar argument whose complete payload fits
within R on the selected target:

- integer, boolean, and character primitives;
- enums and scalar value newtypes with eligible underlying representations;
- `float` and `double`, including eligible scalar newtypes over them;
- data pointers, including string pointers, interface-instance pointers,
  interface-table values, and witness-table pointers, subject to target pointer
  domain/conversion restrictions.

Interface and witness table pointers are data pointers; nint/nuint have the
capacity for the corresponding target data pointers. Eligibility in a fixed-width
integer carrier additionally requires adequate width and a legal target-domain
transport. Equal machine width alone does not waive target restrictions.

The following remain ineligible for integer representation:

- expanded arrays, delegates, optionals, and other multi-component values;
- direct class or struct values, even if their size would fit;
- function pointers, including function-pointer-shaped nominal types, merely
  because they are pointer-sized;
- any payload wider than the carrier.

`untyped` is not added to the generic integer representation family. Existing
raw-scalar facilities remain separate; this proposal requires no new
function-pointer transport through them.

Check eligibility at type construction and explicit or inferred method
specialization, before attempting an operation with an invalid argument.
Resolve aliases and actual value categories; do not classify by spelling alone.

## 4. Substitution, Forwarding, And Identity

### 4.1 Representation Replacement

The substitution rule is:

```text
parameter T with representation R, argument A       => R<A>
argument already represented as S<A>, destination R => R<A>
```

R is void when the declaration omits a representation. The destination validates
A, not the old carrier S. Thus an int-represented byte supplied to a
long-represented parameter becomes `long<byte>`, not `int<byte>` and not
`long<int<byte>>`. Supplied to a void parameter it becomes `void<byte>`.
A double cannot enter a representation too narrow for double by first placing
it in a different carrier.

Only an outer representation annotation is replaced. An actual nominal newtype
is not peeled away, and nested arguments inside A are not recursively flattened.
For example, if A is a nominal `Container<int<byte>>`, that entire constructed
identity is A. It is not rewritten to `Container<byte>`.

Generic argument binding applies this normalization consistently to construction,
matching, references, and exported identities. It does not make two previously
distinct nominal declarations identical or introduce generic covariance.

### 4.2 Forwarded Abstract Parameters

If the argument is an enclosing parameter, substitution operates on its bound
identity and known representation. It must not create a second erased storage
wrapper or reject legitimate forwarding solely because a surface spelling such
as `void<T>` would previously have been rejected.

Same-representation forwarding preserves the represented storage contract.
Different-representation forwarding requires proof that every admitted A is
eligible for the destination and that the particular value/storage transport is
legal. A general void parameter does not prove that A fits an integer carrier.
Name coincidence between unrelated binders supplies no proof.

The proposal does not add arbitrary nested annotation syntax. Normalization is
a semantic operation over bound types, including types formed by substitution.

### 4.3 Type Substitution Is Not A Storage Conversion

Substitution determines a type. It does not resize an object in place:

- A scalar value conversion may decode and re-encode a known eligible A when
  the ordinary source conversion rules permit that boundary.
- A same-representation storage transfer preserves the represented object.
- A pointer to S-sized elements is not implicitly a pointer to R-sized elements.
- Arrays cannot silently change stride, and `out` storage cannot silently gain
  an allocation/copy-back adapter just because type substitution is valid.
- `in` temporaries and expanded argument packing remain available only where
  the existing argument rules permit them and the destination storage is known.

At an abstract representation-changing boundary, both eligibility and the
required encoding/layout facts must be provable. Knowledge of S's width alone
does not reveal A's signedness, payload width, or floating-point category.
If those facts cannot be obtained from the declared contract, reject that
transport; do not add a hidden type descriptor or assume an encoding. This is
a restriction on that call/conversion, not on the replacement formula.

Likewise, `sizeof(S<A>)` is not automatically `sizeof(void<A>)` or
`sizeof(R<A>)`. Forwarded capabilities must describe the destination storage.

## 5. Placement, Passing, Results, And Optionals

| Surface | Void representation | Integer representation |
|---|---|---|
| Direct by-value input T | Always rejected. | Permitted. |
| Pointer/array-view input | Permitted transport; element operations need their capabilities. | Permitted under ordinary storage rules. |
| `in T` / `out T` | Requires checked sizeof(T). | Copy/layout facts follow from fixed eligible representation; normal mode rules apply. |
| Direct T result | Permitted declaration; implementation/caller storage operations must be justified. | Carrier result permitted. |
| Exact value local T | Explicit stackalloc and sizeof(T). | Ordinary local, not unknown-size stackalloc. |
| Direct T instance field | Rejected: no fixed layout. | Permitted fixed-size field. |
| Pointer/view field | Ordinary lifetime/ownership rules. | Ordinary lifetime/ownership rules. |
| T? | Rejected. | Rejected. |

The void input rule holds with neither capability, sizeof alone, witness alone,
or both; a concrete primitive or copyable struct does not create an exception.
Passing an array view does not pass its elements by value. Existing reference
forms, including error-channel forms where otherwise legal, retain their
ordinary rules and do not provide a bypass around copyability.

A direct void result uses the existing erased-result contract: caller-provided
whole-value storage, including every component of an expanded A. A direct call
reads its result as A. A callable signature continues to say `void<A>`;
neither a plain A result nor a trailing `out A` is an alternative spelling for
that result during signature matching.

A result declaration may be valid without sizeof. A body that copies into
result storage still needs copy/layout proof. A genuinely copy-free forwarding
operation does not acquire a new storage requirement merely from its result
declaration. This distinction must not become permission to copy an arbitrary
non-copyable A.

Optional rules are unchanged: an erased/carrier payload is not permitted, so
`T?`, `void<A>?`, and `R<A>?` are invalid. An ordinary optional supplied as A
under void representation is different: `void<int?>` holds an ordinary optional.
`T[]?` has an array-view payload, not a direct T payload, and follows existing
optional-array rules. Nested ordinary optionals are not reintroduced.

## 6. Capabilities

Capabilities attach to exact bound operands, not parameter names or coincident
physical slot types. A signature is checked even if its body does not use a
declared capability. Adding/removing a requirement changes its contract.

Concrete calls may supply capabilities automatically using the established
capability-parameter mechanism. Abstract calls must forward valid evidence or
derive the relevant fact from a sufficient declared representation. A caller's
knowledge cannot change the generic body's representation or permitted operations.

### 6.1 sizeof: Copy And Layout

`sizeof(T)` as a capability parameter requires A to be semantically copyable and
supplies the size of T's represented storage. Its runtime size payload need not
carry a runtime copyability flag; copyability is a checked source obligation.

| A | May satisfy sizeof(T)? |
|---|---|
| Ordinary copyable scalar or struct | Yes. |
| Copyable expanded view/delegate/ordinary optional | Yes; size covers the complete stored value. |
| Direct class, fixed/non-copyable struct, fixed-array value | No. |
| Pointer to a non-copyable object | Yes; only the pointer value is copied. |

An ordinary concrete `sizeof` expression remains a size query. Knowing a
class's size does not make the class copyable. A plain `nuint`, arbitrary byte
count, or sizeof for a different operand does not satisfy this capability or
grant permission to copy. Explicitly supplied size payloads remain subject to
the same operand and copyability checks as automatically supplied ones.

`sizeof(T*)` proves pointer-size facts, not T's stride or copyability.
`sizeof(T[])` describes the view, not its elements. Capabilities must not be
borrowed from another same-width type.

For void representation, the size is A's whole erased storage size; for
`int<byte>`, it is the int storage width, not one byte. Array indexing, copying,
and allocation must agree on that same size. Generic storage retains the
target's ordinary alignment guarantees for admitted A; size is not permission
to under-align it. No additional alignment capability is introduced.

Available sizeof authorizes, subject to ordinary mode/lifetime/constness rules:

- in/out value transport and copying between valid storage objects;
- assignment, default assignment, and copied direct results;
- exact unknown-size stackalloc locals;
- indexing, element addresses, element read/write, slicing, and stride;
- permitted array allocation, default fill, and element enumeration;
- result materialization, expanded packing, and required copy-back.

Fixed integer representation supplies its own carrier copy/layout facts.
Declaring an explicit sizeof slot still makes it part of the callable contract.

Copying an array or delegate copies the value's components, not its referents or
captured graph. Default assignment means Camp's ordinary value default, not
construction or allocation. Copying does not grant a destructor or transfer
ownership. Existing constness, definite assignment, aliasing, and escape rules
continue to apply.

Combining copyability with size deliberately removes a layout-only generic
capability for non-copyable elements. A witness-only class/fixed-struct algorithm
can dispatch without sizeof, but cannot request ordinary generic element stride
or raw T allocation by pretending a numeric size grants the missing authority.
Arrays of pointers are an available alternative where appropriate. A separate
layout-only capability is deferred.

### 6.2 vtableof: One Typed Witness

`vtableof(T: I)` requires T's argument to be a direct class or struct implementing
the specified constructed interface I. This requires void representation.
An integer, a pointer to an implementing object, or an interface-instance pointer
is not itself such an implementing type argument.

The capability supplies dispatch, not size, copying, or allocation by raw size.
It preserves the established witness/interface distinction:

- An ordinary witness slot has an unbound `fn` receiver T*.
- Substituting A preserves `void<A>*` in that witness receiver's canonical
  source signature; it does not substitute plain A under a special rule.
- An interface table's ordinary slot instead receives an interface-instance
  pointer I*. Interchange with a witness receiver is rejected at source level.
- A witness constructor slot has no receiver and returns the appropriate T*.
- A witness destructor slot uses the type's destroy contract, not raw operator
  delete, preserving allocator and retained-allocator requirements.
- Inherited interface slots and their substitutions remain part of the witness.

One shared erased native table layout may serve both table roles. Source typing
must distinguish them regardless of physical pointer compatibility. No additional
per-instantiation generic method body or duplicate table type is required.

A signature permits at most one witness capability for each bound parameter.
Duplicate witnesses are rejected even for the same interface; different
interfaces for one T are not a union/intersection feature. Distinct T and U may
each have a witness. A witness for a derived interface may supply its inherited
contract only through existing interface inheritance rules, not through arbitrary
substitution of an unrelated witness.

Witness availability checks both the implementation relationship and the actual
transported witness. Merely constructing a callable with a valid A does not
justify omitting its witness argument at invocation.

### 6.3 Combining sizeof And A Witness

| Actual argument | Witness only | sizeof plus witness |
|---|---|---|
| Ordinary copyable implementing struct | Allowed. | Allowed. |
| Direct implementing class | Allowed. | Rejected as non-copyable. |
| Fixed/non-copyable implementing struct | Allowed. | Rejected as non-copyable. |
| Pointer to an implementing object | Rejected as the witness operand. | Rejected. |

Both capabilities enable copying and dispatch on ordinary implementing structs
through pointer, in/out, or other existing legal indirect forms. They do not
permit a direct T input, a variable-size field, or arbitrary generic destruction.

### 6.4 typenameof: Exact Type-Name Information

`typenameof(T)` supplies the source type-name contribution for its exact operand.
It gives no layout, copy, dispatch, or runtime reflection authority. It is not
the native symbol spelling and ignores `@symbol` overrides.

For a known represented argument, typenameof follows the carried identity A,
not the integer/void carrier. Existing composed-type naming rules continue to
apply. A name need not uniquely identify a constructed nominal type; type
identity must never be compared using the returned string.

For an abstract operand the capability must be in scope, retained where permitted,
or otherwise derivable under existing exact-operand rules. `typenameof(T)` does
not grant `typenameof(T*)`, `typenameof(T[])`, or `typenameof(const T)` implicitly.
An ordinary string default that invokes a known typenameof expression is not a
capability solely because of that initializer. The operand remains a type, not
a runtime expression. Specialized `typenameof(classtype)` usage is unchanged.

Annotated types continue to have type names but no method-symbol contribution:
they are not newly permitted as an `overload` selector or a declared `this`
receiver type. Ordinary calls on an annotated pointer's permitted read view and
witness-mediated generic calls are separate from declaring such a receiver.

## 7. Retention, Local Storage, And Lifetimes

### Class Capabilities

Camp permits one constructor per type. Capabilities requested by a generic class
constructor are retained as semantic class state for subsequent instance
operations. This includes size, name, and witness values as declared; their
operand identities must survive retention.

An instance method must not request a second/replacement witness for an owner
parameter whose witness the constructor retains, even for the same interface.
A genuinely distinct method-owned parameter may request its own witness.
Another method's local capability is not automatically class-wide retained state.
Constructor requirements constrain construction; an unrelated method-local
requirement does not constrain every use of the class.

Retained capabilities do not add nominal arguments such as `Box<A, hasSize>`.
They do not make unknown-size direct fields legal. Generated retained state
remains semantic implementation state, not newly source-visible ordinary fields.
Existing generated naming and collision rules continue to apply.

### Struct Capabilities

Generic structs do not automatically retain constructor capabilities. A method
must receive what it needs on each call or use deliberately stored capability
state through an already supported, semantically valid retention mechanism.
Default or aggregate initialization does not manufacture capability knowledge.
No class-style fields are synthesized for structs.

Manual retention must preserve the operand/interface relationship; an unrelated
size integer, string, or raw pointer field does not automatically become a
capability. This proposal adds no new manual-retention syntax. Per-call supply
is the general fallback where an existing storage form cannot express that
relationship safely.

### Exact Generic Locals And Suspension

An exact void-represented local requires explicit `stackalloc T` and sizeof(T).
An ordinary T local, or `auto` that would silently require unknown-size storage,
is rejected. A specified-size representation uses an ordinary local instead.
Array views and pointers are not exact unknown-size T values.

The existing rule applies outside generators as well as inside them: storage is
allocated once on function entry, while initializer effects occur at the written
declaration. A resumption is a new entry for this purpose. An unknown-size
stackalloc local or foreach target becomes unassigned after yield/await and
cannot be read until source assigns it again. Stack addresses do not survive
suspension merely because capability values do.

Generated iterator, async, and closure state must retain/thread the declared
capabilities required by later operations and respect lifetime restrictions.
Any existing implicit size requirement for a generated operation becomes a
checked copy/layout obligation, not a way to grant copying of non-copyable A.
Generic prep operations acquire each capability once and reuse it across the
existing sizing/filling protocol. Neither mechanism creates another witness or
changes the declared representation.

## 8. Integer Carrier Values And Equality

### 8.1 Fixed Operation Set

Open integer-represented T and direct R<A> carrier operations support:

- `==` and `!=` with compatible represented operands;
- assignment;
- assignment of `default`.

They do not expose integer arithmetic, ordering, shifts, or bitwise computation
merely because R is an integer. Existing explicit conversions remain available
where already legal. Operations on a separately read/converted ordinary A are
governed by A, not by a newly inferred capability for the generic body.

### 8.2 Compare The Entire Carrier

For compatible direct carrier operands, equality compares every bit of R;
inequality is exactly its logical inverse. It does not decode A, mask unused
bits, canonicalize payloads, or call a payload equality operation.

This carrier equality must be selected before the ordinary top-level read view
could discard its annotation. Reading/converting a value into an ordinary A
local first instead performs ordinary A comparisons. This is an explicit
exception to treating all direct carrier operations only through their read view.

Existing nominal compatibility still applies. Equal width is not permission to
compare unrelated nominal arguments or silently convert differently represented
operands. A permitted explicit conversion can establish a common operand type.
This rule does not grant equality to unspecified-size void storage, arrays, or
objects that otherwise lack it, and does not change pointer equality.

| Floating-point payloads, with equal unused carrier bits | Carrier == |
|---|---|
| Identical finite-value bit patterns | true |
| Positive zero versus negative zero | false |
| Identical NaN bit patterns | true |
| Different NaN bit patterns | false |

Callers requiring ordinary floating-point equality must perform that operation
separately. They may normalize signed zero before carriage when that suits the
algorithm. There is no implicit normalization and no promise that NaNs compare
unequal in carrier comparisons.

### 8.3 Proposed Encoding Rules

Encoding is deterministic over the entire carrier and bit-preserving for the
payload. The following resolves the assessment's wider-carrier recommendation:

1. Encode the payload in the low-order payload-width bits of R. Bit significance,
   not a hard-coded memory byte order, defines this placement.
2. Signed integral payloads use their target integer representation and sign
   extension into a wider R. Unsigned, boolean, and character payloads use their
   ordinary target representation and zero extension. Enums and scalar newtypes
   follow their underlying payload category while retaining nominal identity.
3. Floating payloads retain their exact target bit representation and zero-fill
   the unused high bits. This is not a numeric float-to-integer conversion.
   Decoding preserves signed zero and NaN payload bits; no arithmetic is needed.
4. Data pointers use the target's defined reversible data-pointer/integer
   transport and fully initialize any additional carrier bits. No new pointer
   domain conversion or truncation is authorized by this rule.
5. Typed construction/conversion initializes every carrier bit. It must not
   leave high bits dependent on temporary storage contents.
6. Same-representation assignment copies every bit, including already initialized
   noncanonical unused bits. `default` encodes A's ordinary default into the
   complete carrier; it never leaves part of the destination untouched.
7. A permitted representation-changing value conversion decodes A and encodes
   it in the destination. It does not preserve arbitrary unused bits of the old
   representation. Abstract conversions remain subject to §4.3's proof rule.

These rules define representation transport, not broader implicit cast permissions
or literal acceptance. An initializer still must be valid for A; a wide R does
not make an out-of-range byte literal valid.

Raw/interop producers are responsible for initializing every bit and for any
normalization their algorithms require. Different initialized unused bits make
equal payloads compare unequal. Truly uninitialized storage is not made safe
to read by the bitwise comparison rule. This is not a portable wire encoding:
target payload formats, pointer representations, and memory byte order remain
target-defined.

## 9. Generic Ascription And Signature Matching

### 9.1 Common Rules

An ascription is valid only after the full constructed target is known.
Comparison proceeds conceptually as follows, without requiring a particular
compiler data structure:

1. Bind owner and method parameters by declaration/ordinal; preserve their
   distinct scopes even where source names coincide.
2. Resolve aliases and validate all actual target arguments and obligations.
3. Substitute owner and constructed target arguments without discarding them.
4. Map corresponding method-owned binders by position where comparing generic
   method contracts; parameter names are immaterial.
5. Apply representation substitution to source signatures, including nested
   callables, then apply the established signature conversion view.
6. Check receiver/context, modes, result shape, constness/constof, lifetime,
   availability, target specs, calling convention, and capabilities.
7. Preserve the resulting nominal relationship independently of physical ABI.

No comparison may accept a source mismatch merely because native slots happen
to be pointer-sized. Conversely, permitted erased/concrete compatibility must
not fail just because equivalent generic binders have different names.

### 9.2 Constructed Interface Owner Parameters

An ordinary method of `Check<A>` has fixed owner arguments; it is not a method
whose caller can choose a new A on each invocation. An implementation of
`Check<T>` uses the implementer's bound T through deliberate substitution.

The method's interface marker must designate the constructed interface actually
implemented, not merely a matching base interface name. A method-owned V cannot
turn a nongeneric owner's fixed implementation into `Check<V>` per call.

These rules apply equally to structs and classes, inherited interface slots,
ordinary interface dispatch, witness dispatch, and bound interface references.
A reference extracted from a closed interface must contain no unresolved owner
parameter. Existing rules for optional/default slots and interface inheritance
are preserved; no concrete reimplementation facility is added.

### 9.3 Interface Methods With Their Own Generic Parameters

A generic interface method promises operation for every argument admitted by
its declared representation and capabilities. Its implementation must have:

- equal method generic arity, even when a parameter is unused;
- equal normalized representation for each corresponding parameter;
- compatible substituted source signatures;
- the same required capability-slot contract.

`<U: int>` and `<V: int>` are equivalent under binder mapping. `<U>` and
`<V: int>` are not, even if their occurrences are pointers. A nongeneric concrete
specialization cannot fill a universally generic slot. Extra unused binders
are not ignored. A combined owner/method contract substitutes the owner first
and separately maps method binders.

Weaker/stronger requirement accommodation is deferred. The initial policy is
strict contract matching, not an entailment search or automatic adaptation.

### 9.4 Constructed fn And delegate Newtypes

A constructed callable newtype is a nominal type, not a first-class generic
callable. Ascription may use concrete arguments, a valid enclosing parameter,
or the ascribing method's own parameter, provided the target is valid for the
whole declaration contract.

- Ordinary fn ascription requires the corresponding receiverless contract.
- Ordinary delegate ascription requires the corresponding receiver/context
  contract and preserves its constness and lifetime.
- Existing explicit callable-this forms retain their rules; no new context
  adaptation is introduced.
- A receiverless static method must not use an unavailable owner parameter or
  capture an instance capability merely because the enclosing type is generic.
- A method-owned argument describes a family of constructed references. Explicit
  specialization selects one member of that family, not a universal value.

The inferred type of a successfully ascribed reference is the complete
constructed newtype, never its open definition, a bare name with missing
arguments, or only its structural shape. Interface ascription alone does not
give a method a callable-newtype identity.

Distinct nominal callable types remain distinct despite matching structure.
Target typing does not grant an otherwise forbidden raw-to-nominal conversion.
Existing explicit boundary mechanisms remain governed by the conversion rules.

### 9.5 Capability Slots And Protected Shapes

Capability matching is strict by kind, exact substituted operand/interface, and
position in the callable contract. A plain size integer is not sizeof(T), and
a raw pointer is not a witness parameter. Missing or additional slots mismatch
even if an implementation body ignores a promised capability. No implicit
adapter drops, adds, or rearranges capabilities.

Capabilities already retained by a bound receiver can justify its body without
adding explicit call slots. Capturing that receiver must preserve those facts
and its lifetime. This does not excuse an explicit slot mismatch against the
ascribed target or let a receiverless fn secretly capture state.

Protected signature distinctions remain:

- A non-expanded `in void<A>` may match `in A` where the established view allows.
- Whole stored expandable `in void<int?>` does not match distributed `in int?`.
- An R<A> inside a callable signature retains its carrier/newtype meaning; it
  is not a plain A or plain R parameter/result merely because widths match.
- A void<A> callable result stays protected at every nesting depth. Plain A
  results and trailing out parameters do not fill that slot.
- Existing exact written-shape rules for virtual overrides remain exact.

These distinctions apply recursively to nested callable arguments and results.
Value conversions at an ordinary call do not silently become callable adapters.

## 10. Method References And Their Types

### Explicit Specialization

```camp
auto action = box.isEmpty2<byte>;       // Reference selection; no invocation.
bool result = box.isEmpty2<byte>(value); // Invocation.
```

Parentheses distinguish invocation, including zero-argument methods. Bare
`method<A>` is not an implicit zero-argument call. Direct generic calls retain
their existing explicit/inferred argument rules; reference formation is separate.

A method reference with unresolved method-owned parameters is rejected at
formation, even when they are unused. A closed target type does not infer them
in this proposal. Supplying an enclosing parameter explicitly is permitted
when its representation and capabilities satisfy the target; it denotes that
bound parameter, not a fresh universal callable parameter.

### Worked Nominal Example

```camp
newtype delegate bool EmptyCheck<U: int>(U value);

class Box<T: int>
{
	bool isEmpty(T value): EmptyCheck<T>
	{
		return value == default;
	}

	bool isEmpty2<V: int>(V value): EmptyCheck<V>
	{
		return value == default;
	}
}
```

Given a valid receiver `box` of constructed type `Box<short>`:

| Expression | Type or outcome |
|---|---|
| `box.isEmpty` | `EmptyCheck<short>`; callable input is int<short>. |
| `box.isEmpty2<byte>` | `EmptyCheck<byte>`; callable input is int<byte>. |
| Bare `box.isEmpty2` as a value | Error: unresolved method argument V. |
| `EmptyCheck<byte> f = box.isEmpty2` | Error: no target-directed specialization inference. |
| `box.isEmpty2<W>` inside a suitable generic scope | `EmptyCheck<W>` tied to that enclosing W. |

Without the callable-newtype markers, corresponding valid bound references have
structural delegate types instead: for example `delegate bool(int<short>)`.
An interface member reference similarly has its substituted structural shape
unless independently associated with a callable newtype.

The void-representation counterpart must change its input, not simply remove
`: int` from the by-value example. For example, a borrowed pointer callable can
declare `newtype delegate bool BorrowedCheck<U>(U* value);`; a copying in/out
contract additionally declares sizeof(U). Neither witness nor sizeof makes
`newtype delegate bool EmptyCheck<U>(U value);` a valid void-input declaration.

A concrete declaration ascribed to `EmptyCheck<short>` must match its
int<short> input, not plain short. Enclosing/method arguments use the same rule
after substitution, rather than a special name-matching shortcut.

## 11. Stable Calling Contracts And Separate Compilation

The language retains one erased calling contract per generic declaration.
Selecting A changes source checking and represented identities, not the body's
representation or available operations.

- Integer-represented slots are transported as their declared carrier, including
  through interface slots, callable newtypes, and nested callable signatures.
- Void-represented expanded arguments use whole-value storage where required,
  not a pointer to only their first component.
- Ordinary expanded parameters retain all components before result/capability
  processing; array views keep pointer and length, delegates keep call and context.
- Erased result storage follows the existing ABI result-position rule: after
  ordinary source argument components and before the ABI's supplied support
  slots. Source results are not rewritten into ordinary source out parameters.
- Capability values preserve their declared relative order and semantic operand
  association through the existing allocator/error/result support conventions.
- Source-level constructor, destroy, interface-instance, and witness receiver
  distinctions remain valid even when a native ABI erases their pointer types.

No cast may hide a wrong source contract or replace an integer slot with a
pointer slot. Target-supported native casts for already validated ABI-compatible
signatures remain allowed; this proposal does not demand extra adapter thunks
for portability unrelated to the language contract.

Exported source/API descriptions must preserve representation declarations,
capability kinds and operands, complete constructed nominal arguments, generic
binder ownership, protected erased results, and relevant mode/lifetime/spec
information. The serialized format is not prescribed here. A separately compiled
consumer must reach the same applicability, reference type, and call-shape
decisions as a same-source consumer. Existing visibility filtering of annotated
types remains in force and must not expose private source identities.

This is a source and ABI change. Artifacts based on the old default representation
or old witness substitution must not be treated as interchangeable with the new
contracts. Rebuilding affected producers and consumers is required; a compatibility
mode or binary adaptation layer is outside scope.

## 12. Diagnostics

Reject invalid source at the relevant language boundary, not only after native
emission or at invocation of an already invalid callable. Diagnostics should
identify the actual reason and the source operation, including:

- removed constraint syntax or explicit void representation;
- inadmissible argument category or insufficient target carrier width;
- forbidden direct void input, suggesting a legal indirect form;
- missing copy/layout capability, stride, witness, or exact name operand;
- a non-copyable argument offered to sizeof, even when that slot is unused;
- duplicate witness or attempted re-supply of a retained owner witness;
- illegal unknown-size field/local or stack storage surviving suspension;
- inability to prove a representation-changing transport;
- mismatched constructed interface marker, method arity, representation, mode,
  result shape, capability slot, receiver, or nominal callable identity;
- unresolved method-owned parameter in a reference and the need to specialize it.

Errors must not recommend removed `any`, `copyable`, or `implements` constraints.
An error caused by an invalid parent declaration must not be reported as evidence
that a child reference feature is unsupported.

## 13. Migration And Documentation

| Existing intent | Migration |
|---|---|
| Borrow-only any | `<T>` with legal pointer/view or other justified indirect transport. |
| copyable with layout/value operations | `<T>` plus sizeof on the operation or constructor needing it. |
| copyable without sizeof | Audit intent: removing the constraint broadens callers; a checked size capability preserves copyability restriction. |
| implements I | `<T>` plus explicit or retained vtableof(T: I). |
| Omitted former nint representation | Write `: nint` to preserve that representation, or deliberately redesign for void. |
| Explicit integer representation | Retain R; apply new eligibility, fixed operations, encoding, and equality. |
| Bare generic postfix used as a call | Add invocation parentheses; bare postfix selects a reference. |
| Carrier argument relying on retaining its former representation | Apply destination replacement and review value/storage boundaries. |

Migration is semantic, not a bulk deletion of constraint words. Standard-library
and package APIs must preserve intended input modes, copying restrictions,
capability retention, and exported ABI deliberately. No new compatibility aliases
for retired constraint spellings are proposed.

On adoption, update the canonical semantic chapters coherently: generic binding,
expanded forms, conversion views, signature compatibility, callables, interfaces,
construction/allocation, lifetime/suspension, and API/metadata contracts. In
particular, replace both the old constraint-category table and the old
already-carried-argument collapse rule; neither remains a fallback rule.

Update the language guide with ordinary usage and migration examples, and the
LLM coding guide/related coding guidance with compact reminders. Remove stale
examples of omitted-nint generics, by-value void inputs, automatic struct
retention, or copying authorized only by a constraint. Once adopted and integrated,
canonical docs are authoritative; the proposal becomes a historical design record.

## 14. Recommended Conformance Surface

This is a language acceptance surface, not a requirement for a particular test
framework or one test per row. Reuse or extend coherent fixtures where sensible;
separate positive and negative cases sufficiently that an unrelated error cannot
mask the rule being checked. Prefer observable source behavior and diagnostics,
with type/ABI evidence where runtime output alone cannot establish the contract.

### 14.1 Representations, Capabilities, And Storage

| Family | Required cases and observations |
|---|---|
| Syntax/default | Omitted void; each integer spelling; normalized aliases; explicit void and retired constraints rejected. |
| Eligibility | Fitting signed/unsigned, bool/char, enum/newtype; class/struct/expanded exclusion from integer R; oversized payload rejection; concrete and abstract arguments. |
| Floating carriage | float/double at equal and wider widths, oversized rejection, bit-preserving round trips, floating-backed nominal types. |
| Carrier operations | Assignment/default/equality accepted; arithmetic, order, shift, and bitwise computation rejected in open generic bodies. |
| Bitwise equality | Nonzero finite patterns, +0/-0 distinction, equal/different NaN payloads, != inverse, differing initialized unused bits, nominal incompatibility. |
| Encoding | Deterministic full-width initialization; signed/unsigned extension, wider float zero fill, same-R full-bit copy, complete default, legal re-encoding. |
| Pointers | Non-null data/string/interface/witness pointers round-trip; natural/fixed-width limits and target domains; integer-carrier function pointer rejection. |
| Input/result separation | Direct void input rejected with neither/either/both capabilities, including primitive and copyable struct A; integer by-value accepted; direct erased results independently validated. |
| Size obligation | Copyable argument accepted, class/fixed struct/fixed array rejected even for unused capability; pointer-to-noncopyable accepted. |
| Exact capability operands | Correct implicit/explicit/forwarded size/name/witness; wrong operand, ordinary numeric substitute, missing witness, and unrelated name facts rejected. |
| Element/storage operations | Nontrivial index/stride/slice, in/out copy-back, default fill, allocation, foreach, result copy, and expanded values with all components checked. |
| Unknown-size locals | Explicit stackalloc required in ordinary and generator bodies; auto/ordinary exact local rejected; initialization in loops remains at source location. |
| Suspension/prep | Post-yield/await reads require reassignment; no stale stack escape; lifted capabilities survive; prep evaluates capability acquisition once. |
| Optional boundary | T?, R<A>?, void<A>? and nested optional rejection; void<int?> and legal optional-array values accepted and fully copied. |
| Substitution | Same-R forwarding; S<A> to R<A>; destination width/category checks; preserve actual nominal A and its nested arguments. |
| Transport boundaries | Known legal scalar re-encoding; reject unproved abstract re-encoding and implicit stride-changing pointer/array/out transport; no hidden adapters. |
| Retention | Class constructor size/name/witness survives subsequent bound calls; no accidental method-to-class retention; no generated struct retention. |
| Witness multiplicity | Duplicate same/different-interface witnesses for T rejected; independent T/U accepted; retained owner witness re-supply rejected. |
| Combined capability | Copyable struct copies and dispatches indirectly; direct class/fixed struct remains witness-only; no by-value loophole. |
| Lifecycle | Class and struct witness calls, inherited slots, constructor result and destroy receiver, retained/explicit allocator behavior, ordinary interface/witness receiver mismatch rejection. |

Raw-bit tests must use initialized storage and supported low-level operations.
Do not make undefined/uninitialized reads an expected behavior. For pointer and
carrier ABI cases, zero/null-only examples are inadequate.

### 14.2 Generic Ascription Matrix Derived From DEV-019

The investigation's I/M/F/D/U/J/C/R families provide the research categories
below. Their historical accepted/rejected outcomes are not this proposal's
oracle. Rewrite former any/copyable/implements cases into representation plus
capabilities, and treat former omission as void; add explicit nint controls
where the old native-size representation was the subject.

| Research family | Recommended conformance cases | Required outcome |
|---|---|---|
| I — interface owner argument | Concrete byte/int/nominal arguments; generic owner forwarding with different binder names; class and struct implementations. | Instantiate full owner target, accept compatible implementations, preserve all arguments. |
| I — marker identity | Correct constructed marker versus same interface with a different argument; method-owned parameter trying to change fixed owner identity. | Correct marker accepted; unrelated constructed identity rejected. |
| I — closed references | Extract, assign, pass, return, and invoke a member of a closed interface. | Substituted structural type, no unresolved owner binder, dispatch preserved. |
| M — generic interface method | Same and renamed binders; owner-plus-method parameters; void and explicit integer reps; class and struct receivers. | Names irrelevant; owner substitution and method mapping distinct; runtime arguments/results correct. |
| M — universal contract rejection | Extra/missing unused binders, nongeneric specialization, different representations including pointer-only occurrences, missing/extra capabilities. | Reject rather than silently weakening or adapting a universal slot. |
| F — constructed fn | Receiverless ascription to concrete, method-owned, and otherwise valid in-scope target arguments; nominal scalar and expanded void payloads. | Fully constructed nominal fn identity; correct source contract and invocation. |
| F — receiver negatives | Receiver-bearing declaration for ordinary receiverless fn; unavailable static-owner parameter/capability. | Reject at declaration; do not misclassify as a generic matching failure. |
| D — constructed delegate | Class/struct bound receiver; owner- and method-dependent ascription; receiver mutation/constness/lifetime controls. | Complete nominal delegate identity and correct context capture. |
| D — receiver negatives | Receiverless declaration for ordinary receiver-bearing delegate; invalid receiver escape/constness. | Reject; no fabricated context. |
| U — user reference examples | m1 on closed owner, explicitly specialized m2, unresolved m3, closed target without explicit method args, explicit enclosing W. | Types/outcomes exactly as §10; no universal callable or coincident-name capture. |
| J — instantiated carrier ascription | Concrete int<short> declaration ascribed to a short-constructed int-representation newtype; equivalent nongeneric nominal control; raw reference control. | Constructed ascription works without a raw-to-nominal loophole. |
| C — protected result/input forms | Non-expanded erased in compatibility, whole expanded storage, erased results versus plain/trailing-out results, nested callable results. | Apply recursive signature rules; preserve protected result and component shape. |
| C — capability contracts | Size/name/witness order permutations, matching/missing/extra slots, incorrect operand, invalid witness argument, retained receiver facts. | Validate every contract and transport each declared capability correctly. |
| C — nominal controls | Same constructed callable pass/assign/return/invoke; distinct newtypes and raw shapes with identical ABI; different arguments. | Intended nominal crossings only; structural match alone insufficient. |
| R — nonzero ABI controls | Narrow signed/unsigned values, explicit nint width boundaries, non-null pointers, distinct owner/method arguments, returned values. | Correct carrier width/category on direct, interface, witness, fn, delegate, and nested paths. |

Exercise the four core families (interface owner, interface generic method,
fn newtype, delegate newtype) across these orthogonal axes without blindly
reproducing every exploratory combination:

- void and explicit integer representations, including explicit nint;
- concrete, enclosing-owner, and method-owned target arguments where legal;
- same-spelling versus alpha-renamed binders and distinct owner/method binders;
- class versus struct receiver/conformance paths;
- pointer, in/out, direct carrier input, expanded whole storage, and direct result;
- no capability, size, witness, name, and meaningful valid combinations;
- ascribed versus structural reference controls and positive/negative nominal cases;
- formation, inferred type, assignment/passing/return, invocation, and native ABI.

A rejected parent cannot establish its child's inferred type. Recovery types
are not accepted callable types. A program that only emits successfully has
not demonstrated executable transport. Type evidence must establish the
complete constructed identity independently of permissive assignment behavior.

### 14.3 Separate Compilation And Target Coverage

Use producer/consumer checks to verify that exported declarations retain the
same arguments, representations, capabilities, and protected signatures. Exercise
nonzero carrier and pointer values across that boundary, including constructed
callable values and both struct/class dispatch.

Cover supported target width differences, calling conventions, and native
backends where they affect these contracts. Required evidence includes semantic
acceptance/rejection, exact source types, successful execution of valid cases,
and ABI shape/order checks not observable from output alone. A single host or
zero-only runtime success does not prove all target contracts.

Test development should use focused, coherent cases; broad integration validation
belongs at completion rather than after every small correction. No fixed suite
names, compiler internals, or platform tooling are prescribed by this language
proposal, and no tests were run in preparing it.

## 15. Deliberate Tradeoffs And Review Boundaries

The proposal adopts the assessment's recommendations as concrete proposed rules:
strict generic/capability contract matching, explicit reference specialization,
no target-directed reference inference, no layout-only capability, and
deterministic full-width encoding with zero-filled wider floating payloads.
These are design choices for review, not claims that all were previously
canonical or implemented.

The important costs are intentional:

- A default-representation change requires source and ABI migration.
- Coupling size and copy excludes generic stride-only use of non-copyable objects.
- Bitwise equality has signed-zero/NaN/unused-bit behavior unlike payload equality.
- Replacement of a representation is not an automatic physical storage adapter;
  some abstract cross-representation calls need rejection rather than guesswork.
- Strict ascription rejects some theoretically adaptable implementations until
  a separate, explicit adaptation/entailment design exists.
- Pointer/value layout and erased result contracts still need precise source
  descriptions even though execution remains erased.

These limits keep the three-capability model finite and avoid introducing
reflection, hidden descriptors, universal callables, or speculative interface
unions merely to accept isolated examples.

## References

Canonical semantic context (rules explicitly changed above supersede the older
forms only upon adoption):

- [Generics, erasure, and capabilities](../../semantics/06-generics-erasure-and-capabilities.md).
- [Expanded forms and ABI shapes](../../semantics/02-expanded-forms-and-abi-shapes.md).
- [Conversions and raw carriers](../../semantics/03-conversions-raw-carriers-and-fence-casts.md).
- [Signature compatibility](../../semantics/04-constof-and-signature-compatibility.md).
- [Callable lowering and ownership](../../semantics/07-callable-lowering-and-context-ownership.md).
- [Interface and witness dispatch](../../semantics/09-interface-vtables-and-dynamic-dispatch.md).
- [Construction, destruction, and allocation](../../semantics/10-construction-destruction-and-allocation.md).
- [Metadata, API surface, and symbols](../../semantics/11-metadata-api-surface-and-symbols.md).

Design inputs were the representation-and-capability generics assessment,
the generic ascription assessment, and the DEV-019 generic ascription research
matrix and response. They are research provenance, not normative implementation
behavior or required companion documents. The ascription assessment's former
constraint/default recommendations are superseded by this proposal. Relevant
decisions and research-derived conformance cases are included above so the
proposal does not depend on external working documents.

The conformance surface is stated in this proposal and does not require retaining
temporary probe projects or treating any particular compiler's behavior as the
language definition.
