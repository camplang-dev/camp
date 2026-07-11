# Construction, Destruction, And Allocation

## Constructor Binding

Constructor binding resolves the target type, overloads, receiver/storage
target, generic substitutions, and initializer arguments. Constructors may be
generated or user-defined.

## Default Constructors

Default constructors are generated where the type shape permits it. Generated
constructors must initialize fields according to language defaults and respect
visibility/export rules.

## Destructors

Destructors release owned resources and must return `void`. Lowering must call
destructors at explicit `delete` sites and generated cleanup points where the
language requires it.

## Base Initialization

Derived class constructors initialize base state according to class hierarchy
rules. Extern base classes constrain what source constructors may do.

## `init`

`init` constructs into source storage. Async bodies must reject `init` array
construction when declaration-scope storage would cross suspension.

## `new`

`new` allocates through the selected `within` context, then constructs. Lowering
must preserve allocation order, zeroing rules where required, constructor call,
and cleanup on failure paths.

## `delete`

`delete` destroys and frees a value according to its type and allocation
context. Lifetime analysis validates that the deleted value is compatible with
the free surface.

## Allocator Selection

Allocator selection comes from explicit `within`, current within context,
within parameters, or the compiler's implicit/explicit allocation policy.

## Async And Iterator Restrictions

Generated async and iterator state can retain values across suspension or
yield. Construction and cleanup rules must prevent stack-like storage from
escaping through generated frames.

## Extern Type Boundaries

Extern classes and native base types define ownership boundaries. Camp must not
generate lifecycle calls that imply ownership of native state it does not own.

## Diagnostics

Diagnostics should distinguish missing constructors, invalid destructors,
invalid allocation context, unsafe retention, and invalid extern lifecycle
surfaces.
