# Async Resumption Lowering

This supplement describes Camp async lowering for compiler writers. Camp async
is callback-shaped, not task-object-shaped: an `async` callable has a
source-level async surface, but its ABI surface is an ordinary `void` callable
with a final completion callback. `await` suspends by arranging a generated
completion callback and resuming through a selected ordinary resumer object.

User-facing syntax appears in
[Async, Await, And Deferred Calls](../language/15-async-await-and-deferred-calls.md). Callable context
ownership is described in
[Callable Lowering And Context Ownership](07-callable-lowering-and-context-ownership.md).

## Source Surface And ABI Surface

Async callables lower to callback-shaped functions with completion callback
parameters. The source `async` surface controls how callers use `await` and how
API headers expose the callable.

For a source declaration:

```camp
async int load(const char[] path, thrown IoError error);
```

the ABI surface is conceptually:

```camp
void load(const char[] path, once void(int value, thrown IoError error) complete);
```

The source declaration still remains `async int load(...)` in source-level
metadata, API headers, diagnostics, and language-service surfaces. Generated
frames, continuation functions, and completion helper declarations are not
source API.

## Completion Callback Shape

Awaitable completions follow a strict normalized shape:

- completion returns `void`;
- completion is a `once` callable;
- completion has no `out` parameters;
- completion may have at most one ordinary success result parameter;
- completion may have zero or one `thrown` parameter;
- multiple ordinary success values require an explicit result struct.

These rules are intentionally stricter than "any callback." They make await
result typing and frame layout deterministic. A manual async call may still
supply the final completion callback explicitly; it just must satisfy the ABI
shape of the async declaration being called.

## Await Site Collection

Async body analysis collects await sites before lowering. Functions marked
`@noawait` must reject await sites in their bodies.

Await-site collection should walk the source body before async lowering mutates
calls, lambdas, `try`/`finally`, property access, interface dispatch, or
expanded forms. The collected source-order index becomes the stable basis for
generated state labels and diagnostics.

`await` is valid only inside an async function or async lambda. The operand must
be an awaitable call expression or a chain ending in an awaitable call. If the
operand is not awaitable, the diagnostic should explain whether the problem is
that the operand is not a call, the target is not async-shaped, the completion
callback shape is invalid, or the success/error result slots are unsupported.

## `@noawait`

`@noawait` marks a concrete async definition whose body cannot suspend. It does
not change source callable type or ABI shape. It only changes body validation
and lowering:

- the body may not contain `await`;
- no selected resumer is required;
- no async frame is required merely because the function is async;
- returns and throws still complete through the final completion callback.

`@noawait` is valid only on concrete async definitions with Camp bodies. It
should be rejected on non-async declarations, extern declarations, interface
signatures, abstract declarations, callable newtypes, and other surfaces that do
not own a body.

## Resumer Selection

Resumer selection comes from the current async rules: a receiver or parameter
marked with `@awaitwith`, or a receiver pattern that provides `resumeAsync`,
selects the resumption path where valid.

For an await-capable async definition, the selected resumer is:

1. the single ordinary parameter marked `@awaitwith`, when present;
2. otherwise the receiver `this`, when the definition has a receiver.

A free or static async function that can suspend therefore needs `@awaitwith`.
An instance async method can use its receiver when the receiver type provides a
compatible `resumeAsync`.

The selected resumer remains an ordinary source parameter or receiver. It is
not a hidden scheduler and it does not allocate frames by being selected. It
must simply provide the compatible resumption method required by the rules
below.

## `@awaitwith`

`@awaitwith` is a source attribute on one ordinary runtime parameter of a
concrete async body. It is not a callable type modifier and does not participate
in callable compatibility.

The compiler should reject `@awaitwith` on:

- non-async declarations;
- extern, abstract, or interface declarations without a Camp body;
- `out`, `thrown`, or `within` parameters;
- `sizeof`, `typenameof`, and `vtableof` capability parameters;
- generated completion parameters;
- overload selectors or other non-runtime parameter forms;
- more than one parameter in the same async definition.

The marked parameter must have a type that resolves to an accessible resumer
type. If the resumer value can be retained across suspension, lifetime analysis
must require an escaped or otherwise frame-safe value.

## `resumeAsync`

`resumeAsync` candidates may be ordinary or async-shaped according to the
binding rules. Candidate selection must reject ambiguous, missing, or invalid
resumers with source-range diagnostics.

The ordinary compatible pattern is:

```camp
void resumeAsync(escaped once void() continuation);
```

Equivalent callable-this spelling is accepted when normalized to the same
escaped hidden-context lifetime:

```camp
void resumeAsync(once void(escaped this) continuation);
```

The async compatible pattern is:

```camp
async void resumeAsync();
```

The async form works because an async `void` method's explicit completion
callback supplies the escaped once continuation. The async `resumeAsync` form
must not declare ordinary parameters or thrown completion slots.

Candidate lookup uses ordinary method lookup, including out-of-scope receiver
methods where those are visible. After lookup and overload resolution there
must be exactly one viable compatible candidate. Ambiguous or missing resumers
should be reported at the selected resumer source: the `@awaitwith` attribute,
the receiver declaration, or the async definition name when no better range is
available.

## State Machine Frames

Suspending async functions lower to state-machine frames containing locals,
temporaries, completion callback state, continuation state, and cleanup fields.

The frame must be stable before the awaited operation is invoked. Awaited
operations may complete inline, and the selected resumer may invoke the
continuation inline. Therefore the compiler must:

- allocate or materialize the frame before calling the awaited operation;
- store all live values needed after the await into the frame first;
- store result/error slots in the generated completion callback;
- generate a once continuation that resumes from the correct state;
- arrange cleanup for captured delegates, postponed contexts, and retained
  locals consistently with ordinary `finally`/delete lowering.

Frame fields are generated ABI details. They must avoid C reserved identifiers
and source collisions, but they should remain readable in dumps and emitted C.

A scoped transformed prep result follows the same frame-crossing prohibition as
scoped initialized array storage. Analysis must reject a result, receiver, or
captured argument that would need to survive suspension without a valid frame
lifetime. An allocated `(new)` prepared result may be retained only when its
allocator and cleanup obligations are themselves frame-safe. Async declarations
remain subject to the existing prep declaration restrictions; invocation-time
transformation does not create an async-specific prep protocol.

## Frame Allocation

Async-frame allocation uses ordinary allocation mechanisms. The selected
resumer does not allocate or free the frame simply by being the resumer.

The compiler chooses frame storage from the active async allocation context,
selected `within` allocator, or fallback allocation path as defined by the
current allocation rules. Generated allocation assumes success. Do not insert a
hidden task object, promise object, scheduler post queue, or allocation-failure
completion unless a future language feature explicitly adds one.

If the frame needs an allocator for later cleanup, lowering must store enough
information in the frame to call the matching free path. Lifetime analysis must
reject allocator values that cannot safely be retained across suspension.

## Await Lowering

For each await:

1. analyze and lower the awaited call as an async call with its final completion
   callback omitted at the source site;
2. allocate/materialize the frame and store live values;
3. create a generated completion callback for the awaited operation;
4. call the awaited operation with that completion callback;
5. return control to the caller;
6. in completion, store success/error values and invoke the selected resumer's
   `resumeAsync` with a once resume continuation;
7. in the resume continuation, continue the state machine at the next state.

Thrown completion slots propagate through the frame unless the await site
provides a catch argument. The lowered code should preserve the same semantics
as ordinary throwing calls after completion is delivered.

## Manual Async Calls

An async function can be called without `await` by supplying the final
completion callback explicitly:

```camp
reader.readAsync(buffer, completed);
```

Manual calls do not create a caller await continuation and do not use the
caller's selected resumer. The callee may still use its own selected resumer if
its body awaits.

The analyzer enforces this distinction by validating an async call without
`await`: either it has the explicit completion callback argument, or it
diagnoses that the call must use `await` or provide completion explicitly.

## Tail Await Forwarding

Tail await forwarding can avoid unnecessary frame work when an async call simply
forwards its completion. The transformation must preserve thrown propagation and
resumer choice.

Tail forwarding is valid only when the source function's completion callback can
be passed directly to the awaited async call without changing:

- success result shape;
- thrown slot shape;
- `within` and capability argument order;
- lifetime relationships;
- cleanup obligations;
- selected resumer behavior for any actual suspension.

When any of those are not preserved, generate the ordinary frame/resume path.

## Error Propagation

Thrown completion slots are rethrown into the resumed state machine unless the
await site supplies a catch argument.

The completion callback should store enough state to distinguish success from
error and should only read the result slot valid for that path. Catch arguments
in source await expressions should be lowered through the same throwing-call
machinery used for non-async calls.

## `postpone`

`postpone` builds a once callable by partially applying a target and arguments.
The generated postponed context owns captured arguments and deletes itself after
invocation.

For async calls, `postpone` treats `@awaitwith` as an ordinary source parameter
slot. If the parameter is supplied, it is captured. If it is omitted, it becomes
a parameter of the returned once delegate in canonical source parameter order.

The existing once/context ownership rules apply:

- supplied arguments are evaluated and captured immediately;
- omitted slots become parameters of the returned once callable;
- the generated postponed context owns captured values;
- the context deletes itself after invoking the target call;
- for an async target, invocation starts the async call and supplies or leaves
  open the completion slot according to ordinary async-call shape.

Named postponed slots should be accepted only where the compiler has a complete
lowering path. If a postponed call surface is reserved or unsupported, diagnose
the specific slot shape rather than lowering partially.

## Async Diagnostics

Diagnostics should distinguish invalid async signatures, invalid await
operands, invalid resumers, forbidden await in `@noawait`, and lifetime or
allocation failures in generated frames.

Important diagnostic categories include:

- `await` outside async;
- async call without `await` and without explicit completion;
- invalid awaited completion callback shape;
- more than one non-error completion result;
- async body that can suspend without a selected resumer;
- missing, ambiguous, or incompatible `resumeAsync`;
- invalid `@awaitwith` or `@noawait` placement;
- `await` inside `@noawait`;
- init-array construction inside async body when declaration-scope storage would
  cross suspension;
- resumer or allocator lifetime unsafe across suspension;
- invalid postponed async slot handling.

Diagnostics should point at the `await`, async declaration, attribute, resumer
parameter/receiver, or awaited call as appropriate.

## Metadata And API

Metadata preserves source-level async declarations:

- functions and interface methods are marked async;
- async callable newtypes retain callable type `async`;
- `@awaitwith` and `@noawait` appear as source attributes when visible;
- generated frames, resume helpers, continuation callbacks, lambda contexts,
  and postponed contexts are omitted.

API headers expose the source async shape needed by downstream Camp analysis,
not the internal generated frame details. C emission exposes the callback-shaped
ABI.

## Test Surface

Async changes should cover:

- basic async signature metadata/API/C emission;
- manual async calls with explicit completion callbacks;
- invalid awaited completion shapes;
- `@noawait` placement and body validation;
- `@awaitwith` placement and metadata;
- receiver-based resumer selection;
- ordinary and async `resumeAsync`;
- missing and ambiguous resumers;
- postponed async calls;
- async lambdas and escaped contexts;
- init-array restrictions in async bodies;
- C emission proving generated frames and continuation calls have the expected
  shape.

## Implementation Anchors

Primary implementation points include:

- `BindableNodeAnalyzer.MethodBody.cs` for await collection, await analysis,
  async-aware calls, and `postpone` analysis;
- `BindableNodeAnalyzer.Declarations.cs` for `@awaitwith`, `@noawait`, resumer
  selection, and async declaration validation;
- `BindableNodeAnalyzer.Lowering.Expressions.cs` and related lowering files for
  hidden completion arguments and generated callable/context rewrites;
- `BindableNodeAnalyzer.Lowering.Lambdas.cs` for async lambda target typing and
  context ownership;
- `MetadataJsonSerializer.cs` for source-level async metadata;
- `tests/Diagnostics/async_*.camp`, `tests/Metadata/async_*.camp`, and async
  C/API fixtures.
