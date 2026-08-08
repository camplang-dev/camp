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

Generic parameters are bound in type and function scopes. Type arguments are
applied through substitution while preserving nominal boundaries and source
syntax for diagnostics.

The binder must keep three pieces of information separate:

- the source generic parameter declaration, including constraint and source
  range;
- the resolved type name used for substitution, usually the generic parameter
  name before instantiation;
- capability parameters or fields that transport layout, vtable, or type-name
  information.

Generic parameters introduced by a containing type are visible to member
functions. Function generic parameters are visible only within that function and
its generated helper declarations. Generated iterator, lambda, and virtual
helper declarations must copy or thread generic parameters deliberately; they
must not rely on ambient analyzer state.

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

## Constraint Categories

Constraints determine which operations are legal. `any` permits the type to be
named. `copyable` permits copying when paired with any needed representation
capabilities. Interface constraints permit interface calls when the required
vtable capability is available.

Compiler code commonly distinguishes these categories:

- **representation-known:** an unconstrained type parameter in a representation
  context, integral primitive constraint, or pointer-to-void style constraint
  where ordinary representation operations are available;
- **any:** the type is erased and may be named, stored by reference, and passed
  through source surfaces, but layout/copy operations are not implied;
- **copyable:** erased value copying is permitted when the operation also has
  any required representation capability;
- **interface implementation:** the type is constrained to implement an
  interface, but dispatch still needs a `vtableof` value.

Do not use one constraint as a proxy for another. `vtableof` is not a size
capability. `sizeof` is not an interface-dispatch capability. `copyable` is not
a default-construction or destruction capability.

## Erased Versus Materialized Values

Some generic values are erased at source analysis boundaries. Operations that
need layout, size, vtable, type name, construction, destruction, or copy
capability must request the corresponding capability explicitly.

The highest-risk mistakes are:

- indexing `T[]` without knowing element stride;
- copying `T` when `T` is only constrained by `any`;
- mutating elements through `const T[]`;
- allocating or default-filling `T[]` without size and fill semantics;
- calling interface methods on `T` without a usable vtable;
- lowering an iterator or async frame that needs to store erased values but has
  not threaded the needed capabilities into the generated state.

## `T: any`

`T: any` does not imply copyability, size availability, default filling, array
stride, or destructor availability. Report specific capability diagnostics.

Allowed operations should be limited to operations that can be performed without
knowing layout or copying value bits. The compiler can name the type, preserve
it in signatures, and pass existing values through compatible surfaces, but it
cannot assume how to index, copy, allocate, or destroy values of `T`.

Example diagnostic shape:

```camp
T first<T: any>(T[] values)
{
	return values[0]; // needs sizeof(T) and copyability
}
```

This should not become a vague "generic operation failed" error. The diagnostic
must name the missing element stride and, when relevant, the missing copy
capability.

## `T: copyable`

`T: copyable` is an erased value constraint. It permits ordinary value-copy
operations, assignment, local value storage, and value return when the operation
also has any representation capability needed to emit the code. It does not
make `T` a compile-time layout type.

For array element access, `copyable` is not sufficient by itself when the
element type is erased. A compiler path that copies an element out of `T[]`
needs both:

- permission to copy `T`;
- element stride, normally supplied by `sizeof(T)` or by a representation-known
  constraint.

`copyable` also does not say that a value can be safely retained beyond its
lifetime. Lifetime analysis must still enforce pointer-bearing generic
boundaries.

Direct fields of type `T` are invalid in erased generic type bodies under both
`T: any` and `T: copyable`. The generic type has one physical layout, and the
size of `T` is not a compile-time field layout. Store `T*`, `T[]`, or explicit
erased storage instead. Local variables of type `T` are source-valid when the
function has the size capability needed to allocate the runtime-sized storage.

Direct class types, fixed structs, and fixed-size array value types do not
satisfy `copyable`. Pointers to those values do satisfy `copyable`, because the
pointer value itself has ordinary pointer-sized copy semantics.

## Size, VTable, And Type Name Capabilities

`sizeof(T)`, `vtableof(T: Interface)`, and `typenameof(T)` parameters transport
capabilities into generic bodies. Lowering must thread those values through
generated calls and helper declarations.

### `sizeof(T)`

`sizeof(T)` supplies the runtime element size for an erased type parameter. It
is used by generic arrays, element address calculation, slicing, allocation,
copying, and iterator state that must store or enumerate `T` values.

The analyzer should find `sizeof(T)` capability in:

- ordinary function parameters;
- generated iterator state fields copied from a generator parameter;
- generated constructor/init-new parameters and fields for generic classes when
  the class needs the size later.

Implicit `sizeof` arguments can be added at call sites when the concrete type is
known and the callee declares the capability parameter. If no capability is
available inside the generic body, report the operation that needs it.

### `vtableof(T: Interface)`

`vtableof(T: Interface)` supplies the interface vtable pointer for generic
interface dispatch. It requires the generic parameter to be constrained with an
interface implementation relation compatible with the requested interface.

The value carried by the parameter has type `const Interface*`. It is a vtable
capability, not an object pointer and not a layout capability. It must be
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

Inside erased substitution for `T: any` or `T: copyable`, `T*` means a pointer
to the storage form of `T`. If `T` is an expanded form, that storage form is the
materialized `struct(T)` representation. A compiler must not treat a pointer to
one expanded component as a pointer to the whole substituted value.

Iterator generators with `T: any` or `T: copyable` add `sizeof(T)` parameters
when the generated state needs element stride. When an iterator lowers into a
state type, these capability parameters become state fields so the `next`
method can run after the factory call has returned.

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

## Interface-Constrained Generics

An interface-constrained generic parameter may call interface members only when
the call can obtain a vtable capability. The lowering path for a generic
interface call conceptually turns:

```camp
value.read()
```

into a call through `vtableof(T: IReadable)` plus a cast of the generic receiver
to the interface instance shape required by the slot. The interface supplement
documents the physical vtable carrier in more detail.

The validator must reject:

- `vtableof` where the second type is not an interface;
- `vtableof` on a non-generic type that does not implement the interface;
- `vtableof(T: Interface)` where `T` is not constrained to implement that
  interface or a derived interface;
- use of `vtableof` as if it supplied array stride.

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
void swapFirst<T: copyable>(T[] values, sizeof(T))
{
	stackalloc T temp = values[0];
	values[0] = values[1];
	values[1] = temp;
}
```

The compiler rejects ordinary `T` locals and `auto` locals that would need to
infer stackalloc storage for an erased generic value. `stackalloc T` requires
`sizeof(T)` in scope. Array views, pointers, and other non-exact shapes do not
trigger this exact-value rule.

Foreach over erased generic array values follows the same rule:

```camp
foreach (stackalloc T item in values)
{
	use(item);
}
```

For non-lifted foreach lowering, the compiler may allocate one stackalloc item
slot before the loop and assign into it each iteration. For iterator-lifted
foreach lowering, any stackalloc storage area that is needed after resumption
must be allocated on the resumed execution path so a resume cannot use a stale
stack address.

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

Callable types can contain generic parameters in return, parameter, receiver,
context, thrown, and `within` positions. Compatibility must use the same
expanded callable shape logic as non-generic callables, then apply generic
substitutions.

Callables involving erased generic values must not bypass capability checks.
For example, a delegate returning `T` may be type-compatible, but invoking it
and copying the result into storage can still require copyability and lifetime
checks.

Callable newtypes remain nominal across generic substitution. Matching erased
shape alone does not erase the newtype boundary.

## Metadata And API Output

Metadata should preserve source-level generic parameters, constraints,
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
- "T: any is non-copying" is better than a generic assignment failure;
- "Generic parameter `T` must be constrained with implements `IValue`" is better
  than a failed member lookup.

The range should point to the operation requiring the capability: the index
operator, call argument, `vtableof` expression, mutation target, allocation
expression, or generic type argument.

## Test Surface

Generic changes should cover:

- `T: any` copy/default-fill/storage failures;
- generic array indexing, mutation, enumeration, slicing, and allocation;
- `sizeof(T)` implicit and explicit arguments;
- `vtableof` validation for generic and concrete types;
- interface-constrained generic dispatch;
- generic iterators and lifted capability fields;
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
