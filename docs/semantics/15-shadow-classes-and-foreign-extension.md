# Shadow Classes And Foreign Extension

Shadow classes are a source-level object model for extending an existing class
instance without owning or changing that instance's physical layout. They are
intended for native, plugin, UI, and cross-module APIs where a base library owns
an opaque object and exposes an attachment slot for consumer state.

A shadow class pointer has the same physical representation as a pointer to its
first non-shadow base object. The shadow class's own fields, virtual dispatch
state, implemented interface state, and generated helper state live in a
separate compiler-generated shadow data allocation. The base object stores an
opaque pointer to that allocation through hook methods marked `@getshadow` and
`@setshadow`.

## Source Forms

A shadow class is declared with the contextual `shadow` modifier before
`class`:

```camp
shadow class ButtonView: NativeButton, IControlHost
{
	int clicks;

	ButtonView()
	{
		this.clicks = 0;
	}

	void handleDestroy(): IControlHost
	{
		delete shadow;
	}
}
```

Shadow classes:

- must have a direct base class;
- may be ordinary, `virtual`, or `abstract`;
- may implement interfaces;
- may declare instance fields;
- may not be `sealed`;
- may not declare destructors;
- are treated as escaped class types for lifetime analysis;
- may not be constructed directly with selected construction or `stackalloc`.

When the direct base is not itself a shadow class, the visible base surface must
be non-virtual and non-abstract. A shadow class cannot override hidden base
virtuals from another artifact. It can define its own virtual shadow methods;
their dispatch state lives in shadow data.

## Shadow-Capable Base Hooks

The base surface visible to the shadow class must provide exactly one usable
getter hook and one usable setter hook. These hooks may be ordinary methods,
out-of-scope receiver methods, or extern methods if normal lookup can find them.

The getter hook is marked `@getshadow` and must be const-callable:

```camp
@getshadow
escaped void* getShadow(const this);
```

The setter hook is marked `@setshadow` and must be mutable-callable:

```camp
@setshadow
void setShadow(escaped void* value);
```

The required source-level shape is:

- `@getshadow` has no ordinary parameters, no `out` parameters, and returns an
  `escaped void*`-compatible value;
- `@setshadow` returns `void`, has exactly one ordinary `escaped void*`-
  compatible value parameter, and has no optional or `out` parameters;
- the receiver requirements must be compatible with the way the hook is called;
- overload or visibility ambiguity is an error.

The hooks are storage accessors only. They do not allocate shadow data, cast it
to a typed shadow representation, or perform cleanup. The shadow class lowering
supplies those operations.

## Representation

For each shadow class, the compiler generates a shadow data type. That generated
type is not source API. It may appear in lowering dumps, private generated C,
and private helper symbols, but it must not be exposed as an ordinary user
declaration in Camp API output or source-level metadata.

Generated shadow data contains the state required by the source declaration:

- source fields declared by the shadow class;
- virtual vtable state for virtual shadow methods;
- interface storage for interfaces implemented by the shadow class;
- a stored shadow instance pointer when interface thunks need to recover the
  original base object pointer;
- inherited shadow data prefixes for shadow inheritance.

The shadow class pointer itself remains the base object pointer. Upcasts from a
shadow class pointer to a visible base class pointer are representation-preserving.
There is no checked downcast from an arbitrary base pointer to a shadow class
pointer. A base pointer is not proven to be a shadow instance merely because a
hook could return an opaque pointer.

## Construction And Constructor Lowering

Construction has two separate responsibilities:

1. create or receive the base object instance;
2. allocate, zero, initialize, and install the shadow data.

For `new ShadowType(...)`, the compiler first follows the first non-shadow base
construction path. For an extern base, that path is an exposed extern
constructor or create method. For an ordinary non-shadow base, it is the ordinary
class allocation and initialization path.

After the base object exists, the compiler allocates shadow data using the
selected allocation context, zeroes it, initializes generated dispatch/interface
state, stores the shadow instance pointer when needed, and calls `@setshadow`.
Only then may the shadow constructor body run.

The generated `_op_initnew` helper for a shadow class assumes that shadow data is
already installed. It does not allocate, replace, or install shadow data. It also
does not initialize the non-shadow base object; the base object was already
created by the public `new` or create path.

Constructor binding is source-level. The analyzer selects the constructor before
lowering expands callables, `once` delegates, arrays, strings, or other expanded
forms into ABI components. Lowering must preserve that selected constructor
target. It must not rediscover the constructor by counting lowered ABI
arguments, because source arguments such as `escaped once void()` expand into
multiple physical parameters.

If a shadow class has no matching constructor or create method for the supplied
source arguments, the compiler reports an argument-count or missing-argument
diagnostic at the call site. Arguments must not be silently forwarded to the
base creation path unless the source constructor model says they are base
arguments.

## Field Access And Receiver Constness

Inside an instance method of a shadow class, `this` is still the shadow instance
pointer, which is physically the base object pointer. Field access for fields
declared by the shadow class is rewritten through the selected `@getshadow`
hook:

```camp
int getClicks(const this) => this.clicks;
```

The lowered method retrieves the opaque shadow pointer, casts it to the
generated shadow data pointer type, and reads `clicks` from the generated data.
Receiver constness applies to the typed shadow data pointer:

- a const receiver produces a const shadow data view;
- a mutable receiver produces a mutable shadow data view;
- writes through a const shadow receiver are rejected;
- uses of `this` itself still use the base object pointer.

If `@getshadow` returns `null`, the program is in invalid shadow-instance state.
The compiler does not insert a general runtime null check for every shadow field
access.

## Shadow Inheritance

A shadow class may derive from another shadow class. There is still one active
most-derived shadow data allocation per shadow instance.

Derived shadow data is prefix-compatible with base shadow data. A method
compiled for the base shadow class may retrieve the active shadow pointer and
cast it to the base shadow data type, reading the inherited prefix. A method
compiled for the derived shadow class uses the full most-derived layout.

Construction of a derived shadow class:

- creates the first non-shadow base object once;
- allocates one most-derived shadow data block;
- installs that block through the visible hook surface;
- calls base shadow `_op_initnew` helpers against the inherited prefix;
- calls derived shadow `_op_initnew` helpers against the full layout.

No `_op_initnew` in the shadow inheritance chain allocates or replaces shadow
data.

An exported shadow class may be used as a base for downstream shadow derivation
only when its exported API includes usable `@getshadow` and `@setshadow` hooks
on that exported shadow class surface. Downstream compilers must not reuse hooks
from the wrong base surface.

## Interfaces And Dynamic Dispatch

Shadow classes may implement ordinary Camp interfaces. Their interface state is
stored in shadow data, not in the non-shadow base object layout.

Converting a shadow class pointer to an implemented interface pointer returns a
pointer to the interface slot inside shadow data. Because that interface pointer
does not point at the base object, generated interface thunks recover the shadow
data container, read the stored shadow instance pointer, and then call the
source implementation method with the correct shadow receiver.

The lifetime of a shadow-backed interface pointer is tied to both the base
object and the shadow data. If either is invalidated, the interface pointer is
invalid. A `delete shadow` statement invalidates interface pointers that point
into that shadow data.

Virtual shadow dispatch also lives in shadow data. Shadow virtual dispatch uses
the shadow vtable state stored in the active shadow data allocation. Hidden base
virtual state remains part of the base library's object model and is not
overridden by shadow methods.

## `delete shadow`

`delete shadow` is a special delete statement, not a delete of a variable named
`shadow`.

It is valid only inside an instance method declared in the declaration scope of
the shadow class whose data is being deleted. It is invalid in:

- static methods;
- non-shadow class methods;
- ordinary helper methods outside the shadow class scope;
- lambdas or nested functions;
- unrelated methods that merely have a variable named `shadow`.

When valid, `delete shadow` frees the generated shadow data allocation using the
allocator context selected at the `delete shadow` site. It does not delete the
base object. It does not call a shadow class destructor, because shadow classes
cannot declare destructors. It does not automatically clear the base object's
opaque shadow pointer.

If the method containing `delete shadow` also has an accessible local variable
or parameter named `shadow`, the compiler warns on the `shadow` token that the
statement deletes generated shadow data, not the local value.

The compiler warns when a shadow class contains no reachable `delete shadow`
statement in its declaration scope. The warning is intentional rather than an
error because the base library's lifecycle model may intentionally own cleanup
elsewhere, but most shadow classes need to release shadow data from a destroy or
detach callback.

If a shadow constructor accepts a `within` allocator and a `delete shadow`
statement uses the default allocator, the compiler warns. The compiler does not
implicitly retain the constructor allocator for later deletion. If the class
needs matching custom allocator cleanup, store the allocator explicitly in a
shadow field and use it at the deletion site.

After an obvious same-body `delete shadow`, accessing shadow fields through
`this` is invalid and should be diagnosed. More complex double-delete and
use-after-delete paths are programmer errors unless the analyzer can prove them
locally.

## API And Metadata

Camp API output preserves the `shadow` modifier only when the exported API also
exposes usable `@getshadow` and `@setshadow` hooks for that shadow class
surface. If those hooks are not exported, a generated shared-library Camp API
header must erase the `shadow` modifier and present the class as an ordinary
extern class. A consumer cannot safely shadow-inherit without the hooks.

Source-level metadata may record that the original declaration was a shadow
class. It should include source-visible constructors, methods, interfaces, and
eligible hooks according to the selected metadata mode. It should not expose the
generated shadow data type, generated vtables, interface thunks, or helper
fields as ordinary source declarations merely because the class is exported.

Generated C/private headers may contain the generated shadow data structure,
thunks, vtables, and helper functions required by the implementation.

## Diagnostics

Diagnostics should point at the source token that explains the problem:

- `shadow` for invalid shadow modifiers or missing base class;
- the base type name for incompatible visible base classes;
- `@getshadow` or `@setshadow` for bad hook signatures or ambiguity;
- the constructor call or argument token for invalid shadow construction;
- the constructor type name for invalid direct shadow construction;
- the `delete shadow` statement for invalid deletion scope;
- the `shadow` token for a local-name collision warning;
- the shadow field access after an obvious delete.

Important diagnostics include:

- `sealed shadow class` is invalid;
- a shadow class requires a direct base class;
- a shadow class cannot declare a destructor;
- the first non-shadow base cannot be visibly virtual or abstract;
- missing, ambiguous, or incompatible hooks;
- direct construction of a shadow class;
- field access after an obvious `delete shadow`;
- no reachable `delete shadow` warning;
- default-allocator delete warning when a constructor accepted a `within`
  allocator;
- invalid constructor argument count or missing constructor argument.

## Test Surface

Shadow class coverage should include:

- parsing and API/metadata preservation or erasure of `shadow`;
- hook selection through instance, out-of-scope, and extern methods;
- hook signature diagnostics;
- construction through ordinary and extern bases;
- constructor calls whose source parameters lower to expanded ABI components;
- generated vtable assignment at the `new` or create site;
- field access through mutable and const receivers;
- `delete shadow` legality, warnings, and post-delete field diagnostics;
- shadow inheritance with inherited and appended fields;
- interface conversion and interface dispatch through shadow data;
- virtual shadow dispatch;
- exported project-reference/API-header consumption;
- diagnostics with line/column source ranges.

## Implementation Anchors

Key compiler areas:

- bindable model and parser support for the `shadow` class modifier;
- declaration validation for hooks, base restrictions, generated-name
  collisions, and `delete shadow` scope;
- expansion for generated shadow data, interface slots, virtual state, and
  lifecycle helpers;
- lowering for construction, constructor target preservation, field access,
  interface conversion, virtual dispatch, and `delete shadow`;
- lifetime facts for escaped shadow data and retained fields;
- API and metadata filtering for generated internals;
- editor tooling and LSP filtering so generated shadow internals are not
  offered as ordinary source members.
