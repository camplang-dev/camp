# Outstanding Bugs

Next bug number: BUG-040.

## BUG-039: C emission loses unresolved extern call diagnostics in loop comparison conditions

When a Win32-style extern call is used directly in a comparison inside a loop
condition, C emission can abort with only `CallExpression at line,column has
unresolved type '#ERROR'` and no file/range or source-level diagnostic. This was
seen with:

```camp
while (GetMessageW(&message, (HWND)0, 0, 0) != 0)
{
}
```

The equivalent explicit form compiles:

```camp
while (true)
{
	BOOL hasMessage = GetMessageW(&message, (HWND)0, 0, 0);
	if (hasMessage == 0)
		break;
}
```

The analyzer or emitter should either lower the compact condition correctly or
report a proper source-ranged diagnostic before C emission.

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
