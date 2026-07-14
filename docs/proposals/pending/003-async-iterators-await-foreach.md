# Async Iterators And `await foreach`

Status: pending  
Proposal date: 2026-07-14  
Last updated date: 2026-07-14

Archive sources:

- `archive/docs/camp_unified_spec_v38.md`, especially sections 5.2, 5.4,
  and 7.2.
- `archive/docs/camp_async_scheduler_design_v7.md`.
- `archive/docs/proposals/accepted/camp_async_await_scheduler_implementation_plan.md`.
- `archive/docs/proposals/accepted/camp_constof_conversions_signature_supplement.md`.
- `archive/docs/proposals/accepted/camp_lifetime_analysis_implementation_plan.md`.

## Summary

This proposal adds async iterators and `await foreach` to Camp.

The proposed feature extends the existing iterator model rather than inventing
a separate stream abstraction. An async iterator still has state, a `next(...)`
protocol, caller-provided current-value storage, and deterministic cleanup. The
async extension adds a readiness callback so a producer can tell the consumer
when another `next(...)` call may make progress.

The source surface consists of:

- `async iter` callable types and callable newtypes;
- `class async iter` generator declarations;
- `await foreach` loops for consuming async iterators;
- standard-library async stream contracts such as `AsyncByteReader` and
  `AsyncCharWriter`.

This proposal treats async iterators as a proposed addition, not as current
language behavior. The current compiler contains parser/model support and some
diagnostics for the spelling, but it does not implement async iterator state
machines or `await foreach` lowering.

## Motivation

Camp already has two pieces that naturally want to meet:

- iterators, for values that produce a sequence through explicit state and a
  small `next(...)` protocol;
- async functions, for callback-shaped work that may suspend and resume without
  a hidden runtime exception or task object system.

Async streams, file readers, socket readers, UI event streams, and background
work queues often need both. They produce a sequence, but a particular step may
not be ready yet. In C this is usually represented with callback registration,
polling, status codes, or a platform-specific readiness handle. Camp should
give that pattern a source-level form while keeping the ABI plain enough for C
libraries to implement and consume directly.

The design goals are:

- preserve the existing iterator ABI story;
- avoid a required runtime stream object or global scheduler;
- make async streams usable from ordinary C callbacks;
- keep payload delivery and readiness notification separate;
- provide deterministic cleanup when a loop exits early;
- support standard-library async reader and writer contracts without requiring
  complex standard-library object types.

## Non-Goals

This proposal does not add:

- a built-in `Task`, `IAsyncEnumerable`, promise, or observable runtime type;
- runtime exception handling;
- reflection or runtime type information for iterator values;
- automatic polling threads or a hidden global event loop;
- `struct async iter` generators;
- arbitrary structural acceptance of C functions that merely look similar to
  async iterator functions;
- a hidden `hasResult` flag in the async iterator protocol.

The feature should remain close to explicit state, explicit callbacks, and
explicit cleanup.

## Proposed Surface

### Async Iterator Types

An async iterator type is written with `async iter`:

```camp
async iter int
async iter(int)
async iter(int, thrown ReadError)
```

The shape follows ordinary `iter` rules:

- there is exactly one yielded type;
- an optional trailing `thrown` slot reports step failure;
- the yielded slot is an ordinary current-value slot, not `in`, `out`, or
  `thrown`;
- multiple yielded values are not allowed.

When a current item has several fields, the iterator yields one named value:

```camp
struct Packet
{
	byte[] bytes;
	nuint received;
}

async iter(Packet, thrown NetError) packets;
```

### Async Iterator Callable Newtypes

Callable newtypes may use `async iter` as their underlying callable family:

```camp
newtype async iter nuint AsyncByteReader(byte[] buffer);
newtype async iter nuint AsyncByteWriter(const byte[] buffer);
newtype async iter(char[]? line, thrown IoError) AsyncLineReader(char[] buffer);
```

These newtypes name ABI contracts. They do not imply a particular scheduler,
allocation strategy, or backing implementation.

### Async Generator Declarations

An async generator is declared with `class async iter`:

```camp
class async iter int delayedNumbers()
{
	yield 1;
	await delayAsync(1000);
	yield 2;
	await delayAsync(1000);
	yield 3;
}
```

There is no `struct async iter` form. Async iterator state may survive
suspension and may be retained by async consumers, so the generated state is
class-shaped and opaque across the ABI.

Async generator bodies may contain both `yield` and `await`. Calling the
generator creates async iterator state; the body starts when the iterator is
advanced through `next(...)` or `await foreach`.

### `await foreach`

Async iterators are consumed with `await foreach`:

```camp
async void showNumbers()
{
	await foreach (int value in delayedNumbers())
		Console.writeLine(value);
}
```

`await foreach` is an await site. It is valid only where `await` is valid:
inside an async function, async method, async lambda, or async generator body
that is allowed to suspend. It is invalid inside a `@noawait` routine.

The loop source must be an `async iter` value or a value explicitly converted
or blessed as an async iterator. Arrays and ordinary `iter` values are not
accepted by `await foreach`.

The ordinary `foreach` form continues to consume arrays and ordinary
iterators. It does not consume async iterators.

## Relationship To Ordinary Iterators

An ordinary iterator step has this source model for a generated state type `Y`
yielding `T`:

```camp
bool next(Y* this, T* current);
bool next(Y* this, T* current, thrown E error);
```

`current` points at caller-provided storage. The boolean result says whether a
value was produced. Cleanup is deterministic.

An async iterator keeps those properties:

- there is still a generated or external state value;
- there is still a `next(...)` step;
- the caller still provides storage for the current value;
- failure is still reported through an optional thrown slot;
- cleanup is still explicit and deterministic.

The async extension is a readiness callback. Conceptually:

```camp
bool next(Y* this, T* current, escaped once void() ready);
bool next(Y* this, T* current, escaped once void() ready, thrown E error);
```

The callback means only this:

> another call to `next(...)` may make progress.

It does not mean that a value is ready, that iteration has completed, that an
error occurred, or that the callback carries the item. The consumer learns
those facts by calling `next(...)` again.

## Protocol Outcomes

An async iterator step has three observable outcomes:

1. The iterator ended.
2. A logical value is available.
3. The iterator is still active, but this step produced no logical value yet.

The proposed protocol keeps the ordinary boolean result:

- `false` means iteration has ended;
- `true` means the iterator remains active and the current-value storage should
  be inspected.

When the call returns `true`, `await foreach` decides whether the current value
is a logical yield or a no-progress step by applying the default-skipping rule
described below.

The current-value storage should be initialized to the default value before
each `next(...)` call. An async iterator that has no logical value to report
for a step leaves or writes the all-default value and returns `true`.

## Readiness Callback Semantics

The readiness callback is associated with the most recent `next(...)` call.
Its rules are deliberately narrow:

- it may be invoked zero or one time for that call;
- it may be invoked synchronously from inside `next(...)`;
- it may be invoked later by an event loop, interrupt, callback, scheduler, or
  platform completion;
- once a later `next(...)` call supersedes it, the older callback must not
  fire;
- after cleanup, no readiness callback for that iterator may fire.

If an implementation invokes the callback synchronously, it must first reach a
stable re-entrant state. A consumer may immediately re-enter `next(...)` from
inside the callback.

An async iterator consumer must not issue concurrent `next(...)` calls against
the same iterator state. There is at most one outstanding readiness callback
for a stateful async iterator at a time.

## `await foreach` Lowering Model

The loop:

```camp
await foreach (auto item in source)
{
	use(item);
}
```

lowers conceptually to a state machine that:

1. evaluates `source` once and retains the async iterator value;
2. allocates current-value storage for the yielded type;
3. calls the async iterator's `next(...)` protocol with a generated readiness
   continuation;
4. exits when `next(...)` returns `false`;
5. skips the loop body when `next(...)` returns `true` but the yielded value is
   all-default;
6. binds the loop variable and executes the body when `next(...)` returns
   `true` with a non-default logical value;
7. waits for readiness before retrying after a skipped no-progress step;
8. cleans up the iterator when the loop finishes, breaks, returns, throws, or
   otherwise exits.

The actual emitted code should be integrated into Camp's async lowering rather
than implemented as a source-level rewrite that creates nested closures.

`continue` advances to the next async iterator step. `break` exits the loop and
runs iterator cleanup. `return`, `throw`, and jumps that leave the loop must
also run cleanup according to the same deterministic cleanup rules ordinary
iterators use.

## Default Skipping

`await foreach` treats an all-default yielded value as "no logical value this
step" and does not run the loop body.

Examples:

- `0` is skipped for integer results;
- `false` is skipped for `bool`;
- `null` is skipped for pointer-like results;
- an expanded result is skipped only when every lowered component is default;
- a default optional, whose `specified` flag is false, is skipped.

This rule avoids adding a hidden `hasResult` flag to the ABI. It also matches
stream-style contracts where a yielded count of `0` already means no progress.

When a real payload may itself be the default value of its type, the async
iterator should yield `T?`.

```camp
class async iter int? scoresAsync()
{
	yield 0;
	await delayAsync(500);
	yield 5;
}
```

The yielded `0` is carried as a specified optional payload, so it is not the
all-default optional value and is not skipped.

## Optional Autolift In `await foreach`

When the yielded type is `T?`, `await foreach` applies the skip rule before it
binds the loop variable.

With `auto`, Camp infers the loop variable as `T`, not `T?`:

```camp
await foreach (auto score in scoresAsync())
{
	Console.writeLine(score);
}
```

Explicit loop variable types are also allowed:

```camp
await foreach (int score in scoresAsync())
{
	Console.writeLine(score);
}

await foreach (int? score in scoresAsync())
{
	if (score.specified)
		Console.writeLine(score.value);
}
```

In all three forms, a default optional step is skipped before the body runs.

## Manual Driving

The default-skipping rule belongs to `await foreach`, not to the raw async
iterator protocol.

A manual consumer sees the raw yielded value. A conceptual C-facing manual call
looks like this:

```c
uintptr_t count = 0;
bool active = reader(
	reader_context,
	&count,
	on_ready,
	on_ready_context,
	buffer,
	buffer_length);
```

The exact Camp source spelling for manual calls should follow the final
callable slot order chosen during implementation. Manual driving is
intentionally a low-level surface for adapters and interop code.

## Cleanup

Async iterator cleanup follows iterator cleanup rules.

For abstract async iterator callable values, cleanup uses the ordinary
iterator convention: call the protocol with a null current-value slot. The
readiness callback components for a cleanup call must be null/default, and no
readiness callback may fire after cleanup.

For generated `class async iter` state, cleanup follows the generated class
iterator destruction path. Across the exported ABI, the state has a destroy
entry point rather than exposing internal deletion helpers.

`await foreach` is responsible for cleanup on:

- normal completion;
- `break`;
- `return`;
- `throw`;
- `goto` or other control flow that exits the loop;
- failure propagation from the async iterator's thrown slot.

If the yielded type is compiler-expanded, cleanup uses the same current-slot
component rules as ordinary iterators. A null first current component is the
cleanup signal.

## Failure Flow

An async iterator may include a trailing thrown slot:

```camp
async iter(char[]? line, thrown IoError) lines;
```

The thrown slot reports failure from a `next(...)` step. The default value of
the error type means success. A non-default value follows ordinary Camp thrown
flow:

```camp
async void copyLines(AsyncLineReader reader, CharWriter writer, thrown IoError error)
{
	await foreach (char[] line in reader(buffer, catch error))
		writer.writeLine(line, catch error);
}
```

If the loop catches the iterator error, execution follows the written `catch`
target. If it does not, the async routine rethrows the error through its own
completion callback when compatible.

Failure is not reported through the readiness callback.

## Lifetimes And Captures

Async iterator state retains generator parameters and live locals across both
`yield` and `await`. It must therefore satisfy both iterator lifetime rules and
async suspension lifetime rules.

The practical rules are:

- generator parameters are retained in the async iterator state;
- `in`, `out`, and trailing `thrown` parameters are not allowed in the
  generator parameter list;
- failure belongs in the `async iter(..., thrown E)` result shape;
- default arguments are resolved when the iterator state is created;
- pointer-bearing retained values must remain valid for the lifetime of the
  generated async iterator state;
- `await` does not extend the lifetime of scoped locals or borrowed values;
- yielded views must be valid through the current-value protocol surface.

Because there is no `struct async iter`, generated async iterator state is
escaped class-shaped. A generator that retains `this` must satisfy the same
escaped receiver requirements as other class-shaped retained-state surfaces.

## ABI Shape

An async iterator callable value follows the delegate-like component order used
by Camp callable values:

1. call target;
2. context pointer.

For a callable newtype:

```camp
newtype async iter nuint AsyncByteReader(byte[] buffer);
```

the conceptual C-facing callable type is:

```c
typedef bool (*AsyncByteReader)(
	void *context,
	uintptr_t *current,
	void (*ready)(void *ready_context),
	void *ready_context,
	uint8_t *buffer,
	uintptr_t buffer_length);
```

For a failing async iterator:

```camp
newtype async iter(nuint, thrown IoError) AsyncReadStep(byte[] buffer);
```

the conceptual C-facing callable type is:

```c
typedef bool (*AsyncReadStep)(
	void *context,
	uintptr_t *current,
	void (*ready)(void *ready_context),
	void *ready_context,
	IoError *error,
	uint8_t *buffer,
	uintptr_t buffer_length);
```

This proposal places the readiness callback components after the ordinary
current-value slots and before the optional thrown slot and visible per-step
parameters. That gives compiler code a direct extension point from existing
iterator protocol construction. This exact ABI slot order MUST be confirmed
before implementation because it becomes part of every exported async iterator
contract.

For a generated async iterator:

```camp
export class async iter int delayedNumbers();
```

the exported C-facing shape is conceptually:

```c
typedef struct delayedNumbersIter delayedNumbersIter;

delayedNumbersIter *delayedNumbers(void);
bool delayedNumbersIter_next(
	delayedNumbersIter *state,
	int32_t *current,
	void (*ready)(void *ready_context),
	void *ready_context);
void delayedNumbersIter_destroy(delayedNumbersIter *state);
```

The generated state layout is private for `class async iter`.

## Metadata And API Output

Metadata must preserve the source-level async iterator surface. It should not
emit generated async iterator frames, compiler continuation helpers, readiness
adapter helpers, or lowered state-machine internals as source declarations.

Callable newtypes should use:

```json
{
  "callableType": "async iter",
  "returnType": "nuint"
}
```

Iterator thrown slots and per-step parameters should be represented using the
same source-level parameter and modifier schema used for ordinary iterator
newtypes.

Generated `class async iter` declarations should appear as source-level
generators or exported iterator state surfaces, not as their lowered helper
types, unless a separate compiler-internal dump format is being requested.

## Standard Library Additions

The standard library should add async counterparts to the synchronous stream
contracts. These APIs belong to the stream or I/O namespace used by the
standard library.

Core async stream families:

```camp
export newtype async iter nuint AsyncByteReader(byte[] buffer);
export newtype async iter nuint AsyncByteWriter(const byte[] buffer);

export newtype async iter nuint AsyncCharReader(char[] buffer);
export newtype async iter nuint AsyncCharWriter(const char[] buffer);

export newtype async iter nuint AsyncWCharReader(wchar[] buffer);
export newtype async iter nuint AsyncWCharWriter(const wchar[] buffer);

export newtype async iter nuint AsyncACharReader(achar[] buffer);
export newtype async iter nuint AsyncACharWriter(const achar[] buffer);
```

The meaning mirrors the synchronous stream contracts:

- readers fill caller-provided buffers;
- writers consume caller-provided buffers;
- each successful logical step yields the number of elements transferred;
- a yielded count of `0` means no progress yet and is skipped by
  `await foreach`.

In-memory adapters may expose async readers and writers:

```camp
export AsyncByteReader asyncReader(byte[] this);
export AsyncByteWriter asyncWriter(byte[] this);

export AsyncCharReader asyncReader(char[] this);
export AsyncWCharReader asyncReader(wchar[] this);
export AsyncACharReader asyncReader(achar[] this);
```

Byte-to-text adapters should have async counterparts:

```camp
export AsyncCharReader getCharReader(AsyncByteReader this);
export AsyncWCharReader getWCharReader(AsyncByteReader this);
export AsyncACharReader getACharReader(AsyncByteReader this);

export AsyncCharWriter getCharWriter(AsyncByteWriter this);
export AsyncWCharWriter getWCharWriter(AsyncByteWriter this);
export AsyncACharWriter getACharWriter(AsyncByteWriter this);
```

Helper APIs should include async write and read helpers:

```camp
export async void writeAllAsync(AsyncByteWriter this, const byte[] value, thrown IoError);

export async void writeStringAsync(AsyncCharWriter this, char[] value, thrown IoError);
export async void writeLineAsync(AsyncCharWriter this, char[] value = default, thrown IoError);

export async string readAllCopyAsync(AsyncCharReader this, within allocator, thrown IoError);

export async iter nuint readLinesAsync(AsyncCharReader this, char[] buffer, thrown IoError);
export async iter char[]? iterateLinesAsync(AsyncCharReader this, char[] buffer, thrown IoError);
```

The archived design writes exported async-iterator-returning functions with
the spelling shown above. The accepted implementation must make the source
syntax unambiguous so these declarations are parsed as functions returning
`async iter` values, not as async functions returning ordinary iterators.

Equivalent `WChar*` and `AChar*` helper families should exist for UTF-16 and
ASCII/system-code-page text streams when the synchronous families exist.

The archived spec used inconsistent capitalization for `wchar` and `achar` in
one in-memory adapter example. This proposal uses the primitive spellings
`wchar` and `achar`.

## Current Compiler Touchpoints

The current compiler already has partial syntax/model awareness:

- `async iter` type references are parsed;
- metadata can currently write `"callableType": "async iter"` for an
  `IterTypeReference` marked async;
- diagnostics recognize that `await foreach` over arrays and ordinary iterators
  is invalid;
- async iterator `foreach` reports that the feature is not implemented yet;
- existing iterator callable lowering does not include the readiness callback.

Implementation should audit at least:

- parser and syntax nodes for `async iter` and `await foreach`;
- `IterTypeReference.IsAsync`;
- callable shape construction;
- iterator protocol parameter construction;
- `foreach` binding and lowering;
- async state-machine lowering;
- lifetime analysis across `yield` and `await`;
- metadata and API serialization;
- C emission for callable newtypes and generated iterator states;
- diagnostics and compiler dumps.

## Implementation Strategy

Suggested stages:

1. Finalize source syntax and ABI slot order.
2. Ensure parser, syntax serialization, and bindable serialization preserve
   `async iter` and `await foreach`.
3. Bind async iterator types and callable newtypes as a distinct callable
   family with readiness slots.
4. Reject unsupported async iterator constructs with precise diagnostics while
   the lowering work is incomplete.
5. Implement `class async iter` generator expansion, including state layout,
   `yield`, `await`, and cleanup.
6. Extend async lowering to support `await foreach` as a suspending loop.
7. Integrate lifetime analysis for yielded values, retained parameters,
   readiness callbacks, and suspension.
8. Emit C and metadata for async iterator callable values, generated state
   types, and exported async iterator APIs.
9. Add standard-library async stream contracts and helpers.
10. Add parser, diagnostics, API, metadata, lowering, C emission, and execution
    tests.
11. Integrate the accepted details into `docs/language`, `docs/semantics`,
    `docs/compiler`, and the LLM coding guide.

## Risks

Async iterators combine the two most stateful language surfaces Camp has:
iterators and async functions. The main risks are:

- incorrect cleanup on early exit or failed steps;
- readiness callbacks firing after cleanup;
- re-entrant callbacks corrupting iterator state;
- accidentally spinning when a stream reports no progress;
- confusing optional/default payload behavior;
- lifetime holes for yielded views retained across suspension;
- ABI churn if readiness callback slot order changes after release;
- diagnostic ambiguity between ordinary `foreach`, `await`, and `await foreach`;
- standard-library APIs becoming hard to implement on embedded or retro
  targets.

The default-skipping rule is especially important to document and test. It is
small at the ABI level, but surprising if a user expects `yield 0` to run the
loop body.

## Complexity Estimate

Complexity: high.

The feature is parser-small but lowering-large. It touches callable type
binding, iterator expansion, async lowering, lifetime analysis, cleanup,
metadata, and exported C emission. The standard-library work is moderate once
the language feature is stable, but it depends on the final ABI shape.

Expected implementation size is comparable to a significant async or iterator
lowering milestone, not a small syntax feature.

## Acceptance Criteria

- `async iter` type references and callable newtypes bind as a distinct
  callable family.
- `class async iter` generators support `yield`, `await`, retained state,
  thrown slots, and deterministic cleanup.
- `struct async iter` is rejected with a clear diagnostic.
- `await foreach` consumes only async iterator sources.
- `await foreach` lowers through the async state machine and resumes through
  readiness callbacks.
- Default-skipping and optional autolift behavior are implemented and tested.
- Manual async iterator calls and cleanup have a documented ABI shape.
- Readiness callbacks obey latest-call and post-cleanup rules.
- Exported C headers use the accepted call/context and readiness slot order.
- Metadata preserves source-level async iterator declarations and callable
  newtypes.
- Standard-library async stream contracts and helpers compile against the
  accepted feature.
- Documentation is updated after acceptance, and this proposal becomes
  historical rather than canonical.

## Documentation Impact

If accepted, update:

- language guide topics for iterators, async, standard library, and ABI;
- semantics documents for expanded callable forms, iterator lowering, async
  lowering, lifetime analysis, and metadata;
- compiler supplement material for generated C/API/metadata output;
- LLM coding guide examples and pitfalls.

The active language and semantics documentation should not describe async
iterators or `await foreach` as current Camp behavior until this proposal is
accepted and implemented.

## Test Impact

Add tests for:

- parsing and serialization of `async iter` types and `await foreach`;
- diagnostics for `await foreach` over arrays, ordinary iterators, and
  unsupported structural shapes;
- `class async iter` generation and cleanup;
- default skipping for integers, booleans, pointers, optionals, and expanded
  values;
- optional autolift for `auto`, explicit `T`, and explicit `T?`;
- thrown flow through async iterator steps;
- synchronous and delayed readiness callbacks;
- callback suppression after superseding `next(...)` calls and cleanup;
- C emission for async iterator callable newtypes;
- metadata for async iterator declarations and callable newtypes;
- standard-library async stream helper shapes.

## Open Questions And Required Decisions

1. Exact ABI slot order for readiness callback components.

   Best guess: keep existing iterator protocol current slots first, then add
   readiness callback and context, then the optional thrown slot, then visible
   per-step callable newtype parameters. This proposal uses that order, but it
   MUST be confirmed before implementation.

2. Exact source spelling for exported async-iterator-returning functions.

   Best guess: preserve the archived spelling where possible, but define a
   parser rule that treats `async iter` as a return type when the tokens appear
   where a return type is required. If that creates ambiguity with the existing
   `async` function declarator, the language should choose an explicit spelling
   before any standard-library async stream helpers are accepted.

3. Exact manual-call source spelling.

   Best guess: manual calls should follow the final callable parameter order,
   with the readiness callback supplied explicitly and cleanup still spelled by
   passing a null current slot. Examples in this proposal are illustrative until
   slot order is accepted.

4. Whether callable ascription to `async iter` newtypes should be implemented
   in the same milestone.

   Best guess: yes for completeness, because stream adapters and user APIs will
   want named async iterator contracts. If this proves too large, parser and
   binder should accept the newtype form but reject ascription with a clear
   staged diagnostic.

5. Exact blessing/cast syntax for external async iterator values.

   Best guess: follow existing callable and iterator explicit conversion
   patterns, but require an explicit async-iterator type or newtype target.
   Structural C function shapes should not become async iterators implicitly.

6. Behavior when an async iterator reports a no-progress default value and
   never invokes readiness.

   Best guess: the loop remains suspended or externally stalled; the callback
   remains a readiness hint, not a guarantee. Tests should cover conforming
   producers that eventually invoke readiness.

7. Whether `await foreach` may appear inside an ordinary `class iter` generator.

   Best guess: no, because ordinary iterator generators cannot suspend through
   async await. It should be allowed inside `class async iter` bodies and other
   async bodies.

8. Whether all standard-library async stream helpers should be accepted in the
   first language milestone.

   Best guess: accept the core async stream newtypes with the language feature,
   then stage higher-level helper implementation if needed. The proposal keeps
   the helper shapes so the design is not lost.
