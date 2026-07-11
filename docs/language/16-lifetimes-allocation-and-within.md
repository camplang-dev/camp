# Lifetimes, Allocation, And `within`

## Lifetime Annotations

Camp uses lifetime annotations to describe whether references and pointer-like
values may escape, are scoped to the current call, or are known to outlive an
anchor.

Common annotations are `escaped`, `scoped`, and `unscoped(anchor)`.

```camp
export escaped byte* retain(escaped byte* data);
```

## Default Lifetimes

Parameters receive default lifetime facts when no explicit annotation is
written. Defaults depend on the declaration context, receiver kind, and source
surface. Use explicit annotations when the API contract matters to callers.

## `escaped`, `scoped`, And `unscoped`

`escaped` means a value may be retained beyond the immediate scope. `scoped`
means a value is limited to the current scope or call. `unscoped(anchor)` states
that a value is proven to outlive a named anchor.

```camp
export void store(escaped byte* data);
export void inspect(scoped byte* data);
export byte* slice(unscoped(source) byte* source, nuint offset);
```

## Lifetime Anchors

Anchors are parameter, receiver, or local names visible where a lifetime
annotation or cast refers to them. The compiler reports unresolved anchors.

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

## Source-Level Allocation

`new` allocates through the selected context. `delete` destroys and frees using
the matching allocation model.

```camp
Buffer* buffer = within(allocator) new Buffer(1024);
delete buffer;
```

## Safe Lifetime Casts

Lifetime casts state a fact the compiler cannot infer from syntax alone.

```camp
escaped byte* retained = (escaped byte*)source;
```

Use casts sparingly and only when the program's ownership model really proves
the lifetime relationship.
