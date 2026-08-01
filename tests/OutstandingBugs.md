# Outstanding Bugs

Next bug number: BUG-067.

## BUG-063: Bare `prep` prefix in an interpolation hole reaches C emission unlowered

### Summary

Interpolation holes are already a caller-prepared formatting context. A bare
hole whose expression is a call to a method with a `prep char[]` result buffer
should not need an explicit `prep` prefix. For example, `$"{writeText()}"`
should be enough when `writeText` provides the hole's text through its `prep`
parameter.

When the user writes a bare hole as `$"{prep writeText()}"`, the compiler
currently allows analysis to continue and C emission receives the internal
`PreparedBufferExpression` node. This should instead be diagnosed as a
redundant `prep` prefix in a bare interpolation hole, with a friendly message
that suggests removing `prep`.

Parenthesized or nested `prep` expressions are different. In those cases the
`prep` expression first produces a real array value, and the surrounding
expression then uses that value. For example, `$"{(prep copyText())[2..3]}"` is
a legitimate way to prepare text and interpolate a slice of it.

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

Reproduced on macOS. Compilation fails before native compilation:

```text
C emission does not yet support expression node PreparedBufferExpression.
```

### Expected Behavior

The compiler should diagnose a bare `prep` prefix in an interpolation hole
before lowering reaches C emission. The diagnostic should point at the `prep`
keyword and explain that interpolation holes already use `prep` methods
implicitly when the method provides the hole result. The user should write:

```camp
Console.writeLine($"{writeText()}");
```

Explicit `prep` remains valid when it is parenthesized or nested inside a larger
hole expression and the larger expression consumes the prepared value.

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
