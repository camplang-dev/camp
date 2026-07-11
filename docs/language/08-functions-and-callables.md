# Functions And Callables

## Function Declarations

Functions declare a return type, name, parameter list, and optional body.

```camp
export int add(int left, int right)
{
	return left + right;
}
```

`extern` functions declare callable symbols implemented outside Camp.

## Parameters And Arguments

Parameters have a type and optional name. Parameter modifiers include `in`,
`out`, `thrown`, and `overload`. `within` parameters participate in allocation
context passing and are covered with lifetimes.

```camp
export bool tryReadByte(Stream* stream, out byte value, thrown IoError);
```

Arguments may be positional or named. `out` arguments bind result positions.
`catch` arguments handle thrown slots at call sites.

## Default Arguments

Parameters may have default values. A caller may omit trailing defaulted
arguments or use named arguments to skip over defaulted positions.

```camp
export void writeLine(const char[] text, bool flush = true);

writeLine("saved");
writeLine("queued", flush: false);
```

## Named Arguments

Named arguments use `name: value` and bind to the matching parameter. They make
call sites clearer when several optional arguments have the same type.

## Receiver Parameters

A parameter named `this` is a receiver. Receiver functions can be called using
member-call syntax.

```camp
export nuint length(const Buffer* this);

nuint size = buffer.length();
```

Receiver qualifiers affect mutation, lifetime, and dispatch.

## Callable Types

`fn` describes a direct function value. `delegate` describes a callable value
with context. `once` describes a callable that is called exactly once.

```camp
fn int(int value) transform;
delegate void(const char[] message) logger;
once void(escaped this) completion;
```

Concrete callable types may include target call specifiers.

## Delegates, `once`, And Function Pointers

Delegates can represent function references, method references, and lambdas with
captured context. `once` callables are used when ownership or control flow
guarantees a single invocation. Raw `fn*` values erase a function signature and
must be recovered before calls.

## Callable Newtypes

A `newtype` can wrap a callable shape and give it a nominal API name.

```camp
export newtype delegate void LineWriter(const char[] line);
```

Callable newtypes are useful for callbacks that would otherwise have repeated
anonymous delegate types.

## Lambdas

Lambdas use `=>` and are usually target-typed by a callable parameter or local.

```camp
delegate int(int value) doubleValue = value => value * 2;
```

Escaped delegates and once callables may allocate capture context. Source code
should choose lifetime and allocation context deliberately when a lambda escapes
its creating scope.
