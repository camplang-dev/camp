# Generics, Erasure, And Capabilities

This supplement describes the compiler rules for generic type parameters,
erased values, and explicit capabilities such as `sizeof(T)`,
`typenameof(T)`, and `vtableof(T: Interface)`. User-facing syntax is documented
in [Generics And Capabilities](../language/13-generics-and-capabilities.md);
this supplement is about the invariants the compiler must preserve while
analyzing and lowering generic code.

Camp deliberately avoids assuming that every generic type has layout, copy,
default construction, interface dispatch, type-name, or destructor information
available at every use site. The compiler must require the specific capability
needed by the operation being performed.

## Binding Model

Generic parameters are identified by their introducing declaration and ordinal.
A source name is a lookup spelling, not binder identity. Owner substitution and
method binder mapping are distinct operations. Generated iterator, lambda and
interface helpers preserve the original binders deliberately.

Keep binder identity, representation, actual nominal argument, represented
storage, expression read view and exact-operand capabilities separate. A read
view is a comparison/expression device; it never replaces canonical types in
signatures, generic arguments, symbols, metadata or API output.

## Receiver-Relative Type Forms

`this` as a return type and `classtype` are not generic parameters, but they
use similar substitution discipline. They describe source-relative type facts
that are resolved differently for declaration validation, call-site typing, ABI
lowering, callable references, and metadata.

### Plain `this` Return

Plain `this` is valid only as the complete return type of a receiver-bearing
method. It is invalid as `this*`, `const this`, `this[]`, a callable result, a
free-function result with no receiver, a static method result, a constructor or
destructor result, an interface method result, or a callable-newtype result.

During declaration analysis, a `this` return resolves to the method's effective
receiver type for ABI purposes. During call analysis, the source result type is
refined to the static type of the receiver expression used at the call site,
including pointer shape, constness, target specs, and lifetime qualifiers.

For non-extern bodies, the return expression must be syntactically receiver-
preserving: `this`, or a chain of instance calls on `this` where each call also
returns plain `this`. The current rule is intentionally provenance-light. A
local variable that happens to hold the receiver is not accepted as a `this`
return proof.

Bound method references resolve the `this` return immediately using the bound
receiver expression type. Unbound method references and flattened callable
surfaces have no call-site receiver expression, so their return is the method's
ordinary effective receiver ABI type.

### `classtype`

`classtype` is valid only inside class declarations. It is a class-relative
type form: in an open class it means the enclosing class or a derived class; in
a sealed class it denotes the enclosing class exactly. Unlike plain `this`, it
is composable in allowed method-local and signature positions, such as
`classtype*`, `iter classtype*`, or `out classtype*`.

In lowered ABI signatures, `classtype` is replaced by the enclosing class type.
At source call sites, a result or parameter containing `classtype` is rebound
according to the statically known class type used for the call:

- instance calls use the receiver expression's statically known class type;
- static calls use the type named on the left side of the call;
- sealed classes have no derived rebinding, so the enclosing class is exact.

`classtype` may not appear in fields, static fields, globals, aliases, value
newtype underlying types, callable newtype declarations, interface declarations,
or non-class type declarations outside method bodies. A method body may use
`classtype` in locals and casts while it remains inside class scope.

For virtual, abstract, override, and sealed method declarations, `classtype` is
allowed only in result positions: the return type and direct `out` parameter
types. It must not appear in input parameters or nested callable/iterator/async
types inside the virtual signature. This prevents a derived override from
pretending that callers through the base slot supplied the derived class type.

Values of a `classtype` shape may widen to the enclosing class shape. The
reverse direction is not implicit. Returning `this` from an instance method is
the special safe producer for `classtype`; other enclosing-class-to-`classtype`
flows require an explicit cast and must be checked by the conversion
classifier.

## Representations And Eligible Arguments

A parameter declared as `<T>` selects unspecified-size (void) representation.
Omission has this meaning uniformly for types, interfaces, callable newtypes
and methods. A colon selects one of `byte`, `ushort`, `short`, `uint`,
`int`, `ulong`, `long`, `nuint`, or `nint`; resolve aliases before
validating the representation. Explicit `T: void` and the former `any`,
`copyable`, `implements`, and pointer constraints are invalid.

Void admits otherwise legal actual types, including copyable/noncopyable
aggregates, pointers, ordinary optionals and expanded values. No-value void,
namespaces and open definitions are not actual arguments. Integer
representations admit fitting scalar payloads and eligible nominal scalar
types. Direct aggregates, expanded values and function pointers do not qualify.
Width and pointer domains follow the selected target, never the compiler host.
Check construction and explicit/inferred specialization before operations.

An open body has only its representation and declared/retained capabilities.
A concrete argument cannot grant it undeclared copying, arithmetic or dispatch.
Integer representation supplies ordinary fixed carrier copy/layout facts; it
does not supply a witness, type name, constructor or destructor.

### Representation Substitution And Transport

Substitute `T` represented by R with `R<A>`, where R is void on omission.
If the supplied argument already has outer representation `S<A>`, replace
that outer annotation: `R<A>`, never `S<A>` or `R<S<A>>`. Validate A
against the destination R. Preserve actual nominal A and its nested arguments.
For example, a nominal `Container<int<byte>>` remains that complete A.

Same-representation abstract forwarding preserves represented storage and bound
identity. Different-representation abstract transport requires proof of argument
eligibility and its required encoding/layout facts. Representation width alone
does not reveal eventual payload width, signedness or floating category.

Substitution is not an in-place storage conversion. Known legal scalar
conversion may decode/re-encode A, but pointers/arrays cannot change element
stride and out storage cannot gain an implicit allocation/copy-back adapter.
Capabilities describe destination represented storage, not a previous carrier.

### Integer Carrier Values, Encoding, And Equality

An integer-represented value `R<A>` holds A in the low-order payload-width bits
of R. Eligible payloads are integers, booleans and characters; enums and scalar
newtypes over eligible payloads; `float` and `double`; and data pointers
(including strings and interface-instance pointers) for `nint` and `nuint`
only. Width and pointer domains follow the selected target: the pointer must fit
the natural integer width of its own domain, and raw function pointers never
qualify. Classify the actual underlying category; never classify by spelling.

Interim policy (a later decision than proposal 026, whose broader fixed-width
pointer-carrier eligibility discussion is deferred by it): for now, the
supported integer representations for carrying data pointers are `nint` and
`nuint`, subject to the existing target-domain and width rules. Compilers may
require these representations and reject fixed-width integer pointer carriers
even when the pointer would fit. Broader pointer-carrier eligibility remains
open for discussion and is not required at present. This is not a claim that
other representations are fundamentally impossible, and it neither adds
function-pointer carriage nor waives target restrictions.

Encoding initializes every carrier bit:

- signed integral payloads sign-extend and unsigned, boolean and character
  payloads zero-extend, whatever the signedness of C's plain `char`;
- floating payloads keep their exact bits, zero-fill the unused high bits and
  never undergo numeric float-to-integer conversion; decoding preserves signed
  zero and NaN payload bits;
- pointers use the target's reversible data-pointer/integer transport;
- `default` encodes the complete zero carrier;
- assigning the same represented type copies every bit, including initialized
  unused ones, while a representation-changing value conversion decodes A and
  encodes it in the destination.

Open integer-represented parameters and direct carrier operands support
assignment, `default`, `==` and `!=` only. Arithmetic, ordering, shifts and
bitwise computation are rejected on an open parameter; a carrier of a floating
or pointer payload also rejects compound assignment and updates, because integer
arithmetic on its bits would corrupt the payload. Reading a carrier into an
ordinary local of A first makes later operations A's own.

`==` on two carrier operands (carrier storage, a field of a represented
parameter, or the parameter itself) compares every bit of the carrier before
any read view is applied. `!=` is its logical inverse and a `default` operand is
the encoded default. The operands need the same representation and carried type;
equal width is not a conversion. Consequently carried floating values compare
as bit patterns: `+0` and `-0` differ, identical NaN patterns are equal and
different NaN patterns differ. A carrier compared with an ordinary value
compares the read value instead. Raw producers must initialize every carrier
bit: differing initialized unused bits make equal payloads compare unequal.

A pointer, array or `in`/`out` slot of `R<A>` holds R-sized elements. An
argument of plain A storage is therefore not accepted for it (no stride
adapter), an `out` argument needs storage of exactly `R<A>`, and an `in`
argument that is not that storage is encoded into a temporary carrier.

## Erased Versus Materialized Values

Void-represented parameters accept expandable type arguments. Integer
representations reject them. A direct void T input is always invalid; in/out
transport requires checked sizeof(T), while pointers and views can be borrowed
without element copy/layout rights.

Substitution preserves a single declaration ABI. An expandable argument `A`
uses `void<A>` at storage positions: `T*` becomes `void<A>*`, `T[]` becomes
`void<A>[]`, and `in T` takes one storage reference, for scalar as well as
expanded A. Generic argument identities remain distinct:
`List<int[]*>` and `List<void<int[]>*>` do not name the same type.

An erased `T` result, like a written `void<V>` result, uses caller-provided
storage for every argument, including scalars and ordinary structs. The result
pointer follows source parameters and precedes compiler-supplied allocator,
error, size, name, and witness slots. Calls read as `A`; their ordinary
inferred locals never acquire a top-level erased storage type.

A generic declaration cannot write `T?`: an optional payload cannot be a type
parameter of any representation, so substitution never produces an optional of an
optional. A declaration that would have returned `T?` returns `bool` and writes
the value through a trailing `out T` (`List.tryGetItem`), and one that accepted
`T?` takes a nullable `T*` or is redesigned around a `bool`.

An expanded delegate argument contributes both its call and context slots before
the erased result pointer. Adding defaults or capabilities to an already
expanded call preserves those components and the result storage argument.

An erased result under a `within` expression retains its whole result storage
and any optional presence bit. Expanded payloads, including arrays and
callables, are read from that storage after the call. Evaluate the allocator
expression once before the call, and restore the enclosing allocator context
after expanding the result.

An ordinary generic `T[]` result under `within` also evaluates its call once
and retains both the elements result and output length. Supplying an allocator
does not change that array result into whole erased-value storage.

Erased `in`/`out` arguments pass existing erased storage and non-expandable
values directly. Ordinary expandable values are packed into temporary storage;
outputs copy back immediately after the call, before the enclosing expression
continues. Component addresses need not be adjacent.
Optional cast rvalues follow the same rule: lifting a payload creates a true
presence component, while a cast of an existing optional preserves its flag.

Each materialized input or output occupies one argument slot, regardless of
the substituted value's component count. A following `sizeof(T)` capability
remains a separate slot even when the value is an array.

Only a bare generic parameter is a placeholder for whole erased storage.
Derived forms such as `T*` and `T[]` keep their pointer and array calling forms;
a `T[]` passed to a callable retains separate elements and length arguments.

Interface calls, including witness dispatch, use the lowered entry signature and leading
dispatch context. Source `in` parameters retain their storage-reference calling
form after that context, including when the receiver is an erased generic.

Some generic values are erased at source analysis boundaries. Operations that
need layout, size, vtable, type name, construction, destruction, or copy
capability must request the corresponding capability explicitly.

The highest-risk mistakes are:

- indexing `T[]` without knowing element stride;
- copying void-represented T without checked sizeof(T);
- mutating elements through `const T[]`;
- allocating or default-filling `T[]` without size and fill semantics;
- calling interface methods on `T` without a usable vtable;
- lowering an iterator or async frame that needs to store erased values but has
  not threaded the needed capabilities into the generated state.

## Copy And Placement Rules

Direct void-represented T input is always invalid, with neither capability,
size, witness or both. Use a pointer/view or a justified indirect mode.
`in T` and `out T` require checked `sizeof(T)` in their callable contract
or retained receiver state. Fixed integer representations use ordinary mode
rules and need no implicit size slot.

A direct void T result is a valid declaration independently of a copy
operation. Its body/caller materialization needs copy/layout proof when copying;
genuinely copy-free forwarding does not gain a size requirement merely from the
result declaration. An erased result keeps whole caller storage and remains
protected in every nested callable signature. A plain A result or trailing
source out parameter is not that result.

Direct void T fields are invalid: the declaration has one fixed layout.
Pointer/view fields follow ordinary lifetime rules. An exact void T local
requires explicit stackalloc and size; auto cannot choose unknown-size storage.
Integer-represented T uses ordinary locals/fields. T?, void<A>? and R<A>? are
invalid optional payloads; an ordinary optional A supplied under void is
different, so void<int?> and legal optional array values remain usable.

## Size, VTable, And Type Name Capabilities

`sizeof(T)`, `vtableof(T: Interface)`, and `typenameof(T)` parameters transport
capabilities into generic bodies. Lowering must thread those values through
generated calls and helper declarations.
Call lowering preserves their declaration order. Track each generated argument
by its capability parameter so a size, name, or witness argument cannot be
mistaken for a different capability that has not yet been inserted.

### `sizeof(T)`

`sizeof(T)` is checked copy/layout capability. A must be semantically copyable;
classes, fixed structs and fixed-array values cannot satisfy it even if the
slot is unused. Their pointers are copyable. For void T its payload size covers
every stored component; for R<A> it describes R's storage width. An ordinary
numeric size, another operand's size, sizeof(T*) or sizeof(T[]) does not prove
T's copyability/stride. This applies to explicit and automatic supply.

The analyzer should find `sizeof(T)` capability in:

- ordinary function parameters;
- generated iterator state fields copied from a generator parameter;
- generated constructor/init-new parameters and fields for generic classes when
  the class needs the size later.

Implicit `sizeof` arguments can be added at call sites when the concrete type is
known and the callee declares the capability parameter. If no capability is
available inside the generic body, report the operation that needs it.

### `vtableof(T: Interface)`

`vtableof(T: Interface)` supplies a witness capability for generic interface
dispatch. It requires a void-represented parameter with a direct class/struct
argument implementing the requested interface. Its operand relationship is
independent of a size or name capability.

The source type preserves both `T` and `Interface`; it is distinct from the
interface table value `Interface` and interface-instance pointer `Interface*`.
An ordinary slot extracted from the witness is an unbound `fn` with a `T*`
receiver, whereas a slot extracted from an interface table takes `Interface*`.
The capability is not an object pointer or a layout capability. It must be
threaded through:

- generic calls that need interface dispatch;
- class constructors/init-new methods when a generic class stores the vtable for
  later instance methods;
- generated iterator/lambda/helper declarations that call interface members on
  erased generic values.

Concrete `vtableof(Concrete: Interface)` expressions lower to the generated
vtable for the concrete implementation. Generic `vtableof(T: Interface)`
expressions lower to a parameter or stored field, and must diagnose when neither
exists.

### `typenameof(T)`

`typenameof(T)` supplies a source-level type name value. It should be treated as
metadata/string capability, not as proof of layout, copyability, or interface
dispatch. Literal conversion rules for the target string representation still
apply when the capability lowers to a concrete value.

The operand must resolve as a type form, alias, generic parameter, qualified
type name, composed type form, or the specialized `classtype` form described
above. It is not evaluated as a runtime expression. Variables, fields, methods,
enum values, member accesses, and expanded components are invalid operands.

`typenameof(...)` returns the Camp/compiler type-name contribution used for
generated receiver and overload symbols. It ignores `@symbol`; backend symbol
overrides and metadata `symbolof(...)` are separate concepts.

`typenameof` of an annotated type (`void<V>` or an integer carrier `C<V>`) is
the name of the annotated type `V`, with outer pointer, array and optional forms
applied as they are to `V`. `typenameof(void<User>*)` is `"User"`,
`typenameof(nint<ColorType>)` is `"ColorType"` and `typenameof(void<int[]>)` is
`"IntArray"`. The name needs no runtime capability for `V` because the
annotation names it. The receiver of a `typenameof` capability already knows the
declared representation; the argument identity is the part it cannot know.

An annotated type has a name but no symbol. A `void<V>` or carrier type,
including one behind pointers or arrays, can never be an `overload` selector or
the type of a method's `this` parameter, because no method-symbol fragment
would be correct. Write such a function as an ordinary function or method.
Calling the methods of `V` through a `void<V>*` is unaffected.

`typenameof(T)` names exactly the type expression it is written for. It does
not recursively grant name capabilities for related expressions such as `T[]`,
`T*`, or `const T`; those require their own source capability where the
language permits one. `typenameof(classtype)` is valid only in the specialized
default-parameter position used by receiver-relative class APIs and should not
be generalized into runtime reflection.

For erased generic parameters, `typenameof(T)` is available only when the
current function receives a `typenameof(T)` capability parameter or an enclosing
generic class constructor/init path stored that capability in generated class
state. Constructor-requested `typenameof(T)` fields follow the same generated-
field visibility rules as `sizeof(T)` and `vtableof(T: Interface)` fields: they
are semantic implementation state, not source fields.

### Capability Operands, Forwarding, And Retention

A capability belongs to its exact bound operand, never to a parameter name or
a coincident slot type. A call may supply a slot in three ways, and each is
checked against the slot's substituted operand:

- an expression such as `sizeof(T)`, `typenameof(T)` or `vtableof(T: I)`;
- a written evidence name such as `sizeof_T` or `vtableof_T_I`, which resolves
  to the caller's parameter or retained field before it is compared;
- omission, where the compiler supplies the argument from the caller.

Evidence for another operand, an ordinary `nuint` byte count, a plain string,
a raw pointer or a witness for a different interface does not satisfy a slot.
When an omitted slot's operand is the caller's own abstract parameter, the
caller must hold evidence for exactly that operand through its signature, a
retained class constructor capability or generated state. The call is rejected
at the call otherwise, even if the callee never uses the slot. A witness
supplied for a derived interface satisfies a request for any interface it
inherits.

Substitution is simultaneous, so swapped forwarding is distinct from identity
forwarding. A caller `f<T, U>` that calls `pair<U, T>` supplies its U evidence
for the callee's first operand and its T evidence for the second, whatever the
parameters are named.

A signature declares at most one witness for each bound parameter, including a
second witness for the same interface. Two interfaces for one parameter are not
a union. Distinct parameters may each have a witness. A generic class
constructor that retains a witness for an owner parameter makes that witness
the instance methods' witness: an instance method cannot request a second or
replacement witness for that owner parameter. A generic method may request a
witness for its own parameter. A requirement of one method does not become
class-wide state.

A retained witness for a derived interface supplies the inherited slots,
including constructor and destroy slots, with the same projection as a witness
parameter. A destroy through a retained witness runs the destructor with the
retained or explicit allocator contract.

Generic classes retain the requested size, name and witness values as generated
semantic state, and later instance operations use that state. Generic structs
do not retain constructor capabilities: each call receives what it needs.
Ordinary numeric, string or pointer fields, including fields that happen to use
the generated names, never become capability evidence.

### Capabilities In Lambdas, Iterator State And Async Frames

Generated execution state keeps the capability evidence it needs; it never
invents evidence and never acquires a second copy of the same evidence.

- A lambda that uses `sizeof(T)`, `typenameof(T)` or `vtableof(T: I)` captures
  the evidence for that exact operand: the enclosing parameter, or the class
  retained field reached through `this`. The capture is a context field of the
  matching type (`uintptr_t`, `const char *`, a witness pointer) and the lifted
  lambda function reads it through the context. Evidence is captured only when
  the lambda body uses it, directly or by forwarding to a callee that needs it.
- Capturing lambdas require a delegate target. A function-pointer target cannot
  hold a context, so a lambda that needs enclosing capability evidence reports
  `Capturing lambdas require a delegate target.`
- Generator capability parameters, and the implicit `sizeof(T)` for a generator
  whose element type is a bare void-represented `T`, become iterator state
  fields (`_sizeof_T`, `_typenameof_T`, `_vtableof_T_I`). Every resumption reads
  the retained field, so a `typenameof(T)` after a `yield` is valid.
- An async method cannot declare capability parameters. An async method of a
  generic class reads its evidence from the class retained fields through the
  frame's `this`, and `stackalloc T` locals in an async body are allocated at
  the top of the resume function from that evidence.
- `prep` sizing and filling reuse one acquired evidence value; the lowering
  evaluates `sizeof_T` once and passes it to both protocol invocations.

## Generic Arrays And Iterators

Generic arrays need element stride and lifetime-safe element handling. Generic
iterators need element type, state, and cleanup behavior that remains valid
under erasure.

Operations on `T[]` should check the operation precisely:

- indexing needs element stride;
- taking an element address needs element stride;
- enumeration needs element stride and iterator state fields;
- slicing needs element stride;
- mutation needs element stride and non-const element access;
- copying an element value needs copyability;
- allocating `T[]` needs element stride and allocation/fill semantics.

Inside erased substitution for void representation, `T*` means a pointer
to the storage form of `T`. If `T` is an expanded form, that storage form is the
single-object `void<V>` representation. A compiler must not treat a pointer to
one expanded component as a pointer to the whole substituted value.

Iterator generators with void representation add `sizeof(T)` parameters
when the generated state needs element stride. When an iterator lowers into a
state type, these capability parameters become state fields so the `next`
method can run after the factory call has returned.

The `next(T* current)` slot of a generic iterator is one erased slot. When the
iterator is instantiated with an expanded argument such as `int?`, the slot is
whole `void<A>` storage, not the distributed components, and the consumer reads
the foreach target from that storage. A foreach over such an iterator, and a
generic passthrough of it, preserve the expanded value.

### Generic Prepared Arrays

A generic prep array is still an ordinary mutable generic array in the declared
callable shape. Its element type must satisfy the prep copyability rule, and its
scalar result must exactly match the substituted array length component type.
Sizing, allocation, element addressing, and copying request the same
capabilities as equivalent ordinary `T[]` operations.

When a transformed generic prep call produces two protocol invocations,
lowering evaluates or acquires each `sizeof(T)`, `typenameof(T)`,
`vtableof(T: Interface)`, and related capability once and reuses that value for
both. Compiler-supplied capability parameters may follow prep because the
ordinary binder can satisfy them without a caller-written argument.

## Witness-Mediated Generic Dispatch

`vtableof(T: I)` requires void representation and a direct implementing
class/struct argument. It supplies typed dispatch, not size/copy/stride.
The source witness slot's receiver is T* (substituting to void<A>*); an ordinary
interface-table slot takes I*. A shared erased C table does not equate these
source receiver roles. Constructor slots have no receiver and return T*.
Destroy slots preserve ownership and selected/retained allocator contracts.

Member lookup uses the exact available witness. The colon inside vtableof is
the implementing relationship; it is not a generic declaration constraint.
Existing interface inheritance permits inherited slot projection. Capability
requirements are checked even when the body does not use a declared slot.

A witness over a constructed interface (`vtableof(T: Check<byte>)`) fixes the
owner arguments: member lookup substitutes them into the slot, concrete operands
must implement exactly that construction, and call-argument inference cannot
choose a different one. The witness interface text uses payload (not carrier)
arguments, so a forwarded `U: int` owner reads `Check<U>`, never
`Check<int<U>>`.

A generic interface method is matched by ordinal, not by binder name: equal
arity, equal normalized representation and equal capability slots in order. See
semantics 09, Constructed Interfaces, for owner substitution and the lowered slot
shape.

## Generic Construction And Destruction

Generic construction and destruction require the generated helper surface or
capability values needed to allocate, initialize, finalize, and free the value.

The compiler should distinguish:

- constructing a concrete generic instantiation where all type arguments are
  known at the call site;
- constructing erased `T` storage where size, init, copy, or cleanup capability
  must be passed explicitly;
- constructing a generic class whose methods need capabilities later and must
  store them in generated fields.

If a generic body cannot prove a constructor exists for a type argument, it
should report a missing constructor/capability diagnostic rather than lowering a
best-effort default fill.

Destruction is similar. A generic value cannot be assumed to have no destructor
simply because the source body does not name one. If a language feature needs
generic destruction, it should flow through an explicit capability or a
well-defined generated helper.

## Erased Generic Value Storage

An exact erased generic value local such as `T value` is runtime-sized storage.
Source must choose that storage explicitly:

```camp
void swapFirst<T>(T[] values, sizeof(T))
{
	stackalloc T temp = values[0];
	values[0] = values[1];
	values[1] = temp;
}
```

The compiler rejects ordinary `T` locals and `auto` locals that would need to
infer stackalloc storage for an erased generic value. `stackalloc T` requires
`sizeof(T)` in scope and is accepted for void-represented parameters.
Adding a witness does not change these storage rules. Same-representation
forwarding preserves the bound unspecified-size storage without introducing a
second wrapper. A `stackalloc` local of a specified-size type, including a
representation `T`, is an error that suggests an ordinary local. Array views, pointers, and other non-exact shapes do not
trigger this exact-value rule.

Generator bodies follow the same rule with one addition. A local of exact
void-represented `T` that would be lifted into iterator state must be written
`stackalloc T name`; `auto` and a plain `T name` report `Erased generic type 'T'
requires explicit stackalloc local storage`. Fixed integer representations
(`T: int`) keep ordinary locals and reject `stackalloc`.

Storage operations on erased values are size authorized. Indexing, element
address, slicing, in/out copy-back, `new T[n]`, default fill (`default` writes
`sizeof(T)` zero bytes) and foreach all stride by `sizeof(T)`. An expanded
value needs whole storage: `void<int?>[]`, `stackalloc void<int?>[n]` and
`void<int[]>[]` work, while `T?`, `carrier?` and `int<T>?` payloads over an
erased `T` are rejected. Nested optionals over fixed shapes such as
`void<int?>` remain valid.

Foreach over erased generic array values follows the same rule:

```camp
foreach (stackalloc T item in values)
{
	use(item);
}
```

Storage for every `stackalloc` local is allocated once on entry to the function,
even when the local is declared in a loop. Only the allocation is hoisted: the
initializer and its side effects run where the declaration is written. For an
iterator or async body, entry includes each resumption, so a resume allocates
fresh storage and cannot use a stale stack address.

That storage rule does not create a source-level exception to definite
assignment. Every `stackalloc T` local or foreach target becomes unassigned
after a suspension point such as `yield` or `await`. The value may not be read,
passed, yielded, indexed, or used as a member receiver until source assigns it
again:

```camp
foreach (stackalloc T item in values)
{
	yield item;
	yield item; // ERROR: item is unassigned after suspension
}
```

The common iterator-foreach shape is valid because the next loop iteration
assigns the foreach target before the body reads it again:

```camp
foreach (stackalloc T item in values)
{
	yield item;
}
```

Manual iterator consumption follows the same rule. Assigning a value into the
slot after suspension makes the next read valid; merely reallocating the storage
slot during lowering does not.

Generic default slots and materialized generic results must preserve the same
storage identity and capability requirements. The ABI may pass hidden result
or capability parameters, but source diagnostics should describe the missing
source storage or capability.

## Materialized Generic Results

Some generic returns cannot be represented as a simple scalar return in the
lowered ABI. The compiler may materialize a result into caller-provided storage
or rewrite property/index access to operate on generated local storage.

When adding or changing materialized generic return handling, preserve:

- the source result type for diagnostics and metadata;
- the hidden `out` result slot in lowering;
- default/within/vtable/sizeof argument order;
- lifetime facts for the generated storage;
- cleanup behavior for values that require destruction.

Materialized results are one of the easiest places to accidentally drop
expanded form components. See
[Expanded Forms And ABI Shapes](02-expanded-forms-and-abi-shapes.md).

## Static Members

Generic static members belong to constructed generic contexts. Exported static
members must preserve API visibility and generated symbol uniqueness.

Out-of-scope static members should diagnose when a use would require generic
type arguments that cannot be inferred from the access expression. For example,
a nested or generic static method cannot rely on unrelated local type parameters
unless those parameters are visible through the owning constructed type or the
call's explicit type arguments.

The compiler must preserve a distinction between:

- source lookup of generic static members;
- constructed generic identity;
- emitted symbol names;
- metadata IDs.

## Generic Callable Policy

Erasure substitutes every generic callable source occurrence with its declared
representation: omitted T becomes void<A>; T:R becomes R<A>. This includes
references and nested callable signatures. Only an existing outer representation
on A is replaced. Direct calls read results as A; pointer/array storage and
callable signatures retain their represented identity.

Nonexpanded in void<A> may match ordinary in A under the established conversion
view. Expanded whole storage does not match distributed components. Erased
results remain protected at every depth, and integer carrier slots retain
nominal carrier identity. Virtual override written shapes remain exact.

Capability slots retain kind, exact operand/interface and position in source
callable shapes. sizeof is not an ordinary nuint, typenameof is not an ordinary
string and a witness is not an arbitrary raw pointer. ABI lowering alone selects
those payload types. A constructed callable's argument identity and applicability
must survive separate compilation.

Matching declarations, anonymous functions and structural callable values may
initialize callable newtypes under the existing conversion policy. Distinct
nominal callable types do not become interchangeable merely by shape.
Ascribed reference identity must be established directly, independently of
assignment acceptance.

A binder inside a nominal argument list names the nominal argument A. Substituting
`T: R` with A therefore yields `R<A>` where `T` itself is stored or passed, and
`A` inside `Holder<T>` or `Newtype<T>`. A call to `f<T: int>(Holder<T>* h)` with
`T = short` expects `Holder<short>*`. Inferring `T` from a constructed callable
newtype argument reads the written carrier slots first, so `Check<short>` binds
`T` through its `int<short>` input.

The C typedef of a constructed callable newtype is declared over the erased slots
of its definition: an integer-represented parameter is its carrier (`int`,
`int64_t`, `intptr_t`), a void-represented one is `void*`, and capability slots
keep their order and payload (`sizeof` is `uintptr_t`, `typenameof` a name
string). A symbol ascribed to the newtype keeps its own C signature and is cast to
the typedef where it is used as the newtype.

## Metadata And API Output

Metadata should preserve source-level generic binders, representations,
capability parameters, and type arguments. Capability parameters should be
recognizable as capabilities rather than ordinary user parameters:

- `sizeof(T)` carries the target type and lowers to `nuint`;
- `vtableof(T: Interface)` carries both target type and interface type and
  lowers to `const Interface*`;
- `typenameof(T)` carries the target type and lowers to a string-like value.

Generated helper declarations should be omitted from source-level metadata
unless they are part of the exported source API. API headers must include enough
capability parameters for downstream compilations to analyze calls correctly.

## Diagnostics

Diagnostics should tell the author which capability to add and which operation
requires it.

Good diagnostics are operation-specific:

- "Cannot index `T[]` because element stride is unavailable" is better than
  "invalid generic array access";
- "vtableof supplies interface dispatch capability, not element stride" is
  better than treating the vtable parameter as irrelevant;
- "Copying T requires checked sizeof(T)" identifies the missing contract;
- "Dispatch on T requires vtableof(T: IValue)" identifies the exact witness.

The range should point to the operation requiring the capability: the index
operator, call argument, `vtableof` expression, mutation target, allocation
expression, or generic type argument.

## Test Surface

Generic changes should cover:

- void-represented T copy/default-fill/storage failures;
- generic array indexing, mutation, enumeration, slicing, and allocation;
- `sizeof(T)` implicit and explicit arguments;
- `vtableof` validation for generic and concrete types;
- interface-constrained generic dispatch;
- generic iterators and lifted capability fields;
- lambda capability capture, async generic-class `stackalloc T` and generator
  state (`StdRun/generic_execution_state_runtime`, `CEmit/generic_execution_state_abi`);
- strided storage operations and optional-payload rejection
  (`StdRun/generic_storage_operations_runtime`,
  `Diagnostics/generic_local_storage_invalid`,
  `Diagnostics/generic_generator_locals_invalid`);
- generic static member lookup and type argument inference;
- generic constructor argument errors;
- metadata output for capability parameters and generic self-links.

## Implementation Anchors

Primary implementation points include:

- `BindableNodeAnalyzer.GenericCapabilities.cs` for erased array capability
  checks;
- `BindableNodeAnalyzer.Lowering.SizeOf.cs` for `sizeof` parameters and
  arguments;
- `BindableNodeAnalyzer.Lowering.VTableOf.cs` for vtable capability validation
  and lowering;
- `BindableNodeAnalyzer.Callables.cs` for generic callable shape substitution;
- `BindableNodeAnalyzer.Expansion.Iterators.cs` for iterator state capability
  threading;
- `MetadataJsonSerializer.cs` for metadata representation;
- `tests/Diagnostics/generic_*.camp`, `tests/Diagnostics/vtableof_*.camp`, and
  related metadata tests.
