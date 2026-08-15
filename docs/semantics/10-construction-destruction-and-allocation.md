# Construction, Destruction, And Allocation

This supplement describes lifecycle lowering: constructors,
constructor-shaped type calls, `stackalloc`, `new`, destructors, `delete`,
`within` allocation contexts, generated cleanup, and extern lifecycle
boundaries. User-facing syntax appears in
[Structs, Classes, And Object Lifetimes](../language/05-structs-classes-and-object-lifetimes.md).

The compiler's lifecycle rules sit at the intersection of type binding,
lifetime analysis, virtual/interface dispatch, async/iterator frame lowering,
and C emission. Changes here have a large blast radius.

## Lifecycle Declarations

Constructors and destructors are represented as functions with special
modifiers/signature identity:

- a constructor has constructor modifier and resolves as construction of the
  containing type;
- a destructor has destructor modifier or destructor-style name and returns
  `void`;
- generated create/init/delete helpers are ordinary generated functions with
  lifecycle provenance;
- lifecycle members cannot be declared out of scope where ordinary static or
  receiver functions can be.

Constructors and destructors should not participate in ordinary callable
ascription unless the language rule explicitly says they can. Their source
signature identity is not the same as an ordinary named method.

## Constructor Binding

Constructor binding resolves the target type, overloads, receiver/storage
target, generic substitutions, and initializer arguments. Constructors may be
generated or user-defined.

Construction analysis must decide:

- source construction kind: selected destination construction, `stackalloc`,
  or `new`;
- target type and constructed generic arguments;
- constructor overload or generated default constructor;
- hidden capability parameters such as `sizeof(T)` or `vtableof(T: I)`;
- `within` allocator parameter and current allocation context;
- initializer list field mapping;
- virtual table initialization requirements;
- lifetime retention facts for pointer-bearing inputs.

Do not lower construction before declaration analysis has generated required
lifecycle helpers, interface/vtable fields, and generic capability fields.

## Default Constructors

Default constructors are generated where the type shape permits it. Generated
constructors must initialize fields according to language defaults and respect
visibility/export rules.

Generated default construction is not a license to bit-clear arbitrary generic
or extern values. The generator must know that the type shape permits default
initialization. For structs/classes with fields, generated construction must
apply field defaults, inline constant constraints, fixed storage rules, and
virtual/interface field initialization as required.

For generic types, default construction may need size/copy/init capabilities.
If the capability is not available, diagnose the missing operation rather than
emitting a placeholder fill.

## Definite Assignment

Struct constructors must definitely assign required fields before completion.
Fixed arrays, pointer-bearing fields, inline fields, and generated fields need
special handling so a constructor cannot leave storage in a partially valid
state.

Class construction can rely on allocation/defaulting paths when the generated
create/init helper guarantees the object storage is initialized before user code
observes it. Compiler code should keep this guarantee explicit rather than
assuming C zero-initialization everywhere.

## Fixed Structs And Copyability

A `fixed struct` has visible, inline storage and no implicit copy semantics. It
does not participate in class inheritance or virtual class dispatch. Its layout
is part of the source/ABI surface in the same way ordinary struct field layout
is, but the value itself must be constructed, passed, and retained by reference
or by explicit storage-oriented operations.

Compiler rules to preserve:

- a fixed struct type does not satisfy `copyable`;
- direct fixed-size array value types do not satisfy `copyable`;
- pointers to fixed structs and fixed-size arrays do satisfy `copyable` because
  the pointer value is copyable;
- fixed structs and classes must be passed by reference, except where an extern
  surface explicitly owns a compatible native representation;
- ordinary copyable structs cannot contain direct fields whose type is a class
  or fixed struct;
- initialization of fixed-size arrays is limited to array literals, string
  literals where the element type permits them, or `default`.

Lowering must not accidentally materialize a copied temporary for a fixed
struct. Construction should target the final storage whenever possible, and
operations that require a movable value should diagnose rather than silently
copying bytes.

## Destructors

Destructors release owned resources and must return `void`. Lowering must call
destructors at explicit `delete` sites and generated cleanup points where the
language requires it.

The validator must reject destructors that return values. It must also enforce
virtual hierarchy destructor rules:

- a virtual hierarchy cannot introduce a derived destructor unless the ultimate
  base declares a virtual or abstract destructor slot;
- a derived destructor implementing an inherited virtual destructor must use
  `override` or `sealed`;
- an abstract destructor requires concrete derived classes to implement it with
  the correct override form.

Destructor lowering must preserve base destruction order, field cleanup order,
and generated `finally` cleanup paths. When a destructor participates in
interface or virtual dispatch, the dispatch slot destroys object state; storage
freeing remains part of the `delete`/allocator path.

## Base Initialization

Derived class constructors initialize base state according to class hierarchy
rules. Extern base classes constrain what source constructors may do.

A non-extern class may inherit only from non-extern classes. An extern class may
inherit only from extern classes. The compiler should reject mixed inheritance
because ownership, layout, and constructor/destructor responsibilities are
different across that boundary.

Virtual class participants receive vtable state. Constructors/create helpers
must assign the correct root vtable field so virtual calls dispatch to the
constructed dynamic type after initialization.

Base initialization must run before derived fields/methods depend on base
state. Derived initialization must not overwrite generated base vtable/interface
state unless the lowering path intentionally updates it for the dynamic type.

## Selected Destination Construction

Constructor-shaped type calls construct into storage selected by the
surrounding declaration, assignment, argument materialization, or other target.
They do not allocate by themselves.

Selected construction lowers to an init helper call using the address of the
target storage. In assignment form, the compiler rewrites:

```camp
slot = Widget(args);
```

into a call that constructs at `&slot`, with generated vtable assignment placed
before or around the helper call when the target type participates in virtual
dispatch.

Array allocation is not selected by constructor-shaped syntax. Source must use
`new T[n]`, `stackalloc T[n]`, or `fixed T[n] name` according to storage intent.

Old `init` syntax is rejected as a migration diagnostic. The compiler should
explain the replacement storage choices rather than preserving compatibility.

## Initializer Lists

Initializer lists can initialize fields by position or name. The analyzer must
map initializer items to fields before lifetime retention checks and lowering.
For pointer-bearing fields, initializer values contribute retained lifetime
facts to the constructed aggregate.

Initializer lowering must preserve field order, field names, defaulted fields,
fixed storage, and expanded forms. A positional initializer should not depend on
reflection or metadata order; it uses declaration order.

## Trailing Construction Initializers

Construction may be followed by a trailing initializer, often called an object
initializer in user-facing prose. The constructor call and the initializer
together form one source construction expression. Lowering must evaluate
constructor arguments once, construct the object once, and then apply the
initializer items in source order before the expression is considered complete.

The initializer target can be an aggregate field, property, indexer, or
expanded-form component accepted by ordinary initializer rules. Field targets
write storage directly. Property and indexer targets lower through their setter
calls, so they inherit the accessor semantics described in
[Core Expression, Statement, And Access Semantics](14-core-expression-statement-and-access-semantics.md).

For selected construction, the target storage and lifetime come from the
destination. For `stackalloc`, the target is activation-stack storage scoped to
the current function activation. For `new`, the target is allocated storage,
normally heap-like from the reader's point of view and allocator-defined from
the compiler's point of view. In all cases, pointer-bearing initializer values
contribute retained lifetime facts to the constructed aggregate.

Trailing initializer lowering must not create an intermediate copied value for
fixed structs or other non-copyable storage shapes. It should construct into
the final storage, then apply initializer writes/calls against that storage.

## `new`

`new` allocates through the selected `within` context, then constructs. Lowering
must preserve allocation order, zeroing rules where required, constructor call,
and cleanup on failure paths.

The `new` sequence is conceptually:

1. select allocator from explicit `within`, current within context, default
   within policy, or fallback allocation path;
2. allocate storage for the target type;
3. default/zero/init storage as required by the type and target;
4. assign virtual/interface generated fields needed before user constructor
   code observes the object;
5. invoke constructor/init-new helper;
6. on generated failure paths, run cleanup compatible with what has been
   initialized.

The compiler should not pretend an extern class can be allocated with ordinary
Camp layout unless an extern constructor or create method provides that surface.

## `stackalloc`

`stackalloc` explicitly selects dynamic activation-stack storage. It can
allocate array element storage or storage for a constructed instance:

```camp
char[] scratch = stackalloc char[count];
Widget* widget = stackalloc Widget(args);
```

The backing storage is not allocator-managed and cannot be freed early. A
`delete` applied to a stackalloc-allocated instance may run the destructor, but
must suppress allocator deallocation.

Stackalloc-backed values cannot be returned, stored into longer-lived or
escaped storage, retained by heap/escaped receivers, or used across suspension.
The implementation may conservatively reject stackalloc in async/generator
bodies rather than prove fine-grained post-suspension liveness.

### `(new)` Prepared Allocation

Parenthesized `(new)` is the allocation modifier for a direct transformed prep
call. It is not ordinary type construction. It retains the call's prepared
array result and changes its backing storage from scoped stack storage to the
ordinary allocation path selected by `within`.

Its unary/cast-like operand must remain the direct transformed result. A member,
component, method, index, or range appended to the call makes the `(new)` form
invalid because it would discard the owning array reference. The result may
instead be captured and then accessed, or used directly with `finally delete`.
Allocator selection, checked element-size/terminator arithmetic, deletion, and
cleanup use the same rules as other allocated arrays.

## `delete`

`delete` destroys and frees a value according to its type and allocation
context. Lifetime analysis validates that the deleted value is compatible with
the free surface.

The `delete` sequence is conceptually:

1. validate the target value and lifetime;
2. dispatch or call the correct destructor when required;
3. free storage through the allocator/free path associated with the value;
4. avoid double cleanup on transfer paths where `finally` cleanup already owns
   the value.

For extern classes, `delete` requires an explicit destructor because the
compiler cannot infer native ownership semantics. For class hierarchies,
destructor dispatch must respect virtual destructor slots when the hierarchy
uses virtual dispatch.

Deletion of delegate/postponed/lambda contexts follows callable ownership
rules. Do not delete arbitrary delegate contexts merely because a delegate value
goes out of scope.

## Allocator Selection

Allocator selection comes from explicit `within`, current within context,
within parameters, or the compiler's implicit/explicit allocation policy.

`within` handling has several compiler-visible forms:

- explicit `within(context) expression`;
- `within(default)` expression, which intentionally requests default behavior;
- bare current allocator/current-within expressions generated by lowering;
- `within` parameters in function signatures;
- file or build-level default policy controlling whether implicit within
  arguments may be inserted.

Within parameters cannot have default values. Their value is supplied by an
explicit within context, `within(default)`, or explicit `within null` where the
language permits null/fallback behavior. If the current file requires explicit
within allocation, calls that need a within argument must diagnose instead of
silently inserting fallback.

Hidden within arguments must be ordered consistently with ordinary, `out`,
thrown, expanded-return, `sizeof`, and `vtableof` arguments. The lowering code
normalizes duplicates and moves supplied within arguments into the expected
position.

## Allocator Lifetime

Allocator values may be retained in generated contexts, async frames, iterator
states, or lambda/postpone contexts. Lifetime analysis must reject allocator
values that cannot outlive the generated storage that needs them.

Async bodies have a special hazard: a within allocator used for frame
deallocation may be needed after suspension. An unscoped allocator that cannot
be retained must be rejected for await-capable async code.

## Retained Allocator Lifecycle

A class constructor parameter may be written `within this.allocator`. This is a
source-level retained allocator declaration. It is valid only on a `within`
parameter of a constructor declared by the lifecycle root class. It is invalid
on ordinary methods, destructors, non-class constructors, shadow classes, extern
class constructors, derived classes, non-`within` parameters, or more than one
parameter in the same class.

The retained allocator declaration synthesizes a generated instance field named
`allocator` unless that field would duplicate an existing source field. The
field type is `Allocator*`. It is generated lifecycle storage, not ordinary
public API. Source may read it as `this.allocator`; unqualified `allocator`
inside the constructor refers to the parameter only if an ordinary parameter
with that name is actually in scope. A retained allocator field must not be
assigned by source code. The compiler may diagnose or warn about such
assignments because the field records allocation provenance.

The generated `_op_initnew` helper assigns the retained allocator field from
the constructor parameter before running the user constructor body. The
assignment belongs to `_op_initnew`, not to `create()`, so selected
construction, `stackalloc`, `new`, and generated `create()` all initialize the
same retained field through the same construction helper.

The generated `_op_delete` helper destroys object state but does not free the
object's own storage. It is storage-neutral and is used by stack/fixed,
ordinary `delete`, virtual destructor dispatch, and generated `destroy()` paths.
For retained allocator classes, the generated `destroy()`/owning delete path
must call `_op_delete` and then free the complete object pointer through the
retained allocator field. The free must use the topmost object pointer owned by
the lifecycle root, not a derived subobject pointer.

If a lifecycle root uses `within this.allocator`, derived constructors must
accept and forward a `within` allocation context to the base constructor. The
derived class must not declare a second retained allocator field. In source,
the base call is written with ordinary base-call syntax; the `within` modifier
belongs to the parameter declaration, not to the base-call argument.

Constructor and destructor allocator shapes must remain coherent:

- a retained allocator constructor pairs with a parameterless destructor;
- an ordinary constructor `within` lifecycle pairs with a destructor `within`
  lifecycle when generated allocation/deallocation helpers need the active
  allocation context;
- an interface lifecycle destructor slot with `within` and one without
  `within` are different exact contracts.

When a generated lifecycle helper would receive an allocator parameter only
because of explicit `within` policy, the source compilation must have a visible
`Allocator` type to name that parameter in source API and metadata. If no
source `Allocator` type is available, the compiler must not synthesize an
impossible helper signature. Explicit source constructor/destructor `within`
parameters are still part of the source contract and must use visible,
serializable types.

Imported API headers and metadata expose the callable constructor/destructor
shape required by consumers. A retained allocator constructor is serialized as
an extern constructor with a `within allocator` parameter. The generated
retained allocator field is hidden. A retained allocator destructor is
serialized as a parameterless destructor unless the source/API contract
explicitly declares a `within` destructor.

## Generated Cleanup And `finally`

`finally` lowering creates cleanup scopes, active flags, generated locals, and
transfer rewrites. Return/break/continue paths must run pending cleanups in the
same cases the source language promises.

Generated cleanup can include:

- `delete` of a retained value;
- invocation of a cleanup method;
- deletion of expanded return components;
- cleanup of lambda/postpone contexts;
- destructor calls for initialized storage;
- propagation of stored return values after cleanup.

When cleanup is generated from an expression, keep provenance to the source
expression for diagnostics and dumps. Avoid duplicating side effects: evaluate
the source expression into a generated local, guard cleanup with an active flag
when needed, and clear the flag after cleanup.

Source `goto` is not a structured transfer for cleanup purposes. The current
lowering path rewrites `return`, `break`, and `continue` through pending
cleanup, but leaves source `goto` as a low-level branch. A `goto` that exits a
`try`/`finally` region can therefore bypass the `finally` body. Flow analysis
may diagnose or warn about that source pattern, but lowering must not silently
turn a source `goto` into a structured cleanup transfer.

## Async And Iterator Restrictions

Generated async and iterator state can retain values across suspension or
yield. Construction and cleanup rules must prevent stack-like storage from
escaping through generated frames.

Restrictions include:

- stackalloc-backed storage in async/iterator bodies where activation-stack
  storage would cross suspension;
- retention of scoped pointer-bearing values in async frames or iterator
  states;
- allocator values retained across suspension without escaped/frame-safe
  lifetime;
- destructors or cleanup methods that would run on storage whose initialization
  did not complete.

Generated iterator state includes lifted local fields and destructor/adapter
methods. Async state includes live locals, result/error slots, continuation
state, and cleanup fields. Both must participate in lifecycle analysis.

## Extern Type Boundaries

Extern classes and native base types define ownership boundaries. Camp must not
generate lifecycle calls that imply ownership of native state it does not own.

Extern class rules include:

- extern classes may not declare ordinary instance fields;
- extern class constructors/destructors must be extern;
- non-extern classes may not inherit from extern classes, and extern classes
  may not inherit from non-extern classes;
- allocating an extern class requires an extern constructor/create method;
- deleting an extern class requires an explicit destructor.

For native interop, `@symbol` and extern declarations control emitted names,
but they do not change ownership. The compiler should emit calls exactly where
the source/extern surface says they exist, and diagnose missing lifecycle
surfaces rather than inventing them.

## Interface And Virtual Lifecycle

Interface constructor/destructor slots are lifecycle contracts and must be
validated with interface conformance. Constructor-bearing interfaces may be
implemented only by structs or sealed classes.

Interface lifecycle conformance uses exact helper shapes. A destructor declared
as `~T()` does not implement `~I(within allocator)`, and `~T(within allocator)`
does not implement `~I()`. Retained allocator classes therefore implement
parameterless destructor contracts when their cleanup uses the retained
allocator field instead of a call-site allocator.

Virtual class lifecycle interacts with vtable assignment:

- create/init helpers must assign the correct vtable pointer;
- virtual destructors require a root slot;
- derived destructors must override/seal inherited virtual destructor slots;
- generated delete helpers for destructors should be recognized as lifecycle
  helpers, not ordinary duplicate methods.

See [Interface VTables And Dynamic Dispatch](09-interface-vtables-and-dynamic-dispatch.md).

## Metadata And API

Metadata should expose source constructors, destructors, lifecycle shape,
extern status, visibility, parameters, and return information. Generated create,
init-new, delete, retained allocator fields, vtable assignment, cleanup, and
helper functions should be omitted unless they are part of exported source API.

API headers must expose enough lifecycle declarations for downstream Camp code
to type-check construction/destruction. C emission may include generated helper
functions and must keep their symbols stable enough for native build artifacts.

## Diagnostics

Diagnostics should distinguish missing constructors, invalid destructors,
invalid allocation context, unsafe retention, and invalid extern lifecycle
surfaces.

Important diagnostic categories include:

- missing required constructor argument;
- invalid constructor/destructor declaration placement;
- destructor returns a value;
- extern class allocation without an extern constructor/create method;
- extern class delete without explicit destructor;
- invalid virtual destructor hierarchy;
- struct constructor definite-assignment failure;
- invalid `within` default or missing explicit within context;
- unsafe allocator/lifetime retention;
- invalid retained allocator declaration shape;
- invalid retained allocator field access or assignment;
- stackalloc-backed storage in async or iterator body;
- invalid cleanup method or finally cleanup target.

## Test Surface

Lifecycle changes should cover:

- constructor overload and missing-argument diagnostics;
- generated and user constructors;
- struct definite assignment;
- selected destination construction lowering;
- `stackalloc` array, instance, prep, and interpolation lowering;
- `new` allocation and constructor ordering;
- `delete` destructor/free ordering;
- retained allocator construction, destruction, API, metadata, diagnostics, and
  inheritance forwarding;
- extern class lifecycle failures;
- virtual destructor rules;
- interface constructor/destructor conformance;
- within parameter/call policy;
- async/iterator storage restrictions;
- finally cleanup and transfer paths.

## Implementation Anchors

Primary implementation points include:

- `BindableNodeAnalyzer.Declarations.cs` for lifecycle declaration analysis,
  within parameters, and async allocator validation;
- `BindableNodeAnalyzer.DeclarationValidation.cs` for extern, virtual, and
  interface lifecycle rules;
- `BindableNodeAnalyzer.MethodBody.cs` for construction/delete analysis;
- `BindableNodeAnalyzer.Lowering.Expressions.cs` and
  `BindableNodeAnalyzer.Lowering.Statements.cs` for selected construction,
  `stackalloc`, `new`, `delete`, and hidden argument insertion;
- `BindableNodeAnalyzer.Lowering.Exceptions.cs` for cleanup/finally lowering;
- `BindableNodeAnalyzer.LifetimeFacts.cs` for retention and allocator lifetime;
- lifecycle, extern, within, virtual destructor, async, iterator, and cleanup
  fixtures under `tests/Diagnostics` and generated-code tests.
