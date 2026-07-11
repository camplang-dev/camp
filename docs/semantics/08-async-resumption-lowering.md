# Async Resumption Lowering

## Async Callable Expansion

Async callables lower to callback-shaped functions with completion callback
parameters. The source `async` surface controls how callers use `await` and how
API headers expose the callable.

## Await Site Collection

Async body analysis collects await sites before lowering. Functions marked
`@noawait` must reject await sites in their bodies.

## Completion Callback Shape

Awaitable callbacks return `void`, have no `out` parameters, may have one
thrown parameter, and may have zero or one ordinary success value. Multiple
ordinary success values should use an explicit result struct.

## Resumer Selection

Resumer selection comes from the current async rules: a receiver or parameter
marked with `@awaitwith`, or a receiver pattern that provides `resumeAsync`,
selects the resumption path where valid.

## `resumeAsync`

`resumeAsync` candidates may be ordinary or async-shaped according to the
binding rules. Candidate selection must reject ambiguous, missing, or invalid
resumers with source-range diagnostics.

## `@awaitwith`

`@awaitwith` validates the marked parameter or receiver as the resumer source.
Lowering must thread the selected resumer through await continuation state.

## `@noawait`

`@noawait` allows async-shaped callables that do not suspend. Lowering can avoid
frame generation when analysis proves there are no await sites.

## State Machine Frames

Suspending async functions lower to state-machine frames containing locals,
temporaries, completion callback state, continuation state, and cleanup fields.

## Tail Await Forwarding

Tail await forwarding can avoid unnecessary frame work when an async call simply
forwards its completion. The transformation must preserve thrown propagation and
resumer choice.

## Error Propagation

Thrown completion slots are rethrown into the resumed state machine unless the
await site supplies a catch argument.

## `postpone`

`postpone` builds a once callable by partially applying a target and arguments.
The generated postponed context owns captured arguments and deletes itself after
invocation.

## Async Diagnostics

Diagnostics should distinguish invalid async signatures, invalid await
operands, invalid resumers, forbidden await in `@noawait`, and lifetime or
allocation failures in generated frames.
