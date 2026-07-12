# Async, Await, And Postpone

## Async Callable Shape

Camp async is structurally callback-shaped. An `async` function is represented
as a callable that completes by invoking a final `once` completion callback.
The source `async` modifier gives callers the `await` surface when the
completion callback has an awaitable shape.

```camp
export async const char[] loadText(const char[] path, thrown IoError error);
```

The completion callback is part of the callable shape after lowering, not an
ordinary source parameter written by callers of `loadText`. It is not a hidden
task object and it does not require a global task runtime. Async code is
lowered into ordinary callback and state-machine machinery.

`async` may appear on functions, methods, compatible property accessors, and
callable type surfaces where the grammar allows async callables.

## Signature Rewrite

At the source level, an async function reads like a function that returns its
success value through `await`. At the callable level, the function takes a
completion callback. The callback is a `once` callable because the async
operation must complete exactly once.

Conceptually:

```camp
async const char[] readAll(const char[] path, thrown IoError);
```

has a callback-shaped representation like:

```camp
void readAll(const char[] path, once void(const char[] result, thrown IoError) completion);
```

The compiler owns the exact expanded parameter order. API authors should reason
from the source async signature and the completion callback contract.

## Completion Callbacks

An awaitable completion callback:

- returns `void`;
- has no `out` parameters;
- may contain one `thrown` parameter;
- has at most one ordinary non-error success parameter.

Use a named result struct when an async operation needs to return more than one
ordinary success value.

```camp
export struct LoadResult
{
	const char[] text;
	nuint bytesRead;
}

export async LoadResult loadDocument(const char[] path, thrown IoError);
```

Completion callbacks with two or more ordinary success values can still be
valid callbacks, but they are not awaitable. Call them manually or wrap the
values in a result type.

## `await`

`await` waits for an async call and resumes the current async function.

```camp
const char[] text = await loadText(path);
```

The operand must be a call expression that reaches an async callable target.
The expression may include member, property, or indexer access before the final
call, but the awaited operation is the final async call. `await` is not a
general operator over arbitrary values.

The awaited result type is:

| Completion shape | Await result |
|---|---|
| No ordinary success parameter | `void` |
| One ordinary success parameter | That parameter's type |
| One `thrown` parameter | Error is rethrown unless caught |
| Multiple ordinary success parameters | Not awaitable |

## Await Error Handling

Thrown completion slots propagate into the resumed async function unless the
await site handles them.

```camp
const char[] text = await loadText(path, catch error);
```

Use ordinary `try`/`catch` when the surrounding control flow is clearer:

```camp
try
{
	const char[] text = await loadText(path);
	process(text);
}
catch (auto error)
{
	reportLoadFailure(error);
}
```

The thrown slot remains part of the async callable shape even when a particular
await site catches it.

## Resumer Selection

Camp's async model uses explicit resumer selection. A receiver can provide the
resumption behavior through `resumeAsync`; an ordinary parameter can be selected
for the same role with `@awaitwith`. The resumer is responsible for continuing
the caller after suspension.

There is no implicit task object, thread-local event loop, or hidden global
runtime in the language model. Libraries may build such abstractions, but the
language surface remains callback and resumer based.

A concrete async definition with a body is either await-capable or `@noawait`.
An await-capable instance method can use its receiver as the selected resumer.
A free function or static method has no receiver, so it must mark one ordinary
parameter with `@awaitwith` unless it is `@noawait`.

## `resumeAsync`

A resumer provides a `resumeAsync` pattern compatible with the awaited
continuation. It may resume inline or arrange for later execution according to
the resumer's API contract.

```camp
export class EventLoop
{
	void resumeAsync(escaped once void() continuation);
}
```

An async resumer method with no ordinary parameters is also compatible:

```camp
export class AsyncEventLoop
{
	async void resumeAsync();
}
```

That form lowers to the same final escaped `once void()` continuation shape.
It must not declare ordinary parameters or a `thrown` result.

Candidate selection checks receiver/parameter visibility, callable shape,
lifetime, and ambiguity. If no valid resumer exists and the async operation
requires one, the compiler reports a diagnostic. If more than one viable
candidate remains after ordinary lookup and overload resolution, resumption is
ambiguous.

## `@awaitwith`

`@awaitwith` marks the ordinary parameter used to select a resumer.

```camp
export async void fetch(@awaitwith EventLoop* loop, const char[] path);
```

The marked value must provide the required resumption surface. Marking a value
does not allocate a frame or post a continuation by itself; it identifies the
source of resumption behavior for awaits in the function or call path.

`@awaitwith` is valid only on a concrete async definition with a Camp body. It
is not part of callable type compatibility, and it is not valid on abstract,
extern, or interface signatures. The marked parameter must be an ordinary
runtime parameter, not `out`, `thrown`, `within`, `sizeof(T)`, `typenameof(T)`,
`vtableof(T: Interface)`, an overload selector, or a generated completion
callback. At most one parameter may be marked.

If the selected resumer must live in the async frame, ordinary lifetime rules
apply: the resumer value must be escaped-safe or otherwise proven to outlive
the frame.

## `@noawait`

`@noawait` marks an async function that must not suspend.

```camp
@noawait
export async int answerImmediately()
{
	return 42;
}
```

The compiler reports an `await` inside a `@noawait` body. Because the function
does not suspend, lowering can avoid generating an async state-machine frame.
No selected resumer is required, but the function still has the ordinary async
source and callback-shaped ABI surface. `@noawait` is valid only on concrete
async definitions with Camp bodies, not on extern, abstract, interface, or
callable type declarations.

## Async State Machines

An async function that can suspend lowers to a state machine. The generated
frame stores the state needed to resume execution: locals used after await,
temporary values, completion callback state, cleanup state, and resumer
information.

This is user-facing because it affects lifetimes:

- values used after `await` may be stored in the frame;
- scoped values cannot be retained across suspension unless proven safe;
- allocator values captured for frame cleanup must outlive the frame;
- `finally` cleanup must run along all completion paths.

Prefer small async functions and explicit result structs when state would
otherwise become hard to reason about.

## Async Frames And Allocation

Async frames use generated allocation according to async lowering and the
available allocation context. Ordinary source `new` inside an async body still
uses the active `within` context for that source allocation.

If an async function needs a `within` allocator for source allocation, include
it in the signature or call path. If generated async machinery must retain an
allocator, that allocator must be safe to store in the generated frame.

## Calling Async Functions Without `await`

An async callable can be called manually by supplying its completion callback.
This is useful for interop, low-level libraries, and adapters.

```camp
loadText(path, once (const char[] text, thrown IoError error) =>
{
	...
});
```

Manual calls make the callback shape explicit. The caller is responsible for
providing a completion callback that satisfies lifetime, `once`, and error
handling requirements.

## `once`

Async completion callbacks are `once` because an async operation must complete
exactly once. `once` is a call-count guarantee. Context deletion belongs to the
producer that owns the context, such as a generated escaped once lambda or
`postpone`.

Do not assume an arbitrary `once` value deletes itself. Read the API contract or
ensure your producer owns the context.

## `postpone`

`postpone` creates a deferred callable by partially applying a target and some
arguments.

```camp
once void() later = postpone saveRecord(record);
```

The postponed value is `once`-shaped. It owns generated context for the
postponed target and captured arguments, and it deletes that context after
invocation. Captures must satisfy the lifetime of the postponed callable.

`postpone` is useful for callbacks and explicit continuation construction. It
is not a substitute for ordinary function calls when no deferred execution is
needed.

## Async Lambdas And Callable Values

Escaped async callable lambdas are represented through delegate-like values and
completion callbacks. A rich async lambda body that can suspend has the same
frame and lifetime concerns as an async function body.

When an async callable escapes:

- captured values must be safe to store;
- captured allocators must remain valid for cleanup;
- completion callback shape must be awaitable if callers will use `await`;
- `thrown` slots must be propagated or handled.

## Lifetimes Across Suspension

Lifetimes are checked across suspension points. A pointer, interface adapter,
delegate context, array view, or generic value used after an `await` may need to
be stored in the async frame. If it is scoped to the pre-await stack, the
compiler rejects the use.

```camp
export async void process(scoped byte* temporary)
{
	await waitForSignal();
	use(temporary); // invalid if temporary would need to cross suspension
}
```

To fix such code, redesign the API so the value is escaped-safe, copy the data
into owned storage, or avoid using it after suspension.

## Structural Async And Interop

Because async is callback-shaped, Camp async APIs can interoperate with native
or hand-written callback APIs when the shape matches. The more structural the
interop boundary, the more important it is to spell the completion callback,
error slot, lifetime, and allocator contracts precisely.

Use `async` for source clarity when the API is intended to be awaited. Use
manual callback signatures when the API is deliberately low-level or
interop-facing.
