# Shadow Classes

Status: accepted  
Proposal date: 2026-07-14  
Last updated date: 2026-07-16

Accepted and implemented. Active language guidance now lives in the language
guide, compiler-writer semantics live in the semantic supplements, and this
proposal is retained as the accepted design record.

## Summary

This proposal adds `shadow class` declarations to Camp.

A shadow class is a source-level derived class whose instance pointer has the
same representation as an instance pointer to its direct base class, while its
own fields, virtual dispatch state, and interface implementation state live in a
separate compiler-generated shadow allocation. The base class supplies two
visible hook methods, marked `@getshadow` and `@setshadow`, that store and
retrieve the opaque shadow pointer.

The proposal also amends extern-class inheritance so a non-extern class may
derive from an extern class under strict layout-free restrictions. `shadow`
classes use that same extern-base construction path, then add generated shadow
data for fields, virtual dispatch state, and interface implementation state.

Shadow classes are intended for native and cross-module APIs where a consumer
can see an opaque, non-virtual class surface but cannot see or extend the base
object layout. They let Camp code attach derived behavior to that base instance
without wrapping every call in a separate object or requiring the base module to
export its physical layout.

The source surface is:

```camp
shadow class FancyButton : Control, IControlHandlers
{
	int clicks;

	FancyButton()
	{
		this.clicks = 0;
		this.Handlers = this;
	}

	void handleDestroy(): IControlHandlers
	{
		// User protocol cleanup comes first.
		delete shadow;
	}
}
```

The generated shadow storage is not part of source-level API documentation or
metadata, but it may appear in generated declarations, dumps, private emitted
surfaces, and native symbols in the same spirit as generated iterator state,
interface thunks, and vtable declarations.

## Motivation

Camp already treats exported classes and extern classes as opaque identity
types. Consumers can hold pointers and call visible methods, but they cannot see
or depend on the object's fields. This is the right default for ABI-stable
libraries, but it leaves a gap for APIs that deliberately provide an attachment
slot for user state.

Many native UI, plugin, and callback-oriented libraries provide an opaque object
plus a `void*` user-data field. The library owns the base object; consumers attach
their own data and register callbacks that receive the base object later. In
plain C, this is common and effective, but the derived state is untyped and
manual.

Shadow classes make that pattern a Camp object-model feature:

- the base pointer remains the ABI representation;
- the derived state has a generated layout and ordinary field syntax;
- the derived type can have its own virtual methods and interfaces;
- interface pointers remain valid Camp interface-instance pointers;
- cleanup remains explicit and follows the base library's notification model.

The feature is deliberately an object-model bridge. It does not make the base
layout visible, does not override hidden base virtual slots, and does not add
runtime type information.

## Terms

This proposal uses these terms:

- **base class**: the direct class from which a shadow class derives.
- **base pointer**: a pointer to an instance of the base class.
- **shadow class**: a derived class declared with the `shadow` modifier.
- **shadow instance**: a base object instance viewed through the shadow class
  type.
- **shadow data**: the generated allocation that stores fields and generated
  dispatch state for a shadow class.
- **shadow data type**: the generated struct type for a shadow class's shadow
  data, conventionally suffixed with `Shadow`.

## Non-Goals

This proposal does not add:

- runtime type tests or checked downcasts for shadow classes;
- automatic cleanup notification from base classes;
- automatic clearing of the base object's opaque shadow pointer;
- automatic retention of a constructor allocator for later shadow deletion;
- support for overriding base virtual methods that are not visible in the API;
- a registry for multiple independent shadow attachments on one base object;
- garbage collection, reference counting, or ownership inference for user fields.

The base library remains responsible for whatever destroy notification protocol
it exposes. The shadow class author remains responsible for releasing resources
owned by user fields before deleting the shadow data.

## Syntax

`shadow` is a contextual modifier keyword that may appear before `class`:

```camp
shadow class FancyButton : Control
{
}

virtual shadow class FancyButton : Control, IControlHandlers
{
}
```

The modifier is valid only on a class declaration with a direct base class. The
shadow class may itself be ordinary, `virtual`, or `abstract`, subject to the
ordinary class rules for those modifiers.

`sealed shadow class` is invalid. A shadow class's first non-shadow base is not
visibly virtual, so sealing base override behavior is not useful in this model.

When the direct base is not itself a shadow class, it must be visibly
non-virtual and non-abstract in the source surface used by the shadow class.
This includes extern/API-header class surfaces, where implementation virtuality
is intentionally erased. Because that base is not visibly virtual, a shadow
class cannot override hidden base virtual slots.

Shadow classes may not declare destructors. Their cleanup protocol is explicit:
user code releases owned resources through whatever base-library notification
path is appropriate, then deletes the generated shadow data with `delete
shadow`.

## Non-Shadow Extern Class Derivation

This proposal also amends the general extern-class inheritance rule.

A non-extern class may derive from an extern class. Without `shadow`, this form
is a restricted source relationship over an extern base object. The compiler
reports an error if the derived class:

- is `virtual` or `abstract`;
- declares any virtual members;
- implements any interfaces;
- declares any non-static fields;
- is not explicitly marked `escaped`;
- declares a destructor.

For example:

```camp
escaped class NativeButtonView : NativeButton
{
	void flash()
	{
		this.show();
	}
}
```

These restrictions keep non-shadow derivation from an extern class layout-free:
there is no derived instance field layout, no derived interface storage, no
derived virtual dispatch state, and no derived destructor path for the compiler
to place into an extern object.

Marking the derived class `shadow` removes the errors for virtual/abstract
shadow classes, virtual shadow members, implemented interfaces, non-static
fields, and the explicit `escaped` marker. The `shadow` modifier supplies the
missing representation by moving those features into generated shadow data and
by treating shadow instances as escaped. Shadow classes still may not declare
destructors.

The compiler should use the same extern-base construction machinery for both
restricted non-shadow derivation and shadow derivation. Non-shadow derived
classes need no special field/interface/virtual lowering because the restrictions
above forbid the features that would require it.

## Hook Attributes

The base class must provide exactly one usable `@getshadow` hook and exactly one
usable `@setshadow` hook visible to the shadow declaration.

Hooks may be ordinary instance methods, out-of-scope `this` methods, or extern
methods. They are selected by attribute, not by name.

The `@setshadow` hook must:

- be callable on a mutable base receiver;
- return `void`;
- have exactly one ordinary value parameter after the receiver;
- have parameter type `escaped void*`, adjusted only by target type specs that
  are compatible with the generated shadow data pointer;
- have no ordinary optional parameters;
- have no `out` parameters.

The `@getshadow` hook must:

- be callable on a const base receiver;
- return `escaped void*`, adjusted only by target type specs that are compatible
  with the generated shadow data pointer;
- have no ordinary value parameters after the receiver;
- have no ordinary optional parameters;
- have no `out` parameters.

Hook selection must be unambiguous after ordinary lookup and overload filtering.
If more than one eligible hook is visible, or if the selected hook has an
incompatible signature, the shadow class declaration is invalid.

The hook contract is intentionally raw. The base class stores and retrieves an
opaque data pointer. The shadow class lowering supplies the typed interpretation,
lifetime treatment, and constness treatment.

### Out-Of-Scope Hook Example

A base type does not need to declare its own shadow hooks. A consumer can provide
eligible out-of-scope `this` methods that store shadow data through any
appropriate side mechanism, such as a static lookup table or native extension
property API:

```camp
extern class NativeControl
{
	extern NativeControl();
	extern void destroy();
}

static ShadowTable<NativeControl*, escaped void*> nativeControlShadows;

@setshadow
void setNativeControlShadow(NativeControl* this, escaped void* data)
{
	nativeControlShadows.set(this, data);
}

@getshadow
escaped void* getNativeControlShadow(const NativeControl* this)
{
	return nativeControlShadows.get(this);
}

shadow class FancyButton : NativeControl
{
	int clicks;
}
```

The exact storage API in this example is illustrative. The important rule is
that ordinary lookup can find exactly one eligible `@getshadow` and `@setshadow`
hook for the base receiver.

## Representation

`FancyButton*` is representation-compatible with `Control*`, where `Control` is
the direct shadow base. The pointer value is a pointer to the base object, not a
pointer to the generated `FancyButtonShadow` allocation.

Class pointer conversions follow the ordinary declared class-hierarchy rules:

- a shadow class pointer implicitly converts to its base class pointer;
- downcasts and side-casts use the same explicit, unsafe, or fence-cast rules as
  ordinary class pointers;
- no runtime type test is introduced;
- a raw base pointer is not proven to be a shadow instance merely because its
  shadow hook might return a compatible pointer.

Shadow classes do not add a second public object pointer representation. `this`
inside a shadow class method is still the shadow instance pointer, which is also
the base object pointer. Returning `this` returns the shadow instance, not the
shadow data.

## Generated Shadow Data

For each shadow class, the compiler generates a shadow data struct whose name is
derived from the shadow class name, conventionally by appending `Shadow`.

For example:

```camp
virtual shadow class FancyButton : Control, IControlHandlers, ISomeInterface
{
	int clicks;
	string title;
}
```

may require a generated shape conceptually like:

```camp
struct FancyButtonShadow
{
	FancyButtonVT* _vt;
	FancyButton* _this;
	IControlHandlers _vt_IControlHandlers;
	ISomeInterface _vt_ISomeInterface;
	int clicks;
	string title;
}
```

The exact generated field names are implementation-defined but should follow the
same naming conventions used for existing generated vtable and interface fields.

Generated shadow data stores:

- the shadow class's virtual vtable pointer when the shadow class participates in
  virtual class dispatch;
- a pointer back to the shadow instance when needed by interface thunks;
- interface instance slots for interfaces implemented by the shadow class;
- source fields declared by the shadow class.

Shadow data is implicitly escaped storage. Shadow class fields use the same
retained-lifetime rules as fields of escaped class instances. Constructor
arguments, initializer values, and assignments stored into shadow fields must
satisfy those rules.

## Constness

The `@getshadow` hook returns `escaped void*`; it is not responsible for
expressing receiver-dependent constness.

When lowering a shadow method, the compiler casts the returned shadow pointer to
the shadow data pointer type with constness derived from the effective `this`
receiver. A const shadow receiver produces a const shadow data pointer. A mutable
shadow receiver produces a mutable shadow data pointer.

Therefore:

- reading a shadow field through a const receiver is allowed when the field type
  allows it;
- writing a shadow field through a const receiver is rejected;
- `this` itself keeps its ordinary receiver type and constness;
- interface thunks must preserve the constness required by the interface slot
  when recovering the shadow instance and shadow data.

## Method Lowering

An instance method of a shadow class begins by retrieving shadow data through the
selected `@getshadow` hook and casting it to the appropriate shadow data pointer
type.

Conceptually:

```camp
int getClicks()
{
	return this.clicks;
}
```

lowers as if it had access to:

```camp
constof(this) FancyButtonShadow* _shadow =
	(constof(this) FancyButtonShadow*)this.getShadow();

return _shadow.clicks;
```

All `this.` field accesses for fields declared by the shadow class use the
shadow data. Method calls and uses of `this` itself continue to use the shadow
instance pointer.

If `@getshadow` returns `null`, the program is in invalid shadow-instance state.
The compiler is not required to insert a null check. A resulting trap or native
fault is acceptable.

## Construction

Shadow construction follows the same broad split as ordinary class construction
and virtual dispatch initialization:

```camp
BaseType* BaseType_create();
void BaseType_op_initnew(BaseType* this);

DerivedType* DerivedType_create();
void DerivedType_op_initnew(DerivedType* this);
```

For shadow classes:

- the public create/new path owns allocation or creation of the base instance;
- the public create/new path allocates and zeroes the dynamic shadow data;
- the public create/new path initializes generated shadow dispatch and interface
  fields;
- the public create/new path installs the shadow data through `@setshadow`;
- `_op_initnew` assumes shadow data is already installed;
- `_op_initnew` does not allocate, replace, or install shadow data.

Shadow instances are escaped class instances. They must be constructed through
`new` or an equivalent create surface. `init ShadowType(...)` is invalid for a
shadow class.

When a shadow constructor has a `within` allocation context, the generated shadow
data allocation uses the allocation context selected for the corresponding
create/new operation. Otherwise it uses the active/default allocation context
according to ordinary allocation rules. The compiler does not automatically
retain that allocator for later deletion. If the shadow class needs to delete
shadow data with the same allocator later, the constructor body should store the
allocator explicitly in an ordinary shadow field.

The base object is initialized before shadow-class constructor body code depends
on base state. The shadow data is installed before any shadow `_op_initnew` body
accesses shadow fields.

Constructor failure and cleanup follow existing constructor and allocation
behavior. Shadow classes do not introduce a new constructor-unwind model.

## Shadow Inheritance

Shadow classes may derive from shadow classes when the direct base class exposes
eligible `@getshadow` and `@setshadow` hooks for downstream shadow derivation.
Those hooks expose the active most-derived shadow data pointer. They do not
create or return a separate base-shadow allocation.

A shadow interpretation introduced by a derived shadow class replaces the shadow
interpretation of its shadow base for itself and all of its own derived types.
There is one active most-derived shadow data allocation per shadow instance.

For example:

```camp
shadow class FancyButton : Control
{
	int clicks;
}

shadow class UltraButton : FancyButton
{
	string label;
}
```

constructing `UltraButton` allocates one `UltraButtonShadow`, not one
`FancyButtonShadow` plus one `UltraButtonShadow`.

The generated shadow data layout for a derived shadow class must be
prefix-compatible with its shadow base's data layout. A method compiled as part
of `FancyButton` may retrieve the active shadow pointer and cast it to
`FancyButtonShadow*`; that remains valid for an `UltraButton` instance because
the `FancyButtonShadow` layout is a prefix of `UltraButtonShadow`.

Construction of a derived shadow class follows the init-new chain:

- the most-derived create/new path allocates and installs the most-derived
  shadow data;
- derived `_op_initnew` calls base `_op_initnew`;
- base shadow `_op_initnew` uses the inherited prefix of the already-installed
  most-derived shadow data;
- no `_op_initnew` in the chain allocates or replaces shadow data.

An exported shadow class preserves the `shadow` marker in generated Camp API
output only when the exported API surface also exposes usable `@getshadow` and
`@setshadow` hooks for that class. Downstream Camp compilers need those hooks in
order to preserve shadow representation, derivation, and hook-eligibility rules.
If either hook is not exported, the exported API header emits the class as an
ordinary extern class rather than retaining the `shadow` marker, because a
consumer could not call the required hook methods anyway.

The marker also prevents downstream code from treating hooks inherited from the
shadow class's own base as eligible hooks for deriving from the exported shadow
class. If an exported shadow class wants to allow downstream shadow derivation,
it must declare or export its own eligible `@getshadow` and `@setshadow` hooks.

## Interfaces

A shadow class may implement ordinary Camp interfaces.

Interface implementation state is stored in the shadow data. Converting a shadow
class pointer to an implemented interface pointer returns an interface-instance
slot inside the shadow data.

Because the interface pointer points into shadow data rather than into the base
object, generated interface thunks must recover the shadow instance before
calling the source implementation method. The shadow data therefore stores a
shadow instance pointer when an implemented interface requires it.

Conceptually:

- the interface pointer is a pointer to an interface slot inside shadow data;
- the thunk recovers the containing shadow data from that slot;
- the thunk reads the stored `_this` shadow instance pointer;
- the thunk invokes the source implementation method on `_this`;
- the source implementation method retrieves shadow data normally through
  `@getshadow`.

The lifetime of a shadow-backed interface pointer is tied to both the base
object and the shadow data. If either has been destroyed, the interface pointer
is no longer valid.

## Virtual Shadow Classes

A shadow class may be `virtual` or `abstract` according to ordinary source rules.
Virtual dispatch state for the shadow class is stored in shadow data, not in the
base object.

This virtual dispatch is independent of any implementation virtuality the base
object may have in its defining module. The first non-shadow base must be
visibly non-virtual to the shadow class, so shadow classes cannot override hidden
base virtual slots.

Virtual shadow inheritance uses the same active-shadow-data rule as field and
interface inheritance: the most-derived shadow data stores the virtual dispatch
state needed by the most-derived shadow class, with prefix compatibility for
base shadow methods.

## Deletion And Cleanup

Shadow classes use two separate deletion concepts:

- deleting the shadow instance;
- deleting the shadow data.

Deleting a shadow instance has exactly the same legality check as deleting the
same value as the visible base type. It is forbidden unless the visible base
class already defines a destructor surface accepted by the existing
class/extern-class deletion rules. The value is a base object pointer, not a
pointer to memory allocated for the shadow class by the consuming module.
Therefore:

```camp
delete fancyButton;
```

is valid only when deleting the same value as the visible base type would also be
valid. When valid, it uses the base destructor surface and has no shadow-specific
delete semantics. It does not directly free shadow data unless the base library's
destroy protocol calls back into user code that performs that cleanup.

Shadow data is deleted with the special statement:

```camp
delete shadow;
```

This statement is valid only inside an instance method declared within the
declaration scope of the shadow class whose shadow data is being deleted. A
`delete shadow` statement outside such a method is an error, even if it appears
inside a nested function, lambda, static method, non-shadow derived class, or
ordinary helper reachable from the shadow class.

When valid, `delete shadow` frees the generated shadow data allocation through
the allocator selected by the ordinary `delete`/`within` rules at the `delete
shadow` site. The compiler does not use a hidden allocator retained during
construction. It does not:

- call the base object's destroy function;
- unregister callbacks;
- clear the base object's shadow pointer;
- release resources owned by user-declared fields;
- notify the base library.

Those actions belong to the user protocol exposed by the base library. A common
pattern is for the shadow class to implement a handler interface, receive a
base-object destroy notification, release its own resources, and finally execute
`delete shadow`.

`shadow` is a contextual keyword in this statement, not an ordinary variable
name. If the method containing `delete shadow` also has a parameter or
accessible local variable named `shadow`, the compiler warns on the `shadow`
token in the delete statement that the statement deletes the generated shadow
data, not the local value. This warning can be suppressed through the ordinary
warning-suppression mechanism, or avoided by renaming the local/parameter.

The compiler should warn when a shadow class contains no reachable `delete
shadow` statement anywhere in the class scope. This is a warning because the
base library's notification protocol is intentionally not standardized by Camp.

Because Camp permits only one constructor per type, the compiler should also warn
when that shadow class constructor has a `within` allocator parameter and a
`delete shadow` statement in that class uses the default allocator, either by
omitting an explicit `within` context or by using `within(default)`. This warning
catches likely allocator mismatches while leaving the responsibility for storing
and supplying the correct allocator with the programmer.

After `delete shadow`, accessing shadow fields through `this` is invalid. The
compiler should diagnose obvious same-body field accesses after `delete shadow`
when control flow makes the error clear. It is not required to prove all
possible callback, aliasing, or interprocedural uses.

The compiler does not clear the base object's opaque shadow pointer by default.
If a shadow class wants to clear that pointer, it may explicitly call the
appropriate base hook or base API before or after deleting shadow data, as
allowed by the base library's protocol.

Double `delete shadow`, use-after-delete, and calling shadow methods after the
base library has invalidated the shadow data are programmer errors.

## API, Metadata, And Generated Surfaces

Camp API output must preserve the `shadow` modifier on exported shadow classes
only when the generated API also exports usable `@getshadow` and `@setshadow`
hooks for the shadow class. Otherwise, the generated shared-library API header
must erase the `shadow` modifier and present the type as an ordinary extern
class. Metadata may still record the source declaration as shadow-aware for
tooling, but the importable API surface cannot promise shadow semantics without
callable hooks.

Source-level metadata should represent the shadow class as a class declaration
with:

- the `shadow` modifier;
- its source base class and implemented interfaces;
- its source fields and methods;
- source constructors and visible hooks declared by the shadow class.

Metadata and documentation should not expose the generated shadow data struct as
ordinary source API solely because the shadow class is exported.

Generated declaration dumps, private C headers, and emitted C may include the
generated shadow data struct, vtables, interface slots, thunks, and helper
functions where needed. These generated names should follow existing generated
symbol and collision rules. A source declaration that would collide with a
required generated shadow name must be diagnosed.

## Diagnostics

Important diagnostics include:

- `shadow` used on a non-class declaration;
- `shadow class` without a direct base class;
- `sealed shadow class`;
- non-shadow direct base is visibly virtual or abstract;
- non-shadow non-extern class deriving from an extern class while virtual,
  abstract, declaring virtual members, implementing interfaces, declaring
  non-static fields, missing explicit `escaped`, or declaring a destructor;
- destructor declared in a shadow class;
- `init` construction of a shadow class;
- missing `@getshadow` or `@setshadow` hook;
- ambiguous hook selection;
- hook has an incompatible return type, parameter type, optional parameter,
  output parameter, receiver constness, or target pointer spec;
- source member collides with generated shadow data names or helper names;
- inherited hooks from a shadow base are incorrectly used for downstream shadow
  derivation;
- illegal field access after an obvious `delete shadow`;
- `delete shadow` outside an instance method declared within the shadow class
  declaration scope;
- `delete shadow` in a method that also has an accessible local or parameter
  named `shadow`, reported as a warning on the `shadow` token;
- shadow class with no `delete shadow` statement, reported as a warning;
- `delete shadow` with a default allocator when a shadow constructor has a
  `within` allocator parameter, reported as a warning;
- invalid interface conversion after lifetime analysis;
- invalid delete of a shadow instance when the visible base has no accepted
  destructor surface;
- invalid conversion or cast under ordinary class/interface/raw carrier rules.

Diagnostics should point to the `shadow` modifier for declaration-level errors,
to the hook attribute or selected hook signature for hook errors, and to the
field access or `delete shadow` statement for body-level errors.

## Recommended Test Surface

Parser and bindable tests:

- `shadow class` modifier before `class`;
- combinations with `virtual` and `abstract`;
- rejection of `sealed shadow class`;
- rejection on non-class declarations;
- API output preserving `shadow`.

Declaration validation tests:

- missing base class;
- missing hooks;
- ambiguous hooks;
- invalid hook signatures;
- hook selection through out-of-scope `this` methods;
- const receiver compatibility for `@getshadow`;
- mutable receiver compatibility for `@setshadow`;
- visible virtual/abstract non-shadow base rejection;
- restricted non-shadow derivation from extern classes;
- diagnostics for non-shadow extern-derived classes that are virtual, abstract,
  declare virtual members, implement interfaces, declare non-static fields, omit
  explicit `escaped`, or declare destructors;
- `shadow` removing the non-shadow extern-derived errors for virtual/abstract
  shadow classes, virtual shadow members, implemented interfaces, non-static
  fields, and omitted explicit `escaped`;
- destructor declaration rejection;
- exported shadow class metadata/API shape;
- generated-name collision diagnostics.

Construction and lowering tests:

- generated create path allocates, zeroes, initializes, installs, and then calls
  `_op_initnew`;
- `_op_initnew` does not allocate or install shadow data;
- `init` construction of a shadow class is rejected;
- constructor with `within` allocates shadow data from the selected context;
- `delete shadow` uses the allocator selected at the delete site;
- `delete shadow` with default allocator warns when a shadow constructor has a
  `within` allocator parameter;
- shadow methods lower field access through `@getshadow`;
- const shadow methods cast shadow data to const shadow data;
- `this` returns still return the shadow instance pointer.

Shadow inheritance tests:

- derived shadow class allocates one most-derived shadow data allocation;
- base shadow fields remain accessible through prefix-compatible layout;
- derived shadow fields are appended after inherited shadow data;
- shadow-base hooks expose the active most-derived shadow data pointer;
- base `_op_initnew` is called with shadow data already installed;
- exported shadow base requires its own hooks for downstream derivation.

Interface and virtual dispatch tests:

- shadow class implements one interface;
- shadow class implements multiple interfaces;
- interface conversion returns a pointer into shadow data;
- interface thunks recover `_this` and call the implementation method;
- virtual shadow method dispatch uses vtable state stored in shadow data;
- base virtual override attempts are impossible or rejected through visible-base
  rules.

Deletion and cleanup tests:

- `delete shadow` allowed inside shadow class methods;
- `delete shadow` rejected elsewhere;
- `delete shadow` rejected in static methods, lambdas, nested functions, helper
  methods outside the shadow class declaration scope, and methods of non-shadow
  derived classes;
- warning on the `shadow` token when `delete shadow` appears in a method with an
  accessible local or parameter named `shadow`;
- warning when no `delete shadow` appears in the class;
- obvious field access after `delete shadow` diagnosed;
- destructor declarations inside shadow classes are rejected;
- `delete shadowInstance` is rejected unless the visible base has an accepted
  destructor surface;
- base destroy callback pattern compiles and lowers correctly;
- no automatic clearing of the base shadow pointer is emitted.

Conversion and lifetime tests:

- shadow pointer upcasts to base pointer;
- downcast and raw-fence behavior matches ordinary class rules;
- no runtime type test is generated;
- shadow fields are treated as escaped storage;
- storing scoped pointer-bearing values in shadow fields is rejected;
- target pointer spec mismatches on hooks are diagnosed.

Emission and metadata tests:

- C emission for generated shadow struct, vtables, thunks, and helper functions;
- private/generated surfaces include required generated declarations;
- source metadata omits generated shadow internals by default;
- API headers preserve `shadow` and enough hook information for downstream
  analysis;
- shared-library API headers erase `shadow` when usable `@getshadow` or
  `@setshadow` hooks are not exported;
- downstream project-reference/API-header consumption of exported shadow classes.

## Risks

### Object-Model Complexity

Shadow classes intentionally bridge two object models: an opaque base object
owned by one module or native library, and derived state owned by the consuming
module. This is powerful, but it means construction, deletion, interface
conversion, virtual dispatch, and metadata all need explicit rules.

### Cleanup Is Protocol-Dependent

Camp cannot know how a base library reports destruction. A shadow class may leak
shadow data if the author does not register the right callback or does not call
`delete shadow` from that callback. The warning for missing `delete shadow`
helps, but it cannot prove the base protocol is correct.

### Use-After-Delete

After `delete shadow`, the base object's opaque pointer may still contain the
old address. Camp deliberately does not clear it automatically. This keeps the
feature compatible with external libraries but leaves responsibility with the
programmer.

### Allocator Mismatches

Shadow data allocation occurs in the create/new path, while the programmer may
need to retain an allocator argument in the constructor body for later cleanup.
The compiler warning for default-allocator `delete shadow` catches likely
omissions, but it cannot prove every custom allocator protocol is correct.

### Shadow Inheritance Prefix Stability

Derived shadow classes rely on prefix-compatible shadow data layouts. A compiler
bug in layout generation could corrupt field access, virtual dispatch, or
interface thunk recovery. Tests should heavily cover generated layout ordering
and inherited field access.

### API Surface Leakage

The generated shadow data type is not source API, but generated symbols may
still appear in dumps and emitted native surfaces. The compiler must keep source
metadata, API headers, generated declarations, and native emission clearly
separated.

### Target Pointer Domains

On targets with multiple data pointer domains, hook signatures and generated
shadow allocation pointers must be compatible. The implementation must not
silently erase or reinterpret target pointer specs.

### Cross-Module Derivation

Exported shadow classes need the `shadow` marker and explicit hook eligibility
rules. Without that marker, downstream compilers could accidentally treat a
shadow class like an ordinary class or reuse hooks from the wrong base surface.

## Overall Complexity

This is a medium-high complexity feature.

The core representation is straightforward: a shadow class pointer is a base
class pointer, and source fields live in a generated side allocation. The
feature also reuses existing compiler patterns for:

- generated class create/init-new helpers;
- virtual vtable assignment;
- interface slot storage and thunks;
- exported opaque class surfaces;
- extern-class deletion rules;
- generated declaration visibility and metadata filtering.

The complexity comes from how many compiler subsystems must agree:

- parser and bindable model for the contextual `shadow` modifier;
- declaration validation for hook discovery, visible-base restrictions, and
  shadow inheritance;
- expansion for generated shadow data types, vtables, interface slots, and
  helper declarations;
- construction lowering for caller-side shadow allocation and installation;
- restricted non-shadow extern-base derivation checks that reuse the same
  extern-base construction path without shadow-data lowering;
- method lowering for shadow field access and receiver-constness propagation;
- lifetime analysis for implicitly escaped shadow data and retained fields;
- conversion analysis for class/interface/raw carrier behavior;
- delete lowering for `delete shadow` and shadow-instance delete diagnostics;
- API and metadata output for preserving the source marker while hiding
  generated internals from source metadata;
- C emission for generated shadow structs, helper functions, vtables, and
  thunks;
- language-service support for diagnostics, hover, completion, and generated
  symbol awareness.

The recommended implementation strategy is staged:

1. Parse and preserve the `shadow` modifier.
2. Validate hooks, visible-base restrictions, and restricted non-shadow
   extern-base derivation.
3. Generate shadow data for non-virtual, no-interface shadow classes.
4. Lower construction, method field access, and `delete shadow`.
5. Add shadow inheritance and prefix-compatible layout.
6. Add interface implementation support.
7. Add virtual shadow class support.
8. Complete API, metadata, diagnostics, and cross-module tests.

This staged path keeps the first working slice small while preserving the final
semantics needed for native UI and cross-module extension scenarios.
