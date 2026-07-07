# Outstanding Bugs

Next bug number: BUG-039.

## BUG-038: Explicit `within` arguments can be misordered with `sizeof` and expanded returns

A call to a helper whose signature combines an expanded return value, an explicit
`within` parameter, and a `sizeof(T)` capability can lower the hidden arguments
in the wrong order. For example, a call shaped like:

```camp
return Array_copyArray<T>(slice, within allocator, sizeof(T));
```

was emitted with the explicit allocator slot as `NULL` and the `sizeof(T)` value
in the allocator position. Wrapping the call in `within(allocator)` also produced
an extra hidden allocator argument. This should be fixed in call lowering so the
source-level `within` argument and hidden expanded-return arguments are ordered
according to the lowered ABI signature.
