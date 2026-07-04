# Outstanding Bugs

Next bug number: BUG-037.

## BUG-036: Escaped lambda capture references inside std timer callbacks are not rewritten in generated C

An escaped lambda that captures a final callback-shaped callable parameter can
compile to C that references the source parameter names directly instead of the
generated lambda context fields. This appeared while implementing
`Std.sleepAsync` with:

```camp
void sleepAsync(nuint timeoutMs, escaped once void() complete)
{
	startTimer(timeoutMs, h => {
		stopTimer(h);
		complete();
	});
}
```

The generated lambda body attempted to call `complete(complete_context)` even
though those names were not in scope inside the emitted lambda function. The
standard library currently works around this with an explicit state object and
callback function. Fix lambda lowering so captured expanded callable components
are consistently rewritten to their generated context fields.
