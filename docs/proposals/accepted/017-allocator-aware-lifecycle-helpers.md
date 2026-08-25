# Allocator-Aware Lifecycle Helpers

## Status

Accepted.

## Proposal Date

2026-08-14

## Last Updated Date

2026-08-25

## Summary

This proposal replaces the current `@createWithAllocator` lifecycle escape
hatch with ordinary allocator-aware lifecycle rules.

Generated `create()` and `destroy()` helpers should expose allocator parameters
when the class lifecycle requires them. The decision is based on the effective
`within` policy for the source file and on constructor/destructor `within`
parameters, not on a special attribute.

The proposal also adds one narrow constructor syntax:

```camp
class List
{
	List(nuint capacity, within this.allocator)
	{
	}
}
```

`within this.allocator` is valid only on class constructor `within` parameters.
It introduces a normal allocator field, initializes that field at the start of
`_op_initnew`, and makes generated `destroy()` free the complete instance using
the retained allocator.

The key invariant is:

> If caller-provided allocation participates in constructing an instance, the
> same allocator must be used, either by the caller or by retained provenance,
> to deallocate the complete instance.

## Motivation

Camp has two lifecycle layers:

- `_op_initnew` and `_op_delete` construct and destroy storage selected by the
  caller;
- `create()` and `destroy()` allocate/free complete instances for out-of-module
  callers, interface constructor implementations, and API consumers.

The current `@createWithAllocator` attribute can force an allocator parameter
onto generated `create()`, but it does not naturally keep generated
`destroy()` paired with the same allocation source. This makes allocator-aware
classes harder to author correctly and harder to explain.

Lifecycle helper shape should come from ordinary language semantics:

- file/build `within` policy;
- constructor `within` parameters;
- destructor `within` parameters;
- whether the class retains the allocator used to allocate the instance.

That gives library authors allocator-capable ABI surfaces by default while
keeping ordinary application code simple under implicit `within` policy.

## Generated Helper Policy

Remove `@createWithAllocator`.

For classes that do not retain their allocator, generated helper shape is:

| Source lifecycle shape | Effective `within` policy | Generated `create()` | Generated `destroy()` |
| --- | --- | --- | --- |
| no constructor/destructor `within` | implicit | no allocator parameter | no allocator parameter |
| no constructor/destructor `within` | explicit | has allocator parameter | has allocator parameter |
| constructor has `within` | any | has allocator parameter | has allocator parameter |
| destructor has `within` | any | has allocator parameter | has allocator parameter |

When an ordinary generated `create()` has an allocator parameter, it uses that
allocator to allocate the complete instance. When an ordinary generated
`destroy()` has an allocator parameter, it uses that allocator to free the
complete instance.

The effective `within` policy is the result of the whole policy process:

- command-line `--explicit-within` / `--implicit-within` options;
- build-file options;
- build-kind default;
- per-file `#within` directives.

The current defaults fit this model:

- static/shared library builds use explicit `within`;
- executable and C-only builds use implicit `within`.

The effective policy does not need to be exported as a separate API or metadata
fact. Generated helper signatures are the ABI contract, and imported API
consumers should use the serialized signatures.

When a lifecycle helper would gain an allocator parameter only because of the
effective `within` policy, the compiler must be able to name the source
allocator type. If the source surface does not define or import `Allocator`,
such as in a `--nostdlib` API-only source that has no allocator declaration,
the compiler must not synthesize an impossible allocator parameter. Explicit
source lifecycle shapes still require their declared types to be visible and
serializable.

Generated allocator parameters on lifecycle helpers behave as lifecycle
`within` slots, even if the emitted C ABI is an ordinary `Allocator*`
parameter. They participate in the same lowering and diagnostics as other
`within` parameters.

For example:

- `within(context) new Widget()` supplies the allocator to `Widget.create`;
- `within(default)` intentionally requests the default allocator path;
- explicit-`within` diagnostics still apply;
- hidden argument ordering remains consistent;
- API-imported lifecycle helpers lower according to the imported signature.

## Constructor And Destructor Symmetry

For classes that do not retain their allocator:

- if an explicit constructor has a `within` parameter, an explicit destructor
  must also have a `within` parameter;
- if an explicit destructor has a `within` parameter, every explicit
  constructor must also have a `within` parameter;
- if one side is compiler-generated, the generated lifecycle surface carries
  the allocator parameter when required.

For classes that retain their allocator with `within this.<field>`, generated
`destroy()` does not receive an allocator parameter because the instance knows
how to free itself. A class with a retained allocator constructor must not
declare a destructor with a `within` parameter.

## Retained Allocator Constructor Parameters

The syntax:

```camp
class Buffer
{
	Buffer(nuint size, within this.allocator)
	{
	}
}
```

means:

- the compiler introduces a normal instance field named `allocator`;
- externally, the constructor parameter is named `allocator`;
- `_op_initnew` assigns the parameter value to `this.allocator` immediately on
  entry, before the user constructor body runs;
- generated `create()` uses the constructor `within` parameter to allocate the
  complete instance;
- generated `destroy()` does not receive an allocator parameter;
- generated `destroy()` calls the internal delete/destructor path, then frees
  the complete instance using `this.allocator`.

For the source form above, the generated public shape is:

```camp
Buffer(nuint size, within allocator);
static Buffer* create(nuint size, within allocator);
void destroy();
```

This syntax is not a general parameter-property feature. It is a narrow
allocator-provenance feature for class constructors.

The introduced field has the canonical allocator pointer type:

```camp
Allocator* allocator;
```

The `this.` prefix is not part of the API parameter name. API headers and
metadata should expose the constructor parameter as `within allocator`, while
compiler-owned layout information should include the generated field as an
ordinary field.

Normal field collision rules apply. If the class already declares a field named
`allocator`, `within this.allocator` is a duplicate declaration.

The compiler should reject:

- duplicate retained allocator parameters;
- retained allocator syntax on non-constructor methods;
- retained allocator syntax on non-class constructors;
- retained allocator syntax on non-`within` parameters;
- retained allocator syntax on derived classes;
- retained allocator syntax on shadow classes;
- retained allocator syntax on extern class constructors.

The retained allocator field stores allocation provenance. Source assignment to
that field is allowed but should warn. The compiler-generated `_op_initnew`
entry assignment is exempt from that warning.

## Retained Allocator Lowering

For:

```camp
class List
{
	List(nuint capacity, within this.allocator)
	{
	}
}
```

generated `create()` conceptually lowers as:

```c
List *List_create(uintptr_t capacity, Allocator *allocator)
{
	List *created = allocator != NULL
		? Allocator_alloc(allocator, sizeof(List))
		: malloc(sizeof(List));

	if (created != NULL)
	{
		*created = (List){0};
		List_op_initnew(created, capacity, allocator);
	}

	return created;
}
```

`create()` does not assign the retained allocator field. The assignment belongs
in `_op_initnew` so every construction path has the same behavior:

```c
void List_op_initnew(List *this, uintptr_t capacity, Allocator *allocator)
{
	this->allocator = allocator;
	/* generated setup and user constructor body follow */
}
```

Generated `destroy()` conceptually lowers as:

```c
void List_destroy(List *this)
{
	List_op_delete(this);

	Allocator *allocator = this->allocator;
	if (allocator != NULL)
		Allocator_free(allocator, this);
	else
		free(this);
}
```

The generated destroy path does not need to capture `this.allocator` before
`_op_delete`. Reassigning the retained allocator field is already a warning,
and generated code should use the direct retained-provenance model.

Generated `destroy()` must not recursively call `destroy()` from inside
`destroy()`. It calls the internal delete/destructor helper, then frees the
complete object storage.

`_op_initnew` and `_op_delete` remain storage-neutral helpers. When they are
called directly, the caller still controls allocation and deallocation.

## Constructor Body Name Lookup

Inside the constructor, the retained allocator should be accessed as:

```camp
this.allocator
```

not:

```camp
allocator
```

The external parameter is still named `allocator`, but the constructor body
should use the field spelling. This keeps source aligned with the fact that the
value has been retained as object state.

Allocations inside the constructor still use the ordinary `within` behavior of
the constructor parameter:

```camp
class List
{
	List(nuint capacity, within this.allocator)
	{
		this.items = new int[capacity];
	}
}
```

Other methods do not inherit an implicit allocation context from the field.
They must write the allocation context explicitly:

```camp
within (this.allocator)
	delete this.items;
```

## Derived Classes

If a base type constructor has a `within` parameter, derived type constructors
must also have a `within` parameter, and that parameter must be passed directly
to the base constructor.

This applies whether the base constructor retains the allocator with
`within this.<field>` or accepts an ordinary `within` parameter. Whether the
base retains the allocator is an implementation detail, even to derived types.

This should be valid:

```camp
class Base
{
	Base(within this.allocator)
	{
	}
}

class Derived: Base
{
	Derived(within allocator)
	{
		base();
	}
}
```

The explicit `base();` call is optional in this case. If omitted, the compiler
should implicitly call the base constructor and forward the derived
constructor's `within` parameter.

This should be rejected:

```camp
class Derived: Base
{
	Derived()
	{
	}
}
```

This should also be rejected:

```camp
class Base
{
	Base(nuint capacity, within this.allocator)
	{
	}
}

class Derived: Base
{
	Derived(nuint capacity, within allocator)
	{
		base(capacity, SomeOtherAllocator);
	}
}
```

The required allocator must be the derived constructor's own `within`
parameter, forwarded directly through the normal `within` argument mechanism.
Source does not write `within` as a modifier at the base call site.

Derived classes must not use `within this.<field>`. The lifecycle root owns
complete-object allocator provenance. Derived constructors forward their
`within` parameter to the base.

The derived-class rule applies to:

- explicit base constructor calls;
- implicit base constructor calls;
- compiler-generated default lifecycle constructors;
- API-imported base classes.

The base constructor is the key rule because allocator participation enters the
object lifecycle during construction. Derived classes should not need to know
whether the base retains the allocator, requires the caller to supply it again
to `destroy`, or uses it only for child resources. Forwarding the derived
constructor's own `within` parameter preserves that abstraction and allows the
base implementation to evolve.

## Generated Default Constructors

Existing wording such as "parameterless constructor" becomes imprecise when the
compiler may generate a `within` allocator slot.

Documentation and diagnostics should prefer terms such as:

- "implicit default constructor";
- "compiler-generated default lifecycle constructor";
- "source-parameterless constructor".

The intent remains the same: the compiler generated the constructor because the
class has no source constructor. The generated constructor may still include a
`within` parameter when lifecycle rules require one.

## Destructor Hierarchies

Existing destructor hierarchy rules remain responsible for virtual destructor
correctness. Allocator rules add lifecycle pairing requirements.

In a hierarchy:

- if the base constructor has `within`, derived constructors must have
  `within` and forward it directly;
- if the lifecycle root uses retained allocator syntax, derived classes cannot
  use retained allocator syntax;
- a retained-allocator root destructor must not declare a `within` parameter.

Generated destroy code for a retained-allocator virtual destructor root must
destroy the correct dynamic type and then free the complete object pointer, not
a base subobject pointer.

## Interface Constructors And Destructors

Interface lifecycle shape is explicit. The compiler should not synthesize an
allocator for an interface destructor call.

The destructor `within` parameter is optional:

```camp
interface ICallerFreed
{
	ICallerFreed(within allocator);
	~ICallerFreed(within allocator);
}

interface ISelfFreed
{
	ISelfFreed(within allocator);
	~ISelfFreed();
}
```

Implementing types must match the interface lifecycle shape:

- an interface destructor with `within` requires an implementation destroy path
  with `within`;
- an interface destructor without `within` requires an implementation destroy
  path without `within`;
- a retained-allocator implementation naturally matches a destructor slot
  without `within`;
- the compiler never invents or recovers the allocator sent to an interface
  destructor.

An implementing type must not rely on a policy-generated
`destroy(within allocator)` when the interface destructor lacks `within`, or on
a policy-generated `destroy()` when the interface destructor requires `within`.
The shape must match.

## Extern And Shadow Classes

Extern classes should not receive generated allocator semantics unless their
extern API declares them. The compiler cannot assume foreign object storage is
Camp-allocator-owned.

For extern classes:

- extern `create` and `destroy` signatures are authoritative;
- `new` and `delete` use the exposed extern lifecycle surface;
- retained allocator fields are not synthesized.

Shadow classes cannot use `within this.<field>`. Shadow data follows the
existing shadow lifecycle model, and the base object lifecycle is owned by the
foreign/base API.

In particular:

- shadow classes cannot declare destructors;
- `delete shadow` frees generated shadow data;
- shadow classes that need matching allocator cleanup should store the required
  state explicitly and use it at the deletion site.

## Diagnostics

Diagnostics should describe the lifecycle rule and the fix.

Constructor/destructor mismatch:

```text
Constructor for class 'Buffer' has a within allocator parameter. The destructor
must also declare a within allocator parameter, or the constructor must retain
the allocator with 'within this.allocator'.
```

Destructor/constructor mismatch:

```text
Destructor for class 'Buffer' has a within allocator parameter. Each explicit
constructor must also declare a within allocator parameter.
```

Invalid retained allocator on derived class:

```text
Constructor parameter 'within this.allocator' is valid only on the lifecycle
root class. Derived constructors must declare 'within allocator' and forward it
to the base constructor.
```

Base constructor forwarding:

```text
Base class 'Base' constructor has a within allocator parameter. Constructor for
derived class 'Derived' must declare a within allocator parameter and pass it
directly to the base constructor.
```

Unqualified constructor access:

```text
Retained allocator parameter 'allocator' is stored as a field. Use
'this.allocator' inside the constructor.
```

Assignment warning:

```text
Field 'allocator' stores the instance allocation provenance. Assigning it may
cause the instance to be freed with a different allocator than the one that
created it.
```

Missing exported allocator:

```text
Exported lifecycle helper 'Widget.create' exposes type 'Allocator'. Export or
re-export 'Allocator', or compile this file with implicit within policy.
```

## Compiler Implementation

Remove:

- parser/binder support for `@createWithAllocator`;
- lifecycle generation paths that special-case `@createWithAllocator`;
- documentation for `@createWithAllocator` as an active feature.

Add:

- parser support for `within this.<identifier>` only in class constructor
  parameter lists;
- AST/binder representation for retained allocator constructor parameters;
- generated field creation with ordinary duplicate-name checking;
- a field flag or equivalent marker for retained allocator provenance;
- assignment warnings for retained allocator fields outside generated
  `_op_initnew` entry assignment;
- lifecycle helper generation based on effective `within` policy and
  constructor/destructor shape;
- `_op_initnew` entry assignment before user constructor body;
- retained-allocator `destroy()` lowering;
- derived-class validation for direct `within` forwarding;
- interface lifecycle exact-shape validation;
- API/metadata serialization that exposes `within allocator`, not
  `within this.allocator`, as the constructor parameter spelling.

Suggested implementation order:

1. Remove `@createWithAllocator` recognition and expected tests.
2. Centralize generated lifecycle helper shape selection.
3. Add parser/binder support for retained allocator parameters and generated
   fields.
4. Add `_op_initnew` and `destroy()` lowering for retained allocators.
5. Add constructor/destructor symmetry diagnostics.
6. Add derived-class and interface lifecycle validation.
7. Update standard library containers.
8. Update API/header/metadata import/export tests.

## Documentation Updates

Update the living documentation only. Do not update accepted/rejected proposals
or prior release notes as part of this work; those are historical records.

Language guide updates:

- class construction and destruction;
- `within` policy and allocator-aware APIs;
- generated `create()` and `destroy()` helpers;
- retained allocator constructor syntax;
- inheritance rules for allocator-aware base constructors;
- interface constructor/destructor shape.

Semantic documentation updates:

- lifecycle helper generation;
- `_op_initnew` and `_op_delete` storage-neutral behavior;
- retained allocator field semantics;
- destructor hierarchy interaction;
- interface lifecycle conformance.

LLM coding guide updates:

- when to use ordinary `within allocator`;
- when to use `within this.allocator`;
- how to write allocator-aware library classes;
- how to derive from allocator-aware base classes;
- how to interpret diagnostics about generated lifecycle helpers.

Remove active documentation references to `@createWithAllocator`.

## Standard Library Updates

Allocator-owning standard library classes should use retained allocator
constructor syntax where appropriate.

Likely affected types include:

- `List<T>`;
- `HashMap<K, V>`;
- `HashSet<K>`;
- other owning containers or buffers that store an `Allocator*`.

For example:

```camp
public class List<T: copyable>
{
	public List(within this.allocator, sizeof(T))
	{
	}

	public ~List()
	{
		within (this.allocator)
			delete this.items;
	}
}
```

Generated `List.create(..., within allocator)` allocates the list object with
the selected allocator. Generated `List.destroy(list)` frees the list object
using its retained allocator. Stack/fixed construction still uses
`_op_initnew` and `_op_delete` without freeing stack/fixed storage.

Public standard library APIs that expose allocator-aware lifecycle helpers must
export or re-export `Allocator` as needed for library builds using explicit
`within` policy.

## Existing Package Impact

This is an ABI-breaking change for packages that export classes.

Likely impacts:

- generated API headers change for exported/public classes in explicit-policy
  library builds;
- generated metadata changes for lifecycle helper signatures;
- generated default lifecycle helper names may remain the same, but signatures
  can gain allocator slots;
- downstream packages must be rebuilt;
- package tests with expected C output need updates;
- classes with constructor `within` and destructor without `within` must either
  use retained allocator syntax or update the destructor shape;
- derived classes of allocator-aware bases must add and forward `within`
  parameters.

Because Camp is still in preview, this break should happen before the package
ecosystem and generated package ABI stabilize.

## Test Plan

Add targeted compiler tests for:

- implicit-policy class with no lifecycle `within` produces helpers without
  allocator parameters;
- explicit-policy class with no lifecycle `within` produces helpers with
  allocator parameters;
- constructor `within` produces allocator-aware `create()` and `destroy()`;
- destructor `within` produces allocator-aware `create()` and `destroy()`;
- constructor `within` plus explicit destructor without `within` diagnoses;
- destructor `within` plus explicit constructor without `within` diagnoses;
- `within this.allocator` creates a real field;
- duplicate retained allocator parameters diagnose;
- retained allocator syntax is rejected outside class constructors;
- retained allocator syntax is rejected on non-`within` parameters;
- retained allocator syntax is rejected on extern and shadow class
  constructors;
- `within this.allocator` exposes the constructor parameter externally as
  `allocator`;
- retained allocator assignment occurs immediately at `_op_initnew` entry;
- retained allocator field assignment warns;
- unqualified retained allocator access in the constructor diagnoses;
- generated retained-allocator `destroy()` calls `_op_delete` and then frees
  through `this.allocator`;
- direct same-module `new` and generated `create()` both reach `_op_initnew`
  assignment;
- stack/fixed construction does not free object storage;
- derived constructor must declare `within` when base constructor has
  `within`;
- derived constructor directly forwards its `within` parameter to the base;
- `within this.allocator` is rejected on derived classes;
- virtual destructor root with retained allocator destroys dynamic type and
  frees complete object storage;
- interface destructor with `within` requires matching implementation shape;
- interface destructor without `within` accepts retained-allocator
  implementation shape;
- imported API consumers use serialized lifecycle helper signatures;
- `@createWithAllocator` is rejected or reported as unknown after removal.

Add standard library regression tests using a tracking allocator for:

- `List<T>` object allocation and destruction;
- `HashMap<K, V>` object allocation and destruction;
- `HashSet<K>` object allocation and destruction;
- leak-checking behavior under `@test`.

Expected-C tests should verify helper signatures and lowering order where the
compiler test suite already uses generated C comparisons.

## Example Verification

Examples using `within this.allocator` cannot be smoke-tested until parser
support for this proposal exists. The examples in this proposal are therefore
written as proposed source syntax.

Examples that use base constructor calls intentionally do not write `within` as
a call-site modifier. The `within` parameter is supplied by the constructor's
allocation context and forwarded by the compiler where required.

When implementation begins, the first smoke tests should be parser-only tests
for retained allocator syntax and generated-C tests for `_op_initnew`
assignment order.

## Regression Risks

ABI compatibility:

- exported class helper signatures can change;
- downstream packages must be rebuilt;
- generated API/header tests may need broad expected-output updates.

Allocator correctness:

- helper-generation rules must keep `create()` and `destroy()` paired;
- retained allocator destroy must free the complete object pointer;
- virtual destructor dispatch must not lose the complete object address.

Policy correctness:

- consumers must use imported helper signatures, not recompute lifecycle shape
  from their own `within` policy;
- generated helper allocator parameters must behave as `within` slots, not
  ordinary `Allocator*` parameters.

Inheritance correctness:

- derived constructors must not change allocator provenance before calling the
  base constructor;
- retained allocator ownership must remain rooted at one class in the
  hierarchy.

Implementation churn:

- removing `@createWithAllocator` touches parser/binder diagnostics, generated
  lifecycle code, API serialization, and tests;
- standard library container updates may expose existing assumptions in
  allocator-aware tests.

## Recommendation

Implement the proposal before package metadata and third-party package ABI
stabilize.

The retained allocator constructor syntax is narrow, but it captures an
important lifecycle contract at the exact place where allocator provenance
enters an object. It is more coherent than `@createWithAllocator`, avoids a
separate `@destroywith` attribute, and keeps the generated `create()` /
`destroy()` surface learnable through ordinary `within` rules and targeted
diagnostics.
