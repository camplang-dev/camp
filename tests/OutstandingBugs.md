# Outstanding Bugs

Next bug number: BUG-067.

## BUG-063: `prep` expression used as an interpolation hole reaches C emission unlowered

### Summary

A `prep` prefix expression can produce a `char[]` value, and `char[]` values are
valid interpolation hole values. However, when a `prep` expression is written
directly inside an interpolation hole, C emission receives the internal
`PreparedBufferExpression` node instead of a lowered value expression.

This is a lowering coverage bug. A `prep` expression should behave like any
other source expression after analysis has established its result type and
lifetime.

### General Repro

```camp
using Std;

export void main()
{
	Console.writeLine($"{prep writeText()}");
}

nuint writeText(prep char[] buffer = default)
{
	if (buffer.length > 0)
		buffer[0] = 'x';
	return 1;
}
```

Run:

```bash
bin/campc run repro.camp
```

### Current Behavior

Reproduced on macOS/clang. Compilation fails before native compilation:

```text
C emission does not yet support expression node PreparedBufferExpression.
```

### Expected Behavior

The compiler should lower the `prep` expression before it is consumed by
interpolation lowering, or interpolation lowering should materialize the
prepared result explicitly. The program should print:

```text
x
```

## BUG-064: `prep` prefix does not recognize callable values with `prep` parameters

### Summary

Callable types can declare `prep` parameters and ordinary calls through callable
values can pass the prepared buffer explicitly. A `prep` prefix call through a
callable value is currently rejected even when the callable type carries the
required `prep` parameter.

This leaves a mismatch between callable signature compatibility and use-site
prepared-call syntax.

### General Repro

```camp
using Std;

newtype fn nuint Writer(prep char[] buffer = default);

export void main()
{
	Writer writer = writeText;
	char[] text = prep writer();
	Console.writeLine(text);
}

nuint writeText(prep char[] buffer = default)
{
	if (buffer.length > 0)
		buffer[0] = 'x';
	return 1;
}
```

Run:

```bash
bin/campc run repro.camp
```

### Current Behavior

Reproduced on macOS. Analysis rejects the `prep` prefix:

```text
error: prep requires a call or property getter target with a prep parameter.
```

Additional overload-selection errors can follow because the failed `prep`
expression has no usable independent type.

### Expected Behavior

If the callable value's static callable shape contains exactly one valid
`prep` parameter, `prep writer()` should be accepted and lowered using the same
two-call size/write protocol as a direct function call. The sample should print:

```text
x
```

If the language intentionally does not allow `prep` through callable values, the
semantics documentation should say so explicitly and the diagnostic should point
to that rule.

## BUG-065: Source filenames beginning with digits emit invalid C header guards

### Summary

When a Camp source file basename begins with a digit, the C emitter uses that
basename directly to form generated header guard macros. C macro names cannot
begin with a digit, so native compilation fails even for an otherwise empty
program.

This is a general C-emitter identifier sanitization bug.

### General Repro

Create a file named `01_repro.camp`:

```camp
export void main()
{
}
```

Run:

```bash
bin/campc run 01_repro.camp
```

### Current Behavior

Reproduced on macOS/clang. Native compilation fails:

```text
error: macro name must be an identifier
#ifndef 01_REPRO_PRIVATE_H_
        ^
```

### Expected Behavior

Generated C identifiers and macro names derived from source filenames should be
sanitized into valid C identifiers. For example, a leading underscore or other
stable prefix could be added when the first derived character is not valid as a
C identifier start.

## BUG-066: Interpolation with a `prep` formatter that has defaulted ordinary arguments emits invalid C

### Summary

Interpolation can select a caller-prepared `format` method for a hole value.
When the selected `format` method has an ordinary parameter with a default value
before the `prep` buffer parameter, interpolation lowering emits invalid C for
the defaulted argument position.

This appears to be a prepared-formatter interpolation lowering bug around
default argument substitution and expanded default values.

### General Repro

```camp
using Std;

export void main()
{
	Badge badge = default;
	Console.writeLine($"{badge}");
}

enum Style
{
	PLAIN = 0
}

struct Badge
{
}

nuint format(in Badge this, Style style = Style.PLAIN, prep char[] buffer = default)
{
	if (buffer.length > 0)
		buffer[0] = 'x';
	return 1;
}
```

Run:

```bash
bin/campc run repro.camp
```

### Current Behavior

Reproduced on macOS/clang. Native compilation fails because generated C contains
an initializer-list expression where a scalar enum argument is expected:

```c
uintptr_t _interpolatedPartSize2 =
	Badge_format(&_interpolatedValue0, { NULL, 0 }, 0, 0);
```

clang reports:

```text
error: expected expression
```

### Expected Behavior

Interpolation lowering should substitute the ordinary default argument exactly
as an ordinary source call would, then supply the generated `prep` buffer for
the sizing and writing passes. The sample should print:

```text
x
```
