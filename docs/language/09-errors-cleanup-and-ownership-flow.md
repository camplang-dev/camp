+++
nav_title = "9. Errors And Cleanup"
+++

# Errors, Cleanup, And Ownership Flow

You have already seen `thrown` slots in function signatures and `finally
delete` in lifecycle examples. This chapter gives those pieces their own
story.

Camp error handling is explicit. A function can return its success value in
the ordinary way while carrying a separate error path beside it. Cleanup is
explicit too: the source says which value is protected and what should happen
when control leaves the protected region.

The syntax uses familiar words like `throw`, `try`, `catch`, and `finally`, but
Camp is not building a runtime exception system around hidden objects and stack
unwinding metadata. These forms model the status-return and cleanup patterns C
libraries already use, while making the pattern visible in the type system.

## Error Values

A Camp error is an ordinary value with a convention:

- the default value means success;
- any non-default value means failure.

Enums are a natural fit:

```camp
enum ParseError
{
	OK = 0,
	EMPTY,
	INVALID_DIGIT
}
```

Put the success value first and assign it `0` when the enum is used as an error
type. That keeps `default` meaningful and keeps the lowered ABI friendly to C
callers that expect zero to mean success.

The common thrown types are small values: integral primitives, integral
newtypes, enums, and sometimes pointers. If an API needs rich diagnostics,
store them somewhere else and throw a small status, handle, or pointer that can
lead the caller to the details.

## `thrown` Slots

A trailing `thrown` parameter says that a call can produce an error value:

```camp
int parsePort(const char[] text, thrown ParseError error)
{
	if (text.length == 0)
		throw ParseError.EMPTY;

	return 80;
}
```

At the source level, the function still reads as "return an `int`." The
`thrown` slot is the side path for failure. Across a C-style ABI, that usually
looks like a success return plus a caller-provided error slot:

```c
int parsePort(const char *text, size_t text_length, ParseError *error);
```

The exact C spelling is target-dependent, but the shape is the important part:
the error value is explicit. There is no hidden exception object.

## `throw`

`throw` writes to the active compatible error destination and exits the current
path:

```camp
int divide(int left, int right, thrown CalcError error)
{
	if (right == 0)
		throw CalcError.DIVIDE_BY_ZERO;

	return left / right;
}
```

Conceptually, that is close to the C pattern:

```c
if (right == 0) {
	*error = CalcError_DIVIDE_BY_ZERO;
	return 0;
}
```

Successful paths write the default error value. You normally do not spell that
yourself; the language supplies the success behavior around the source
construct.

## Propagation

If the caller also has a compatible `thrown` slot, an uncaught error can
propagate:

```camp
int readConfiguredPort(Config* config, thrown ParseError error)
{
	return parsePort(config.portText);
}
```

`readConfiguredPort` does not need to catch and rethrow manually. Its signature
already says it can report the same error kind. That keeps the common path
short while preserving the error in the function type.

If the caller has no compatible `thrown` slot, the call must catch the error.

## Catch Arguments

A call can catch the error value directly:

```camp
ParseError error = default;
int port = parsePort(text, catch error);

if (error != default)
	port = 80;
```

The catch target is an ordinary local. You can also declare it at the call
site:

```camp
int port = parsePort(text, catch ParseError parseError);
if (parseError != default)
	port = 80;
```

Use catch arguments when the call itself is the right place to handle or record
the error. Use propagation when the caller's caller should decide what to do.

## `try` And `catch`

Use `try` / `catch` when a block of work shares one error-handling path:

```camp
int portOrDefault(const char[] text)
{
	try
	{
		return parsePort(text);
	}
	catch (ParseError parseError)
	{
		return 80;
	}
}
```

The catch variable is an ordinary local scoped to the catch body. It can be
inspected, translated to another error type, logged, or used to choose a
fallback.

Multiple calls inside the same try block can flow to the same catch:

```camp
int loadPort(Config* config)
{
	try
	{
		int base = parsePort(config.portText);
		int offset = parsePort(config.offsetText);
		return base + offset;
	}
	catch (ParseError parseError)
	{
		return 80;
	}
}
```

## `finally` Blocks

A `finally` block runs when ordinary structured control leaves the associated
`try`: normal completion, `return`, `break` or `continue` where those are
valid, a caught error, or a propagated error.

```camp
int useScratch(thrown ParseError error)
{
	prepareScratch();
	try
	{
		return parsePort(scratchText());
	}
	finally
	{
		releaseScratch();
	}
}
```

Use a block when cleanup needs more than a single expression or when the
protected region spans several statements.

There is one low-level exception worth remembering: `goto` is allowed to jump
out of blocks without running skipped `finally` work. Treat `goto` the way you
would in careful C code, and do not use it to leave a protected cleanup region.

## Scope Cleanup

A bare `finally` statement registers cleanup for the current scope:

```camp
int parseWithScratch(const char[] text, thrown ParseError error)
{
	prepareScratch();
	finally releaseScratch();

	return parsePort(text);
}
```

This is useful when a function acquires something near the top and every exit
from the scope should release it. The cleanup stays close to acquisition, so a
reader does not have to search for every return path.

The same `goto` warning applies here. Structured exits run registered cleanup;
a low-level jump can bypass cleanup for the blocks it skips.

## Expression Cleanup

An expression can register cleanup for the value it produces:

```camp
auto handle = openHandle(path, catch OpenError openError) finally delete;
```

`finally delete` means the value should be deleted when the surrounding cleanup
scope exits. You have already seen this form with heap objects; it is also a
good fit for temporary buffers and owned values returned by helper functions.

Cleanup can also call a method:

```camp
NativeHandle handle = openNativeHandle(path) finally close();
```

The cleanup method runs on the produced value. Use this shape for native
handles or value newtypes that are not deleted with `delete`.

Cleanup arguments are evaluated when cleanup runs:

```camp
CleanupProbe probe = default;
int amount = 2;
auto guarded = probe finally add(amount);
amount = 5;
```

In this example, cleanup observes the later value of `amount`. Prefer simple,
stable cleanup arguments when possible; they are easier to audit.

## `thrown(E)` Return Form

Most Camp functions use a normal result plus a trailing `thrown` slot. Camp also
has a status-return form:

```camp
thrown(ParseError) tryParsePort(const char[] text, out int value)
{
	if (text.length == 0)
		throw ParseError.EMPTY;

	value = 80;
}
```

This form is useful when the ABI should look like a C function that returns a
status code and writes successful results through `out` parameters:

```c
ParseError tryParsePort(const char *text, size_t text_length, int *value);
```

At the source level, treat it as an error-producing call. A `try` / `catch`
block can handle the status:

```camp
int value = 0;
try
{
	tryParsePort(text, out value);
}
catch (ParseError parseError)
{
	value = 80;
}
```

Use this form when the status-return ABI is the point. For ordinary Camp APIs,
a normal success result plus a trailing `thrown` slot is usually easier to read.

## Choosing An Error Shape

Choose the shape that matches the API:

| API Need | Prefer |
|---|---|
| Primary success value with occasional failure | Return the success value and add a trailing `thrown` slot. |
| C-style status return with several success outputs | Use `thrown(E)` plus `out` parameters. |
| Absence that is not really an error | Use an optional value. |
| Extra success data | Use `out` or a result struct. |
| A small closed set of failures | Use an enum error type. |
| Native library compatibility | Match the native status convention, then wrap it if Camp callers need a cleaner shape. |

Do not use `thrown` just because a function has two results. Use it when the
second path is failure flow and callers should make an explicit decision about
catching or propagating it.

## Ownership Patterns

Cleanup should be registered where ownership becomes clear:

```camp
auto buffer = new Buffer(4096) finally delete;
```

```camp
NativeHandle handle = openNativeHandle(path) finally close();
```

```camp
try
{
	process();
}
finally
{
	releaseScratch();
}
```

The theme is the same as lifetimes: make the important movement visible. If a
function produces an owned value, the caller should be able to see how that
value is protected. If a function can fail, the signature should say so. If a
resource must be released, the cleanup should live near the code that acquired
it.
