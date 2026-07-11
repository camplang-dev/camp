# Async, Await, And Postpone

## Async Callable Shape

`async` functions are callback-shaped. They complete through a final `once`
completion callback in the lowered callable form.

```camp
export async void loadText(const char[] path, once void(const char[] text, thrown IoError) completion);
```

Source `async` declarations let callers use `await` when the completion shape
is awaitable.

## Completion Callbacks

An awaitable completion callback returns `void`, has no `out` parameters, may
have one thrown slot, and has at most one non-error success value.

Use a result struct when an async operation needs to return multiple ordinary
values.

## `await`

`await` waits for an async call and resumes the current async function.

```camp
const char[] text = await loadText(path);
```

The awaited expression must be a call that reaches an async callable target.
Thrown completion slots propagate unless handled by a catch argument.

## Resumer Selection

Camp's current async model uses resumer selection rather than an implicit task
runtime. A receiver or parameter can provide a `resumeAsync` pattern used to
resume the caller after suspension.

## `@awaitwith`

`@awaitwith` marks the parameter or receiver used to select an async resumer.
The compiler validates that the marked value has the required resumption
surface.

## `@noawait`

`@noawait` marks async functions that must not suspend. The compiler reports an
`await` inside such a body.

## `postpone`

`postpone` creates a deferred callable by partially applying a target and some
arguments.

```camp
once void() later = postpone saveRecord(record);
```

Postponed calls are `once`-shaped and own their generated call context.

## Async Frames And Allocation

Async functions that suspend use generated frame state. Allocation of that
state follows the async lowering and allocation rules. Ordinary source `new`
still follows the active `within` context.

## Async Lambdas And Callable Values

Escaped async callable lambdas are represented through delegate-like values and
completion callbacks. Code should make capture lifetime and allocation context
explicit when an async callable escapes.

## Async Error Propagation

Thrown completion slots rethrow into the resumed async function unless the await
site handles them.

```camp
const char[] text = await loadText(path, catch error);
```
