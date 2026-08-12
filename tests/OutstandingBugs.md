# Outstanding Bugs

Next bug number: BUG-073.

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
