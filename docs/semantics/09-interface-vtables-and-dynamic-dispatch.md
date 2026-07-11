# Interface VTables And Dynamic Dispatch

This supplement describes interface lowering, interface vtable layout, generic
`vtableof` capability flow, and the relationship between interface dispatch and
virtual class dispatch. User-facing interface syntax appears in
[Interfaces And Dispatch](../language/12-interfaces-and-dispatch.md). This document is about the ABI and
compiler invariants.

Camp interfaces are nominal vtable contracts. An interface value used for
dispatch is not a boxed object and not a reflection object. The compiler creates
or references vtable records and converts struct/class receivers into the
interface instance shape required by those records.

## Source Interface Shape

An interface shape is the set of inherited and declared callable slots visible
to implementers and callers. Shape includes required, defaulted, and optional
slots.

The shape of an interface includes:

- inherited interfaces;
- declared methods;
- constructors declared by the interface;
- destructors declared by the interface;
- callable newtype/ascription markers;
- overload selectors;
- vtable initializers for defaulted/optional methods;
- generic parameters and capability parameters needed by generic slots;
- call specs, target specs, constness, lifetimes, and thrown/within/out slots.

Inherited slots occupy stable positions. A derived interface must not change an
inherited method category from ordinary method to overload family or the
reverse. Slot name/signature conflicts must be diagnosed during declaration
validation.

## Slot Function Types

Slot function types include receiver representation, parameter list, return
type, thrown slots, lifetimes, call specs, and generic substitutions. Override
and conformance checks require exact or rule-defined compatibility.

For an interface method declared in `I`:

```camp
interface IReadable
{
	nuint read(byte[] buffer, thrown IoError error);
}
```

the source-level slot function shape is conceptually:

```camp
fn nuint(IReadable* this, byte[] buffer, thrown IoError error)
```

The lowered ABI uses the interface instance slot as the first parameter. Since
source `IReadable*` is the interface-instance pointer form, the physical first
parameter is comparable to an interface slot pointer:

```camp
fn nuint(IReadable** ctx, byte* buffer_elements, nuint buffer_length, thrown IoError error)
```

The exact component expansion is governed by the expanded-forms supplement. The
important rule here is that interface slots always receive the interface
receiver explicitly as their first ABI parameter.

## Required Slots

Required slots must be supplied by implementing structs and classes.

If a type declares it implements an interface and no inherited/defaulted/optional
slot supplies a method, the validator must report the missing member. When a
same-name method exists but lacks the explicit interface marker required by the
current rules, the diagnostic should point at the candidate and tell the author
that the marker is missing.

## Defaulted Slots

Defaulted slots use a target implementation when an implementing type omits the
slot. The default target must match the slot function type.

The initializer can name a visible free function, out-of-scope receiver
function, or static method. It is not a method body, lambda, delegate value, or
runtime fallback. The target must match the slot shape after adding the
interface receiver parameter and applying ordinary callable compatibility rules.

When an implementing type provides its own compatible method, that method wins.
When it omits the slot, the generated vtable stores the default target.

## Optional Slots

Optional slots may be null in a vtable. Calls must account for optional-slot
rules rather than assuming a valid target function.

An optional slot is initialized with `null` or `default`. The slot still exists
in the vtable, but its value may be null for an implementation that omits it.
Camp does not insert a null check for optional interface calls. A source method
reference can read the slot function pointer and compare it with `null`; a
direct interface call through a null slot has the same failure mode as calling
any other null function pointer.

Optional interface members inherited through a base class cannot be introduced
in a derived class after the base omitted them. The validator must reject a
derived declaration that would appear to fill an optional slot inherited through
class state where the vtable shape has already been chosen.

## Interface Constructors

An interface constructor slot means implementers must be constructible through
that interface contract. Current conformance rules permit constructor-bearing
interfaces only on structs and sealed classes. Non-sealed classes cannot safely
promise a constructor slot because derived allocation and construction behavior
would need a different dispatch contract.

Compiler writers must keep constructor slots out of ordinary interface-call
lowering. Construction is a lifecycle operation, not a normal member call.

## Interface Destructors

Interface destructors participate in lifecycle contracts. A destructor slot
must have `void` result behavior and must be validated under the ordinary
destructor rules. Interface vtable initializers are not valid on interface
constructors or destructors.

Destruction through an interface must preserve ownership and allocation rules:
the interface slot can destroy the object state, but freeing storage still
belongs to the allocation/delete path described in the lifecycle supplement.

## Struct Conformance

Struct conformance binds methods directly against interface slots. Receiver
representation and addressability are part of compatibility.

Struct interface conversion creates a scoped indirect carrier when needed. The
compiler materializes a temporary structure containing:

- `_vt`: pointer to the generated interface vtable for the struct/interface
  implementation;
- `ctx`: pointer to the struct storage being viewed through the interface.

The expression returned by conversion is the address of the vtable field, cast
to the interface instance pointer shape. This is why struct interface
conversions are sensitive to statement-prefix availability and lifetime: the
temporary carrier must remain alive for the interface use.

Struct methods implementing interface slots are usually adapted through
generated thunks so the ABI first parameter matches the interface instance slot
shape and the thunk recovers the concrete struct receiver.

## Class Conformance

Class conformance may include inheritance, virtual dispatch, and derived-class
conversion behavior. Extern class boundaries must preserve native ownership and
ABI rules.

Class interface conversion normally uses an interface slot field stored in the
object or an accessor generated/inherited for the implemented interface. The
class instance carries or can produce the address of the interface instance
slot. For derived classes, conversion may need to cast the receiver to the base
class that owns the interface implementation before taking the interface field
or calling the accessor.

Class implementations can inherit interface implementations from base classes.
The search must walk base classes without duplicating or inventing vtable
storage. A derived class that implements an interface through its base should
reuse the base lowering unless it explicitly provides its own compatible
implementation according to the language rules.

Extern classes are native ownership boundaries. The compiler should not insert
ordinary Camp instance fields into an extern class. Extern classes may
participate in interface typing only where an explicit extern surface supplies
the required representation and lifecycle rules.

## Interface Inheritance

Inherited slots participate in conformance and vtable generation. Name and
signature conflicts must be diagnosed.

An interface can derive only from interfaces. A slot inherited from a base
interface keeps its declaring receiver type for default-target matching and
vtable layout. Derived interface lowering must preserve base slot order and
avoid duplicate diamond slots where the same base contract is inherited through
multiple paths.

Interface-to-interface widening is an ordinary implicit conversion when the
target is an exact base interface and the compiler can perform the required
carrier conversion. Narrowing or side-casting follows the raw/interface cast
rules in the conversion supplement.

## VTable Generation

VTables are generated from interface shape and implementation mapping. Emitted
names and slot order must be stable for ABI and tests.

For each concrete implementation, the compiler generates an implementation
vtable. Each slot is selected in this order:

1. a marked or otherwise accepted implementation method from the concrete type
   or inherited class implementation;
2. the interface default initializer target;
3. `null` for an optional slot;
4. diagnostic for a required slot.

Generated vtable variables and thunk symbols are ABI-visible when the
implementation is exported or referenced through an exported API. Names should
be stable and collision-safe. Generated declarations must retain provenance back
to the interface/member/type that caused them.

VTable slot order is part of the ABI. Do not change it as a side effect of
dictionary ordering, source file ordering outside the interface declaration, or
metadata filtering.

## `vtableof`

`vtableof(T: Interface)` transports vtable capability for generic interface
dispatch. Lowering threads the capability through calls that require it.

For a concrete type, `vtableof(Concrete: Interface)` lowers to the generated
vtable variable for that concrete implementation. For a generic type parameter,
`vtableof(T: Interface)` lowers to:

- a function parameter when the current function declares the capability;
- a generated class field when a generic class constructor/init-new stored the
  capability for instance methods;
- a generated iterator/lambda/helper field or parameter when the helper needs to
  preserve generic interface dispatch.

The type of a vtable capability is `const Interface*`. It is not an object
pointer and not an element-stride capability. Generic array operations must
still request `sizeof(T)`.

Validation must ensure that the second type is an interface and that the first
type is either a concrete type implementing that interface or a generic
parameter constrained to implement it.

## Interface Conversions

Conversions from structs/classes to interfaces build or reference compatible
interface carriers. Interface-to-interface and class/interface conversions must
respect inheritance and target representation.

The conversion paths are:

- class pointer to implemented interface: use the stored interface field or
  accessor, possibly via the class that owns the implementation;
- struct value to implemented interface: materialize a temporary indirect
  carrier and return its interface slot pointer;
- generic `T` to interface in a constrained generic body: use `vtableof` and
  cast the receiver to the interface instance slot shape;
- interface to base interface: cast or project through the inherited interface
  carrier;
- unrelated interface cast: follow explicit/unsafe raw-fence rules.

An interface pointer has built-in physical indirection. Source `I*` represents
an interface-instance slot pointer and is physically comparable to `I**`.
Compiler code must preserve this distinction when computing pointer depth and
raw casts.

## Virtual Class Dispatch

Virtual class dispatch is separate from interface dispatch but uses similar
vtable concepts. A virtual/abstract/sealed class hierarchy has:

- a generated vtable type per participating class;
- a root vtable field inserted into the root object layout;
- generated implementation thunks for methods with bodies;
- vtable variables initialized with the closest implementation;
- dispatch bodies for virtual/abstract slot declarations.

Derived vtable types include base vtable state, and vtable pointer assignment is
inserted during creation/init paths. Overrides and sealed methods must match an
inherited virtual or abstract slot. The validator enforces destructor rules for
virtual hierarchies so a derived destructor cannot introduce a destroy path that
the root vtable cannot represent.

Do not conflate virtual class vtables with interface implementation vtables.
Interface vtables describe a nominal interface contract. Virtual class vtables
describe class hierarchy dispatch. A class can participate in both.

## Metadata And API

Metadata should expose source interface declarations, implemented interfaces,
interface implementation markers, optional/defaulted slot source facts when
visible, and exported vtable symbols where those are part of the API contract.

Metadata should not expose generated thunks, temporary struct interface
carriers, hidden vtable fields, or virtual dispatch helper bodies as ordinary
source declarations. Stubs may identify referenced declarations that are not
emitted in full.

API headers must include enough interface declarations and implementation
surface for downstream Camp compilations to type-check interface calls and
`vtableof` expressions.

## Diagnostics

Diagnostics should name the missing, mismatched, ambiguous, defaulted, or
optional slot and point to both the interface requirement and implementation
candidate when possible.

Important diagnostic categories include:

- interface derives from a non-interface;
- class/struct derives from multiple classes or non-class/non-interface base;
- missing required interface member;
- same-name method missing explicit interface marker;
- ambiguous interface implementation marker;
- interface slot initializer is invalid, ambiguous, or shape-incompatible;
- constructor-bearing interface implemented by an invalid class kind;
- optional inherited method introduced in a derived class where not allowed;
- invalid `vtableof` request;
- extern class interface/lifecycle mismatch;
- virtual override or destructor hierarchy mismatch.

## Test Surface

Interface and dynamic-dispatch changes should cover:

- required, defaulted, and optional interface slots;
- default target matching, including static methods and free functions;
- struct and class interface conversions;
- inherited interfaces and diamond shapes;
- interface constructors and destructors;
- explicit implementation markers and overload selectors;
- generic `vtableof` dispatch;
- class virtual dispatch and destructor rules;
- metadata/API output for interface implementation markers;
- C emission for vtable variables, slot order, and thunks.

## Implementation Anchors

Primary implementation points include:

- `BindableNodeAnalyzer.DeclarationValidation.cs` for conformance, default slot
  validation, virtual hierarchy validation, and extern checks;
- `BindableNodeAnalyzer.Lowering.Interfaces.cs` for interface conversion and
  call rewriting;
- `BindableNodeAnalyzer.Lowering.VTableOf.cs` for generic/concrete vtable
  capability lowering;
- `BindableNodeAnalyzer.Expansion.cs` for generated interface and virtual class
  declarations;
- `CallableShapeService.cs` and `BindableNodeAnalyzer.Callables.cs` for slot
  shape compatibility;
- interface, vtable, default-method, virtual, and metadata fixtures under
  `tests/Diagnostics`, `tests/CEmit`, and `tests/Metadata`.
