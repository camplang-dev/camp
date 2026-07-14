# Construction, Destruction, And Allocation

This supplement describes lifecycle lowering: constructors, `init`, `new`,
destructors, `delete`, `within` allocation contexts, generated cleanup, and
extern lifecycle boundaries. User-facing syntax appears in
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

- source construction kind: `init` or `new`;
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

## `init`

`init` constructs into source storage. Async bodies must reject `init` array
construction when declaration-scope storage would cross suspension.

`init` is a construction-into-existing-storage operation. It should lower to an
init helper call using the address of the target storage. In assignment form,
the compiler rewrites:

```camp
slot = init Widget(args);
```

into a call that constructs at `&slot`, with generated vtable assignment placed
before or around the init call when the target type participates in virtual
dispatch.

`init T[n]` style array construction is restricted in async and iterator bodies
when the storage would be lifted across suspension/yield. Use fixed storage,
ordinary allocation, or another explicit storage strategy in source code.

## Initializer Lists

Initializer lists can initialize fields by position or name. The analyzer must
map initializer items to fields before lifetime retention checks and lowering.
For pointer-bearing fields, initializer values contribute retained lifetime
facts to the constructed aggregate.

Initializer lowering must preserve field order, field names, defaulted fields,
fixed storage, and expanded forms. A positional initializer should not depend on
reflection or metadata order; it uses declaration order.

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

## Async And Iterator Restrictions

Generated async and iterator state can retain values across suspension or
yield. Construction and cleanup rules must prevent stack-like storage from
escaping through generated frames.

Restrictions include:

- init-array construction in async/iterator bodies where declaration-scope
  storage would be lifted;
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
implemented only by structs or sealed classes under the current rules.

Virtual class lifecycle interacts with vtable assignment:

- create/init helpers must assign the correct vtable pointer;
- virtual destructors require a root slot;
- derived destructors must override/seal inherited virtual destructor slots;
- generated delete helpers for destructors should be recognized as lifecycle
  helpers, not ordinary duplicate methods.

See [Interface VTables And Dynamic Dispatch](09-interface-vtables-and-dynamic-dispatch.md).

## Metadata And API

Metadata should expose source constructors, destructors, lifecycle attributes,
extern status, visibility, parameters, and return information. Generated create,
init-new, delete, vtable assignment, cleanup, and helper functions should be
omitted unless they are part of exported source API.

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
- init-array construction in async or iterator body;
- invalid cleanup method or finally cleanup target.

## Test Surface

Lifecycle changes should cover:

- constructor overload and missing-argument diagnostics;
- generated and user constructors;
- struct definite assignment;
- `init` assignment lowering;
- `new` allocation and constructor ordering;
- `delete` destructor/free ordering;
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
  `BindableNodeAnalyzer.Lowering.Statements.cs` for `init`, `new`, `delete`,
  and hidden argument insertion;
- `BindableNodeAnalyzer.Lowering.Exceptions.cs` for cleanup/finally lowering;
- `BindableNodeAnalyzer.LifetimeFacts.cs` for retention and allocator lifetime;
- lifecycle, extern, within, virtual destructor, async, iterator, and cleanup
  fixtures under `tests/Diagnostics` and generated-code tests.
