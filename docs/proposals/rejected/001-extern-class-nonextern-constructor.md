# Rejected Proposal: Non-Extern Constructors On Derived Extern Classes

Status: Rejected.

This proposal has been rejected. The primary reason is that allowing a class to
be declared `extern` while also giving it a non-extern constructor can become
silently confusing when a type with the same name or conceptual identity already
exists in the external library. The Camp declaration would look like an
extension view, but the generated `create` wrapper could make it appear as
though Camp owns or meaningfully specializes a foreign type. That mismatch can
lead to surprising ABI behavior without a clear source-level warning.

This proposal should be replaced by a future cross-library extension proposal
that addresses this ambiguity directly and provides a more general mechanism for
adding typed views, helper methods, and construction/customization behavior
across ABI boundaries.

## Summary

The proposed feature would have allowed a non-extern constructor inside an
`extern class`, but only when the class derived from another `extern class` that
already had an accessible constructor or `create` surface.

The constructor would not have generated normal Camp lifecycle machinery. It
would have generated only a `TypeName_create` method. That generated `create`
method would have called the base class `create`, cast the returned pointer to
the derived extern class pointer type, null-checked it, and then run the
constructor body with `this` statically typed as the derived class.

## Intended Semantics

The feature was intended to support typed views over opaque external objects:

```camp
extern class NativeWidget
{
	extern NativeWidget(int kind);
}

extern class NativeButton : NativeWidget
{
	NativeButton()
	{
		base(1);
		this.configureButtonDefaults();
	}

	void configureButtonDefaults()
	{
	}
}
```

Conceptually, this would have lowered to:

```camp
static NativeButton* create()
{
	NativeButton* this = (NativeButton*)NativeWidget.create(1);
	if (this != null)
		this.configureButtonDefaults();
	return this;
}
```

No `NativeButton_op_initnew` would have been generated. The class would still
have had no Camp-owned layout and no virtual behavior.

## Proposed Rules

- A non-extern constructor would have been allowed only on an `extern class`
  that derives from another `extern class`.
- The base class would have needed an accessible matching constructor or
  `create` method.
- `base(...)` would have followed ordinary constructor UX: it must be the first
  constructor action, and if omitted, the compiler would use the base
  parameterless create surface.
- The generated method would have been `Derived_create`, never
  `Derived_op_initnew`.
- The generated method would have called the base create method, cast the result
  to `Derived*`, and run the constructor body only when the result was non-null.
- No allocation, zeroing, vtable assignment, layout initialization, or hidden
  field initialization would have been generated.
- Extern class destructors would still have been required to be `extern`; Camp
  would not provide body-around-base-destroy semantics.

## Rejection Rationale

The feature blurs two concepts that should remain visibly distinct:

- importing a foreign class that already exists in another library;
- creating a local typed extension/view over an object produced by another API.

Because both would use `extern class`, source code could appear to declare a
foreign type while actually generating new local construction behavior for that
type name. If the external library also exposes a type or construction surface
with the same conceptual meaning, the result can be silently weird: calls may
bind to generated Camp wrappers while readers expect direct foreign ABI
surfaces, or a local view may be mistaken for a real externally recognized
derived type.

The desired capability is still valuable, but it needs a clearer language
mechanism that explicitly models cross-library extension views instead of
overloading `extern class` constructors.

## Future Direction

A replacement proposal should consider:

- explicit syntax for declaring a local view or extension of an external type;
- clear separation between imported foreign lifecycle surfaces and generated
  local helper/create wrappers;
- diagnostics that prevent accidental shadowing of foreign class identities;
- support for constructor-like setup hooks without implying Camp-owned layout;
- explicit rules for API header output, metadata, and cross-module consumption.

