# Generics, Erasure, And Capabilities

This supplement describes the compiler rules for generic type parameters,
erased values, and explicit capabilities such as `sizeof(T)`,
`typenameof(T)`, and `vtableof(T: Interface)`. User-facing syntax is documented
in [Generics And Type Capabilities](../language/17-generics-and-type-capabilities.md);
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
- **copyable:** copying is permitted, but size/stride may still need an explicit
  capability;
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

`T: copyable` permits copy operations but does not imply unrelated capabilities
such as default construction or interface dispatch.

For array element access, `copyable` is not sufficient by itself when the
element type is erased. A compiler path that copies an element out of `T[]`
needs both:

- permission to copy `T`;
- element stride, normally supplied by `sizeof(T)` or by a representation-known
  constraint.

`copyable` also does not say that a value can be safely retained beyond its
lifetime. Lifetime analysis must still enforce pointer-bearing generic
boundaries.

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

Iterator generators with `T: any` or `T: copyable` add `sizeof(T)` parameters
when the generated state needs element stride. When an iterator lowers into a
state type, these capability parameters become state fields so the `next`
method can run after the factory call has returned.

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
