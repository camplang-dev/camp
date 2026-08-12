# Outstanding Bugs

Next bug number: BUG-073.

## BUG-071: Inline `out const char[]` declaration emits invalid C

Status: open

Generalized behavior:

An inline `out const char[]` declaration at a call site binds, but C emission
does not pass all lowered components of the expanded array out parameter. The
generated call passes only the elements pointer output and omits the length
output. The emitted use of the inline local also treats it like a pointer rather
than an expanded array value.

Minimal repro:

```camp
bool tryGet(out const char[] value)
{
	value = "hello";
	return true;
}

export int main()
{
	if (tryGet(out const char[] value))
		return value.length == 5 ? 0 : 1;

	return 2;
}
```

Repro instructions:

```text
campc run dev/tmp/argparser-smoke/bug_out_const_char_array.camp
```

Observed failure:

```text
error: too few arguments to function call, expected 2, have 1
error: member reference base type 'const char *' is not a structure or union
```

Boundary check:

The same out parameter works when the array local is declared before the call:

```camp
const char[] value = default;
if (tryGet(out value))
	...
```

The predeclared-local smoke test passes:

```text
campc run dev/tmp/argparser-smoke/bug_out_const_char_array_predeclared.camp
```

## BUG-072: Inline `out` declarations inside lambda bodies can be captured before declaration

Status: open

Generalized behavior:

An inline `out` declaration inside a lambda body can cause invalid C emission.
The lambda lowering emits capture initialization for the inline `out` local
before that local exists in the generated C scope.

Minimal repro:

```camp
struct Step
{
	bool match(out bool value)
	{
		value = true;
		return true;
	}
}

void run(delegate void(Step* step) body)
{
	Step step = default;
	body(&step);
}

export int main()
{
	bool observed = false;

	run(step => {
		if (step.match(out bool value))
			observed = value;
	});

	return observed ? 0 : 1;
}
```

Repro instructions:

```text
campc run dev/tmp/argparser-smoke/bug_inline_out_in_lambda.camp
```

Observed failure:

```text
error: use of undeclared identifier 'value'
```

Boundary check:

The same call works when the out local is declared before the call inside the
lambda:

```camp
run(step => {
	bool value = false;
	if (step.match(out value))
		observed = value;
});
```

The predeclared-local smoke test passes:

```text
campc run dev/tmp/argparser-smoke/ok_predeclared_out_in_lambda.camp
```
