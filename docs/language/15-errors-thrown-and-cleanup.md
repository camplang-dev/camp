# Errors, Thrown, And Cleanup

Camp uses explicit error values rather than hidden exception objects. A
function that can fail exposes that fact in its signature with a `thrown` slot.
The caller then either catches the value, forwards it through a compatible
`thrown` slot, or handles it in a `try` / `catch` statement.

Cleanup is likewise explicit. `finally` statements, `try` / `finally`, and
expression-level cleanup make resource lifetime visible at the source level.

## Error Values

A Camp error is an ordinary value with a convention:

- the default value means success;
- a non-default value means failure.

Common error types are enums:

```camp
export enum IoError
{
	OK = 0,
	E_NOT_FOUND,
	E_ACCESS_DENIED,
	E_INTERRUPTED
}
```

Integral primitives, integral value newtypes, enums, and pointers can be used
as thrown types. Pointer thrown values are treated as escaped pointer results.

Keep error values small and easy to compare. Rich diagnostic information can be
stored elsewhere and referenced by handle or pointer when an API needs it.

## `thrown` Parameters

A trailing `thrown` parameter models an error slot in the callable signature.

```camp
export int parsePort(const char[] text, thrown ParseError error)
{
	if (text.length == 0)
		throw ParseError.E_EMPTY;

	...
}
```

The name may be omitted:

```camp
export int parsePort(const char[] text, thrown ParseError)
{
	...
}
```

When omitted, the implicit parameter name is `error`. That implicit name is
used for ABI spelling and named-argument resolution, so another parameter named
`error` in the same signature is invalid.

A `thrown` parameter is not an `out` parameter. It does not participate in
trailing `out` deconstruction.

## `thrown(E)` Return Form

Camp also supports a thrown-return spelling:

```camp
thrown(ParseError) tryParsePort(const char[] text, out int value)
{
	if (text.length == 0)
		throw ParseError.E_EMPTY;

	value = parseDigits(text);
}
```

This places the error in the ordinary ABI return slot. At the source call site,
`catch` still appears in the argument list:

```camp
auto value = tryParsePort(text, catch ParseError error);
```

Omitted trailing `out` values still follow the ordinary result-binding rule.
The thrown result is handled separately by `catch` or propagation.

## `throw`

`throw` produces an error value for the active compatible thrown destination.

```camp
int divide(int left, int right, thrown CalcError error)
{
	if (right == 0)
		throw CalcError.E_DIVIDE_BY_ZERO;

	return left / right;
}
```

The surrounding function, generator, async completion, or try/catch context
must have a compatible destination. If no such destination exists, the compiler
reports that the thrown value must be caught or rethrown by a compatible
thrown result.

Throwing exits the current path. Cleanup registered for scopes being exited
runs according to the cleanup rules.

## Propagation

If a call produces a thrown value and the caller has a compatible thrown slot,
the value can propagate automatically.

```camp
int loadPort(Config* config, thrown ParseError error)
{
	return parsePort(config.PortText);
}
```

The propagated value is still an ordinary error value. There is no hidden
exception object or stack unwinding metadata requirement in the language model.

If the caller has no compatible thrown slot, the call must provide a `catch`
argument or appear inside a `try` with a compatible `catch`.

## Catch Arguments

A call can catch a thrown result directly in its argument list.

```camp
ParseError error;
int value = parsePort(text, catch error);

if (error != ParseError.OK)
	value = 80;
```

`catch auto name` asks the compiler to infer the error variable type:

```camp
int value = parsePort(text, catch auto parseError);
```

`catch _` intentionally discards the thrown value:

```camp
parsePort(text, catch _);
```

Discarding an error should be rare and deliberate. If the error can affect
program correctness, bind it and handle it.

## `try` And `catch`

`try` protects a block. `catch` handles compatible thrown values from inside
that block.

```camp
int loadPortOrDefault(Config* config)
{
	try
	{
		return parsePort(config.PortText);
	}
	catch (ParseError error)
	{
		return 80;
	}
}
```

The catch variable is an ordinary local scoped to the catch body. It can be
inspected, converted, logged, returned from, or rethrown through another
compatible thrown slot.

Multiple catch clauses should be ordered from specific to broad when the error
types make that distinction meaningful. If an error type cannot be caught by
any visible catch and cannot propagate, the compiler reports the unhandled
thrown value.

## `finally` Blocks

A `finally` block runs cleanup when control leaves the associated `try`.

```camp
try
{
	readFile(path, out data);
}
catch (IoError error)
{
	report(error);
}
finally
{
	delete data.elements;
}
```

Use `try` / `finally` when cleanup needs several statements or when the
protected operation spans a block.

## `finally` Cleanup Statements

A bare `finally` statement registers cleanup for the current scope.

```camp
void runChecked(thrown CalcError error)
{
	finally releaseTemporaryState();

	stepOne(catch error);
	stepTwo(catch error);
}
```

The cleanup runs when control leaves the scope, including ordinary return,
thrown propagation, and loop exits that leave the scope.

This form is useful when a function establishes a resource at the beginning and
all exits should perform the same cleanup.

## Expression-Level Cleanup

An expression can register cleanup with `finally delete` or
`finally methodName(arguments...)`.

```camp
Buffer* buffer = new Buffer(1024) finally delete;
FileHandle handle = FileHandle.open(path) finally close();
```

For `finally methodName(...)`, the cleanup method is invoked on the produced
value when the surrounding cleanup scope exits:

```camp
FileHandle handle = FileHandle.open(path) finally close();
```

Conceptually, the value is stored in a local and the cleanup call is registered
against that local. Cleanup arguments are evaluated according to the expression
rules and the cleanup method must return `void`.

```camp
CleanupProbe probe = default;
auto guarded = probe finally add(amount);
```

If `amount` changes before scope exit, the cleanup call observes the value
according to normal argument evaluation and lowering rules for the registered
cleanup call. Prefer immutable cleanup arguments when possible.

## `finally delete`

`finally delete` registers deletion of the produced value:

```camp
auto bytes = takeBytes(count) finally delete;
consume(bytes);
```

Pointer deletion may require an explicit `within` context so the compiler knows
which allocator/free path owns the storage:

```camp
byte* data = within (allocator) new byte[64] finally delete;
```

`finally delete` does not turn a value-newtype into a resource object. If a
newtype wraps a native handle, expose and use a cleanup method:

```camp
FileHandle handle = FileHandle.open(path) finally close();
```

## Cleanup And Ownership

Cleanup should be registered at the point where ownership becomes clear.

Good patterns:

```camp
Buffer* buffer = new Buffer(1024) finally delete;
```

```camp
FileHandle file = FileHandle.open(path) finally close();
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

Avoid cleanup that depends on a distant comment or an implicit convention.
Camp's source model is designed so the reader can see when an allocated value,
handle, delegate context, iterator state, or async frame is cleaned up.

## Thrown Flow Through Iterators

Iterator types may include one trailing thrown slot:

```camp
struct iter(int value, thrown IterError error) numbers(int failAt)
{
	yield 1;
	if (failAt == 2)
		throw IterError.FAILED;
	yield 3;
}
```

A `foreach` over a failing iterator participates in ordinary thrown flow:

```camp
int sumUntilFailure(thrown IterError error)
{
	int total = 0;
	foreach (auto value in numbers(2))
		total += value;
	return total;
}
```

If the iterator throws and the enclosing function has a compatible thrown slot,
the error propagates. Otherwise the loop must be inside a compatible
`try` / `catch`.

## Thrown Flow Through Async

Async functions carry thrown values through their completion callback.

```camp
async int loadCountAsync(const char[] path, thrown IoError error);

async int useCount(const char[] path, thrown IoError error)
{
	return await loadCountAsync(path, catch error);
}
```

Awaited calls use ordinary `catch` syntax. A thrown value that is not caught can
propagate through the async function's own thrown completion slot when the
types are compatible.

## ABI Shape

A `thrown` slot is part of the callable's ABI shape. For an ordinary trailing
`thrown E` parameter, callers provide storage for the error value. For
`thrown(E)` return form, the error value uses the ordinary return slot and any
success value travels through `out` slots.

The default value means success at the source level and in lowered code. For an
enum error type, that is why the success member should have value `0`.

## Choosing Error Shapes

Use `thrown E` when:

- the function's primary return value is a successful result;
- the error naturally fits a trailing status slot;
- the function also uses ordinary return type syntax.

Use `thrown(E)` return form when:

- the ABI should put the error in the ordinary return slot;
- successful result values are naturally `out` parameters;
- the API is modeled after C-style status-return functions.

Use an ordinary `out` result, not `thrown`, when the value is successful output
rather than failure status.

## Diagnostics To Expect

The compiler reports errors when:

- a thrown value has no compatible catch or propagation destination;
- a `catch` argument type is incompatible with the thrown type;
- a function has an invalid thrown type;
- a thrown parameter conflicts with the implicit name `error`;
- `throw` appears where no compatible thrown surface exists;
- cleanup method registration targets a method that does not return `void`;
- pointer deletion lacks required allocator context.
