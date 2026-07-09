# Outstanding Bugs

Next bug number: BUG-041.

## BUG-040: LSP tests can leave `camp-lsp` running and hang local full `dotnet vstest`

On macOS, the full `dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll`
run can become silent near the end of the suite with only `vstest.console`,
`testhost`, and a stale `/Users/andrew/Projects/camplang/bin/camp-lsp` process
remaining. Killing the stale `camp-lsp` process does not always let the already
hung testhost recover, so the full run must be restarted. Targeted LSP tests and
targeted command-line tests pass independently.

The LSP test harness should guarantee that every `camp-lsp` child is terminated
and waited on, even if a test fails or times out. Once fixed, a local full
`dotnet vstest` run should exit cleanly without manual `pkill`.

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
