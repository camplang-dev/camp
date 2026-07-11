# Lifetimes, Allocation, And `within`

## Lifetime Annotations

Camp uses lifetime annotations to describe whether references and pointer-like
values may escape, are scoped to the current call, or are known to outlive an
anchor.

Common annotations are `escaped`, `scoped`, and `unscoped(anchor)`.

```camp
export escaped byte* retain(escaped byte* data);
```

Lifetime annotations are part of API design. They tell callers whether a value
may be stored, returned, captured, yielded, or retained after a call. They also
tell implementers what obligations they have when assigning to fields,
constructing objects, returning pointers, or building delegates.

Lifetimes apply to pointer-bearing values and expanded forms that can carry
references. They are not a substitute for ownership documentation, but they
make the important storage relationships checkable by the compiler.

The most useful vocabulary is:

| Term | Meaning |
|---|---|
| Slot | A storage location such as a local, field, parameter, or component. |
| Value | The expression value read from a slot or produced by a call. |
| Anchor | A named parameter, receiver, or local that another lifetime depends on. |
| Escaped storage | Storage that can outlive the current call or scope. |
| Scoped value | A value valid only for the current call or declaration scope. |

## Default Lifetimes

Parameters receive default lifetime facts when no explicit annotation is
written. Defaults depend on the declaration context, receiver kind, and source
surface. Use explicit annotations when the API contract matters to callers.

Defaults are intentionally conservative. They make common signatures readable,
but they are not a license to store everything. When a function returns or
stores a pointer derived from a parameter, write the relationship explicitly.

```camp
export byte* firstByte(unscoped(data) byte[] data)
{
	return data.elements;
}
```

Specific result relations remain explicit because they matter to callers. A
return value that depends on a parameter should say so with `unscoped(anchor)`,
`scoped(anchor)`, or the appropriate lifetime form rather than relying on
readers to infer intent.

Class and interface pointer defaults are especially important. A class pointer
that escapes through a field or callback must satisfy escaped storage. An
interface pointer derived from a struct adapter is scoped and cannot satisfy an
escaped interface requirement.

## `escaped`, `scoped`, And `unscoped`

`escaped` means a value may be retained beyond the immediate scope. `scoped`
means a value is limited to the current scope or call. `unscoped(anchor)` states
that a value is proven to outlive a named anchor.

```camp
export void store(escaped byte* data);
export void inspect(scoped byte* data);
export byte* slice(unscoped(source) byte* source, nuint offset);
```

`escaped` means the value may be retained in escaped storage. It is required
for fields, escaped delegate contexts, heap-retained callbacks, and APIs that
store a value beyond the call.

`scoped` means the value is limited to the current scope. It can be passed to a
callee that promises not to retain it. It cannot be stored in escaped fields or
captured by escaped delegates.

`unscoped(anchor)` means the value is not limited to the immediate scope because
it is proven to outlive a named anchor. This is useful for views into a
parameter, receivers returning internal storage, and APIs that tie one pointer
to another object.

Bare `unscoped` and bare `scoped` have restricted meanings in signatures and
casts. Prefer anchor-bearing forms when the relationship is specific.

## Lifetime Anchors

Anchors are parameter, receiver, or local names visible where a lifetime
annotation or cast refers to them. The compiler reports unresolved anchors.

Anchors make lifetime relationships local and auditable:

```camp
export constof(this) byte* data(unscoped(this) Buffer* this);
export byte* findToken(unscoped(text) const char[] text, uchar token);
```

The anchor must be visible at the annotation site. A return type can anchor to a
parameter or receiver. A parameter can anchor to an earlier parameter only where
the language permits that relationship. A body lifetime cast can anchor to a
visible local when the local proves the relationship.

Changing a parameter name can therefore change lifetime binding if another
annotation names it. Treat anchor names as part of the signature contract.

## Allocators And `within`

`within` supplies an allocation context.

```camp
export Buffer* createBuffer(nuint capacity, within allocator);

within (allocator)
{
	Buffer* buffer = new Buffer(capacity);
}
```

Source-level allocation uses the active `within` context or the compiler's
configured implicit/explicit policy.

A `within` parameter is an allocator-context parameter. It is not a normal
defaulted parameter and it is not merely documentation. It controls `new`,
pointer-form `delete`, escaped delegate context allocation, async-frame
allocation where applicable, and generated cleanup that must return storage to
the correct allocator.

The allocator pattern is structural: an allocator value must provide compatible
allocation and free operations. Standard library types can provide helpers, but
the language rule is about the required callable surface.

The active allocation context can be supplied three ways:

```camp
within (arena)
{
	Buffer* fromArena = new Buffer(1024);
}

Buffer* explicitBuffer = within(heap) new Buffer(1024);

export Buffer* createBuffer(nuint capacity, within allocator)
{
	return new Buffer(capacity);
}
```

The statement form creates a lexical allocation context. The expression form
overrides allocation for that expression. The parameter form lets a function
receive or forward a context.

Compiler policy controls whether source-level `new`/`delete` can fall back to a
default allocator when no explicit context exists. Library APIs should still
write `within` when allocation is part of their contract.

## Source-Level Allocation

`new` allocates through the selected context. `delete` destroys and frees using
the matching allocation model.

```camp
Buffer* buffer = within(allocator) new Buffer(1024);
delete buffer;
```

`within` affects source-level allocation and generated allocation that is part
of source constructs. It does not automatically change every native allocation
inside an extern function or target library call. Native functions decide their
own allocation behavior unless the API explicitly accepts and uses a Camp
allocator.

When a value captures its allocator for later cleanup, that allocator must
itself be safe to retain for the same lifetime. This matters for escaped
delegates, postponed call contexts, async frames, and objects that store
allocator references.

Pointer-form `delete` follows the free contract. The compiler checks that the
deleted pointer is compatible with the allocator/free surface. Do not mix
allocators unless the API explicitly permits it.

## Safe Lifetime Casts

Lifetime casts state a fact the compiler cannot infer from syntax alone.

```camp
escaped byte* retained = (escaped byte*)source;
```

Use casts sparingly and only when the program's ownership model really proves
the lifetime relationship.

Lifetime casts are explicit proof boundaries. They are not ordinary value
conversions. A cast such as `(escaped byte*)source` says the programmer knows
the value can safely be retained. If that proof is wrong, the program can store
a pointer past the lifetime of its target.

Use lifetime casts for narrow API boundaries, not as a broad way to silence
errors. If a function repeatedly needs the same cast, the signature probably
needs a more precise lifetime relationship.

## Slots And Values

Camp distinguishes slot lifetime facts from value lifetime facts. A field slot
may be escaped storage even when the value currently read from it is scoped.
Likewise, a value can be derived from an anchor even when stored in a local slot
with a different default.

This distinction explains several diagnostics that otherwise look surprising:

```camp
escaped byte* retained;

export void remember(scoped byte* temporary)
{
	retained = temporary; // invalid: scoped value into escaped storage
}
```

The target slot requires an escaped value. The value fact of `temporary` is
scoped, so the assignment is rejected.

## Aggregate And Container Rule

Aggregates carry the lifetime requirements of their pointer-bearing components.
If a struct contains an escaped field, assigning to that field has escaped
storage requirements even if the containing value is local. If an array,
delegate, optional, iterator, or interface adapter carries pointer-bearing
state, the expanded components are checked.

```camp
export struct RetainedBytes
{
	escaped byte* data;
	nuint length;
}
```

Constructing `RetainedBytes` with a scoped `data` pointer is invalid unless the
source proves the pointer can escape.

## Returns, Yields, And Captures

Return values must satisfy the function's declared lifetime. Yielded values
must satisfy the iterator result lifetime and any generated iterator state.
Captured values in lambdas or postponed calls must satisfy the context lifetime.

```camp
export delegate void() makeCallback(scoped byte* temporary)
{
	// Invalid if the delegate escapes and captures temporary.
	return new delegate () => use(temporary);
}
```

Scoped captures are allowed only when the callable value itself remains scoped.
Escaped callable values require escaped-safe captures.

## Async And Suspension

Async functions can retain locals, parameters, completion callbacks, and cleanup
state across suspension. Values used after an `await` must be safe to store in
the async frame. Stack-like storage and scoped adapter values cannot cross a
suspension unless the compiler can prove the generated state does not retain
them beyond their valid lifetime.

This rule is why some `init` forms and scoped conversions are valid in ordinary
functions but rejected in async bodies.

## Common Pitfalls

- Returning a pointer into a parameter without an explicit anchor.
- Capturing a scoped pointer in an escaped delegate.
- Storing a struct interface adapter in escaped storage.
- Allocating with one context and deleting through another.
- Treating `constof` as a lifetime rule. It controls constness, not storage
  duration.
- Assuming a local variable makes a value safe to escape. The value fact still
  matters.
