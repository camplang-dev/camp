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

A function signature can carry much more than parameter types. It can include
visibility, target call specifiers, lifetime annotations, `constof(anchor)`
relationships, `out` result slots, `thrown` error slots, `within` allocation
contexts, generic parameters, and generic capability parameters. For exported
functions, this source signature is part of the API and ABI contract after the
compiler expands special forms.

Use the ordinary return type for the primary result. Use a named result struct
or `out` parameters for additional success values. Use `thrown` parameters for
error values that participate in call, callback, iterator, or async contracts.

## Parameters And Arguments

Parameters have a type and optional name. Parameter modifiers include `in`,
`out`, `thrown`, `overload`, and `within`.

```camp
export bool tryReadByte(Stream* stream, out byte value, thrown IoError);
```

Arguments may be positional or named. `out` arguments bind result positions.
`catch` arguments handle thrown slots at call sites.

Special parameter forms are part of the callable shape:

| Parameter form | Meaning |
|---|---|
| `in T value` | Input transport, especially for erased generic values. |
| `out T value` | Caller-provided output slot. |
| `thrown T error` | Error propagation slot. |
| `within allocator` | Allocation context parameter. |
| `sizeof(T)` | Generic size capability. |
| `typenameof(T)` | Generic runtime-name capability. |
| `vtableof(T: Interface)` | Generic interface vtable capability. |

Parameter order matters. Expanded parameters such as arrays, delegates,
`thrown`, and capability parameters can lower to multiple ABI components, but
the source order remains the contract users write and read.

## Default Arguments

Parameters may have default values. A caller may omit trailing defaulted
arguments or use named arguments to skip over defaulted positions.

```camp
export void writeLine(const char[] text, bool flush = true);

writeLine("saved");
writeLine("queued", flush: false);
```

Default expressions are part of the declaration surface. Exported APIs should
avoid defaults that depend on private implementation details. Defaults also
matter when taking callable references: the compiler may need a thunk that
supplies omitted arguments for a particular callable shape.

## Named Arguments

Named arguments use `name: value` and bind to the matching parameter. They make
call sites clearer when several optional arguments have the same type.

```camp
copyBytes(source, destination, count: 128);
```

Named arguments do not make arbitrary reordering safe if required positional
parameters remain unbound. They clarify binding to a parameter in a candidate
signature.

## Trailing `out` Result Binding

Functions with trailing `out` parameters can be called in ways that bind or
omit those result slots.

```camp
parseCount(text, out count, catch error);
```

Omitted `out` values are not ordinary runtime values. They are call-site
binding conveniences. If the value is needed later, bind it to a local or use a
result struct.

## Receiver Parameters

A parameter named `this` is a receiver. Receiver functions can be called using
member-call syntax.

```camp
export nuint length(const Buffer* this);

nuint size = buffer.length();
```

Receiver qualifiers affect mutation, lifetime, dispatch, and callable
references.

```camp
export void clear(Buffer* this);
export nuint length(const Buffer* this);
export constof(this) byte* data(const Buffer* this);
```

The receiver participates in overload resolution, `constof(this)`, virtual
dispatch, interface implementation, and method reference binding. A method
reference that captures a receiver may allocate callable context if it escapes.

In valid method positions, `this` can also be a receiver-preserving return type.
That form preserves the dynamic receiver type for fluent APIs:

```camp
export this reset(this)
{
	this.clear();
	return this;
}
```

`this` as a return type is not a general type constructor. It is a receiver
relative result form with specific declaration and override rules.

## Callable Types

`fn` describes a direct function value. `delegate` describes a callable value
with context. `once` describes a callable that is called exactly once.

```camp
fn int(int value) transform;
delegate void(const char[] message) logger;
once void(escaped this) completion;
```

Concrete callable types may include target call specifiers.

Callable kinds differ in representation and ownership expectations:

| Callable kind | Source meaning |
|---|---|
| `fn` | Direct function value with no captured context. |
| `delegate` | Callable value with target plus context. |
| `once` | Callable guaranteed to be invoked exactly once. |
| `iter` | Iterator callable/protocol shape. |
| `async` | Callback-shaped async callable. |

The return type, parameters, `out` slots, `thrown` slots, lifetimes, call spec,
and generic capabilities are part of the callable type. Signature compatibility
does not insert hidden conversions into parameter types. If a callback needs a
different signature, write an adapter.

## Delegates, `once`, And Function Pointers

Delegates can represent function references, method references, and lambdas
with captured context. `once` callables are used when ownership or control flow
guarantees a single invocation. Raw `fn*` values erase a function signature and
must be recovered before calls.

A direct `fn` value is suitable for free functions and static functions. A
`delegate` value is suitable for bound methods, lambdas, and any callable that
needs context. A `once` value is suitable for completion callbacks, postponed
calls, and ownership-transfer patterns where exactly one invocation is part of
the contract.

`once` does not by itself mean "delete the context." Context deletion belongs
to the producer that owns the context. Escaped once lambdas and `postpone`
produce generated context that can delete itself after invocation. A once value
received from another API is just a once callable unless its producer's
contract says more.

Raw `fn*` is not callable. It is a fence carrier for function pointers:

```camp
fn int(int) transform = getTransform();
fn* rawTransform = (fn*)transform;
fn int(int) recovered = (fn int(int))rawTransform;
```

Use `fn*` only at native ABI boundaries where erasing and restoring a signature
is intentional.

## Callable Newtypes

A `newtype` can wrap a callable shape and give it a nominal API name.

```camp
export newtype delegate void LineWriter(const char[] line);
export newtype once void SaveCompletion(thrown IoError);
```

Callable newtypes are nominal. Two callable newtypes with the same underlying
shape are not the same API type unless the language defines a conversion. This
is useful because callback roles often matter even when signatures match.

Callable newtypes can carry lifetime and call-spec rules through a named API
surface. They also make metadata clearer for documentation tools and LLMs.

## Lambdas

Lambdas use `=>` and are usually target-typed by a callable parameter or local.

```camp
delegate int(int value) doubleValue = value => value * 2;
```

If the lambda is not target-typed, inference remains local and explicit. Camp
does not infer a global function type from arbitrary later uses.

Captures are determined by use inside the lambda body. Camp has no separate
capture-mode syntax. A scoped lambda can capture scoped values because the
callable value does not outlive them. An escaped lambda can capture only values
that are safe to store in its generated context.

```camp
export delegate bool(int value) makeThresholdFilter(escaped int* threshold)
{
	return new delegate (int value) => value >= *threshold;
}
```

Escaped captures are checked carefully because the generated context may be
called later. If a lambda captures expanded values, the generated context stores
the relevant components. If it captures an allocator for cleanup, that allocator
must itself be safe to retain.

## Method References

A method reference can become a callable value. If the receiver is already
known, the callable value binds that receiver as context. If the receiver is not
known, the callable may expect a receiver parameter.

Method references preserve receiver constness, lifetime facts, virtual
dispatch, and interface dispatch. When a method reference crosses an API
boundary, prefer a named callable newtype so the callback role and lifetime
contract are clear.

## Overloads And Callable Ascription

Camp supports overload selection by callable shape, receiver, parameter types,
and overload markers. When the compiler cannot infer the intended callable from
context, use ascription or an explicitly typed local.

```camp
fn int(int) selected = Math::abs;
```

Callable ascription is also useful when a function has default arguments or
when a method reference could bind several overloads.

## Async And Iterator Callable Surfaces

Async callables and iterator callables build on the same callable-shape rules.
Async completion callbacks are `once`-shaped. Iterator values expose protocol
callables. `thrown` slots and `out` slots participate in these shapes.

When designing a callback-heavy API, decide whether the value is:

- direct and context-free (`fn`);
- context-carrying (`delegate`);
- single-use (`once`);
- async completion-shaped (`async`/`once`);
- iterator protocol-shaped (`iter`).

Choosing the right callable kind makes ownership and lowering much easier for
both the compiler and users.
