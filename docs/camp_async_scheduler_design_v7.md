# Camp Async Scheduler and Callable Context Supplement

**Status:** superseded historical design  
**Superseded by:** `docs/proposals/accepted/camp_async_resumption_redesign_proposal_v2.md` and `docs/camp_unified_spec_v31.md`
**Audience:** LLM or human agent implementing Camp compiler support for async methods, `await`, `postpone`, `upon` scheduler parameters, async callable forms, lambda context lowering, and once-callable cleanup behavior  
**Source baseline:** `camp_unified_spec_v21.md`, `CAMP_LLM_CODE_GUIDE.md`, and `camp_doc_comments_metadata_supplement.md` from Sources  
**Revision:** 7  
**Last updated:** 2026-07-03

This document no longer supplements the current Camp specification. It is kept
only as historical context for the earlier scheduler-based async design. Where
this document conflicts with the unified spec or accepted resumption redesign,
the newer resumer-based design wins.

It does not restate the base async callback rewrite, delegate expansion, ordinary `within` allocator model, lifetime model, or metadata model except where this supplement changes or specializes behavior.

## Current compiler implementation note

The v21 compiler implements the core async function/callable, `await`, `once`,
`upon`, scheduler-selection, async-frame, postponed-call, and lambda-context
semantics from this supplement, with these explicit deferrals:

- `async iter` and `await foreach` remain reserved design direction.
- Named postponed-call slots are diagnosed as not implemented; positional
  postponed-call slots are supported.
- Escaped async callable lambdas are supported through `new delegate` and an
  explicit completion-callback parameter. Rich async lambda bodies that
  themselves suspend remain future hardening work.

The implemented `await` surface intentionally differs from older wording in
this supplement: an awaitable completion callback may have at most one non-error
success parameter. Completion callbacks with two or more non-error success
parameters are valid callable shapes, but they are not awaitable.

## 1. Core decisions

Camp async remains structurally callback-shaped. A scheduler is an explicit parameter value. It is not a task object, not thread-local state, and not a lexical statement or expression context.

A scheduler provides compiler-generated async machinery with:

- async-frame allocation;
- async-frame deallocation;
- continuation posting.

The scheduler does not control ordinary source `new` or pointer-form `delete`. Those remain controlled by the existing allocator rules.

`upon` is only a declaration-side parameter modifier. It is not a statement, expression, prefix operator, or argument-list keyword.

## 2. `once` callable meaning

A `once` callable means:

- the callable is guaranteed to be called;
- the callable is guaranteed to be called only once.

`once` does not by itself define ownership or deletion of the callable context.

Context deletion belongs to the producer of the callable context. Compiler-generated producers that own their context may generate self-deleting call targets. In this supplement, those producers include:

- escaped lambdas target-typed to `once`, when they allocate capture context;
- `postpone`, which always returns a `once` callable and deletes its postponed-call context after invoking the postponed target.

Ordinary delegate values, including ordinary `once` values received from outside the current construct, do not gain deletion semantics merely because their type is `once`.

## 3. Scheduler pattern

Scheduler recognition is pattern-based. The standard library may provide helper types, but language semantics do not require a single built-in base class.

Recommended helper surface:

```camp
export newtype once void ScheduledContinuation(escaped this);

export abstract class Scheduler: Allocator
{
	abstract void post(ScheduledContinuation continuation);
}
```

A scheduler type used by an `upon` parameter must provide compatible methods:

```camp
void* alloc(nuint size);
void free(escaped void* ptr);
void post(once void(escaped this) continuation);
```

The `post(...)` parameter may be written directly as `once void(escaped this)` or as a callable `newtype` whose underlying form is `once void(escaped this)`.

`post(...)` may invoke the continuation inline or store it for later execution. Generated async code must make the frame stable before it calls `post(...)`.

## 4. `upon` parameters

A callable signature may declare at most one `upon` parameter.

Bare form:

```camp
async void f(upon scheduler)
{
}
```

means:

```camp
async void f(upon escaped Scheduler* scheduler = null)
{
}
```

`Scheduler` is resolved by visible name lookup. A concrete scheduler type may be written explicitly:

```camp
async void f(upon escaped MyLibScheduler* scheduler = null);
```

The `upon` parameter is ABI-visible source surface. It appears in generated headers/API metadata when the declaration is exported or otherwise emitted.

`upon` may appear where a scheduler parameter is part of an async or async-iterator callable contract, including async functions, async methods, async property accessors after rewriting, async callable `newtype` signatures, and async callable parameter types where parameter modifiers are allowed.

## 5. `within` defaults in async routines

Bare `within allocator` uses the same scoped shorthand in async and non-async
routines:

```camp
within allocator
// hidden allocator-context parameter using visible Allocator*
```

Explicit lifetime annotations remain explicit:

```camp
async void f(within escaped Allocator* allocator);
async void g(within unscoped Arena* arena);
```

`within` parameters are not defaulted parameters and may not declare default
values. Their argument is supplied by the current `within` context, by an
explicit `within` argument, or by the implicit-within policy when no explicit
context exists.

If an escaped delegate or once context captures its allocation service for later
cleanup, that captured allocator must satisfy the ordinary escaped-storage
rule, or the lambda should be created inside `within(default)`.

Reject `within unscoped` in an async routine when the allocator may be retained for async-frame deallocation or used after suspension.

Ordinary async parameters keep ordinary lifetime defaults. A value used after suspension must be escaped or proven with `unscoped(...)` to outlive the async frame.

## 6. Supplying scheduler and allocator arguments

`upon` and `within` are declaration-side parameter modifiers. They are not argument-list keywords. A caller explicitly supplies those parameters using ordinary positional or named arguments.

Example declaration:

```camp
async string loadAsync(
	const char[] path,
	upon scheduler,
	within allocator,
	thrown IoError);
```

Valid calls:

```camp
await loadAsync(path);
await loadAsync(path, scheduler);
await loadAsync(path, scheduler, allocator);
await loadAsync(path, scheduler: uiScheduler);
await loadAsync(path, scheduler: uiScheduler, allocator: heapAllocator);
```

When an async call outside `postpone` omits an `upon` parameter, the compiler supplies the current async routine's `upon` parameter when one exists; otherwise the parameter default is used.

When a call outside `postpone` omits a `within` parameter, existing `within` forwarding/default rules apply, with the async lifetime default from this supplement.

If an awaited call explicitly supplies the callee's `upon` argument, that value also selects the caller continuation scheduler for that `await`. Explicit `null` selects direct resumption for that suspension.

## 7. `await`

`await` is followed directly by one method-call expression. No prefix operator may appear between `await` and that method expression.

Here, method-call expression means the Camp call surface that reaches an async callable target: free-function call, receiver method call, callable-value invocation, or property/indexer accessor after rewriting.

Valid operands:

```camp
await loadAsync(path);
await loadAsync(path, scheduler);
await file.AsyncReader.CharReader.readAllCopyAsync(allocator);
await services.Loader.loadAsync(path, scheduler: uiScheduler);
await later(scheduler, allocator);
```

The expression after `await` may be a member/index/property chain, but the chain must end in the call being awaited. After normal rewriting, the final call must match the async callable pattern:

- final parameter is a `once` callable;
- the final `once` returns `void`;
- the final `once` has no `out` parameters;
- the final `once` may contain one `thrown` parameter;
- the final callback parameter is omitted at the `await` site.

The awaited non-error completion parameter becomes the result value. A completion
callback with no non-error parameter has result type `void`. A completion
callback with more than one non-error parameter is not awaitable in the current
language; use a named result struct or explicit `out`/callback API instead. A
completion `thrown` slot is rethrown by the resumed async state machine unless
the await expression supplies an ordinary `catch` argument such as
`await loadAsync(catch err)` or `await loadAsync(catch _)`.

## 8. Scheduler selection at `await`

For each `await`, the selected scheduler for the caller continuation is:

1. the value supplied to the awaited call's `upon` parameter after call matching, when the awaited target has one;
2. otherwise the current async routine's `upon` parameter, when present;
3. otherwise `null`.

The selected scheduler value is stored in the async frame for that suspension.

## 9. Await lowering

For:

```camp
await operationAsync(args, scheduler);
```

conceptual lowering is:

```text
selectedScheduler = scheduler argument selected by await rules
ensure frame exists and is stable
store selectedScheduler in frame for this suspension
call operationAsync(args..., complete, complete_context)
return until completion/resume
```

The generated completion performs:

```text
store completion result/error slots in frame
if selectedScheduler != null:
    selectedScheduler.post(once(call: ResumeFn, context: frame))
else:
    ResumeFn(frame)
```

The resume function performs existing async resume behavior: check pending error slot, bind non-error result slots, continue the state machine.

The awaited operation may invoke its completion inline. The scheduler may invoke the posted continuation inline. The frame must already be stable for both paths.

## 10. Async-frame allocation

Async-frame storage is compiler-generated storage. It is separate from ordinary source allocations.

When a frame must be allocated, choose storage in this order:

1. selected non-null scheduler for the current suspension or current async routine: `scheduler.alloc(size)`;
2. selected non-null `within` allocator for the current suspension or current async routine: `allocator.alloc(size)`;
3. visible fallback allocation: `malloc(size)`.

Retain the matching deallocation source in the frame:

1. scheduler frame: `scheduler.free(frame)`;
2. allocator frame: `allocator.free(frame)`;
3. fallback frame: `free(frame)`.

The compiler allocates the frame before lifted state can outlive the ordinary call stack. If a frame has already been allocated by an earlier suspension, later scheduler or allocator arguments do not reallocate it.

Generated frame allocation assumes success. Emit the allocation call and use the returned pointer as the frame pointer. Do not emit a null check, allocation-error completion, or allocation-failure helper.

## 11. Ordinary allocation inside async routines

Scheduler selection has no effect on ordinary source allocations.

Ordinary `new` and pointer-form `delete` use the existing allocator rules. A scheduler is used for ordinary source allocation only when the program explicitly uses it as an allocator through the existing allocator mechanisms.

Use direct resumption for the no-scheduler continuation path.

## 12. Manual async calls

An async function may be called without `await` by supplying the final completion callback explicitly.

```camp
readAsync(path, scheduler, (text, error) =>
{
	...
});
```

There is no caller await continuation in a manual call. The explicit final completion callback remains the async function's ordinary `once` completion callback.

If the callee's `upon` parameter is omitted in a manual async call, ordinary non-`postpone` argument forwarding/defaulting applies.

## 13. `postpone` as partial function application

`postpone` is followed directly by one method-call expression, using the same call-surface definition as `await`. No prefix operator may appear between `postpone` and the method expression that follows.

`postpone` performs partial function application over that call.

The compiler binds the postponed call against the target callable's argument slots. Argument slots include the receiver when the method-call expression supplies one, ordinary parameters, defaulted parameters, `within` parameters, `upon` parameters, and the lowered final async completion parameter when the target is async.

A slot is filled only when the postponed method-call syntax supplies that slot. Implicit `upon` forwarding, implicit `within` forwarding, and default-argument insertion do not fill slots at the postponement site.

Filled slots are evaluated immediately and captured into allocated postponed-call context storage. Unfilled slots become parameters of the returned `once` delegate.

Named arguments may fill arbitrary slots. The returned delegate's parameters are ordered by the target callable's canonical source parameter order after removing the captured slots. This ordering rule does not imply that slots must be filled left to right.

The returned delegate parameter for an unfilled slot preserves the source parameter's name, type spelling, modifiers, lifetime annotations, and default value when one exists.

When the returned `once` delegate is invoked:

1. invocation arguments fill the previously unfilled slots;
2. captured and invocation-supplied slots are combined into the original call;
3. the postponed target is invoked;
4. the postponed-call context storage is deleted after the target invocation is made.

The generated postponed-call context stores enough allocation information to free itself using the allocator/free path that allocated it. For an async postponed target, this deletion occurs after the async method has been invoked, not after the async completion callback later fires.

### 13.1 Basic example

```camp
async string loadAsync(
	const char[] path,
	upon scheduler,
	within allocator,
	thrown IoError);

const char[] path = ...;
auto later = postpone loadAsync(path);
```

Only `path` is captured. The scheduler, allocator, and final async completion slot are left open.

Conceptual returned callable shape:

```camp
once void(
	escaped this,
	upon escaped Scheduler* scheduler = null,
	within escaped Allocator* allocator,
	once void(string result, thrown IoError) complete)
```

Usage:

```camp
string text = await later(scheduler, allocator);
```

### 13.2 Explicit scheduler or allocator capture

To capture scheduler and allocator in the postponed call, supply them explicitly:

```camp
auto later = postpone loadAsync(path, scheduler, allocator);
string text = await later();
```

To capture only the scheduler:

```camp
auto later = postpone loadAsync(path, scheduler);
string text = await later(allocator);
```

The scheduler and allocator are not captured from the location where `postpone` appears merely because they are current or in scope.

### 13.3 Named-argument partial application

Named arguments can fill non-prefix slots:

```camp
auto later = postpone copyAsync(destination: dst);
```

The receiver, if supplied by the method expression, and `destination` are captured. Other unfilled slots become returned delegate parameters in the target callable's source parameter order.

### 13.4 Receiver capture

For a receiver method call:

```camp
auto later = postpone client.loadAsync(path);
```

`client` is captured as the receiver slot, and `path` is captured as an ordinary filled argument slot.

### 13.5 Async-shaped versus ordinary postponed delegates

A postponed async call remains awaitable only when the final async completion slot is left open.

```camp
auto later = postpone loadAsync(path);
string text = await later(scheduler, allocator);
```

If the postponed call supplies the final completion callback explicitly, the returned `once` delegate no longer has the structural async completion shape and is not awaitable:

```camp
auto fire = postpone loadAsync(path, scheduler, allocator, complete);
fire();
```

The returned delegate in the second example is an ordinary `once` callable whose invocation calls `loadAsync(...)` with the captured completion callback and then deletes the postponed-call context.

## 14. Lambda context ownership

Lambda capture context ownership depends on the target callable category and lifetime.

### 14.1 Escaped ordinary delegate lambda

A lambda target-typed to an escaped ordinary `delegate` must be written with
`new delegate`. Capturing escaped delegate lambdas allocate context storage and
copy captured values into it. When the generated context will later be deleted
by `delete delegate`, the allocator/free path used for the context is stored in
the context so the context can be freed later.

By default, the generated ordinary-delegate call target does not delete that
context. The delegate context may be deleted externally by code that owns the
materialized delegate context, or internally by the lambda through the special
`delete delegate` feature defined below.

### 14.2 Escaped once lambda

A capturing lambda target-typed to an escaped `once` callable allocates context storage, copies captured values into it, and stores the allocator/free path in the context.

The generated once-lambda call target deletes its generated context automatically after the lambda body produces its result/error slots. If no context was allocated, nothing is freed.

Example:

```camp
escaped once int(int) adder = other => other + someLocal;
```

Conceptual lowering:

```camp
struct AdderContext
{
	Allocator* allocator;
	int someLocal;
}

int adder_call(AdderContext* context, int other)
{
	int result = other + context.someLocal;
	context.allocator.free(context);
	return result;
}
```

The exact stored allocator/free representation is implementation-defined, but the generated context must carry enough information to release itself through the allocation path that created it.

Explicit `delete delegate` is invalid in once-lambda bodies because the
generated once-lambda target owns the deletion policy.

### 14.3 Scoped delegate lambda

A scoped delegate lambda does not own escaped heap context. It may access declaration-scope values according to the existing scoped capture rules.

`delete delegate` is invalid in scoped delegate lambdas.

## 15. Special `_context` name inside lambdas

Inside a lambda body, `_context` is an implementation-owned special name.

The generated lambda call function's hidden context parameter is named
`_context`.

The name is not an ordinary readable or assignable source variable. Delegate
context cleanup is written with `delete delegate`, not by naming `_context`.

Invalid uses:

```camp
auto c = _context;
someFunction(_context);
_context = null;
delete _context;
```

Valid cleanup form inside a `new delegate` lambda:

```camp
delete delegate;
```

The cleanup form refers to the innermost `new delegate` lambda currently being
analyzed.

## 16. `delete delegate` in escaped ordinary delegate lambdas

`delete delegate` is valid only in a `new delegate` lambda. It deletes the
compiler-generated owned capture context when one exists and is a no-op for a
valid `new delegate` lambda with a null context.

It is invalid for:

- scoped delegate lambdas;
- once delegate lambdas;
- lambdas that do not use `new delegate`;
- plain `fn` lambdas;
- any expression other than exactly `delete delegate` or the `finally` forms described below.

For a void-returning escaped ordinary delegate lambda, `delete delegate;` may appear as the final statement of the lambda body:

```camp
escaped delegate void(int) action = new delegate value =>
{
	Console.writeLine(value);
	delete delegate;
};
```

For a non-void-returning escaped ordinary delegate lambda, context deletion must be registered through `finally delete delegate`:

```camp
escaped delegate int(int) adder = new delegate other =>
{
	finally delete delegate;
	return other + someLocal;
};
```

`finally delete delegate` must be the last `finally` cleanup registration in the lambda body.

The block form is also valid when the `delete delegate;` statement is the final statement in that cleanup block:

```camp
escaped delegate int(int) adder = new delegate other =>
{
	finally
	{
		logDone();
		delete delegate;
	}

	return other + someLocal;
};
```

No later `finally` cleanup may be registered after the cleanup that deletes the lambda context.

The generated cleanup destroys any compiler-generated captured values requiring destruction and then frees the lambda context through the allocator/free path stored in that context.

After a lambda deletes its own context, the delegate value's context is invalid. Calling the same ordinary delegate value again is invalid program behavior.

## 17. Async callable values and async delegates

Async callable values remain delegate-like expanded values with `call` and `context` components. They do not gain hidden scheduler state.

If an async callable contract needs scheduler participation, include an `upon` parameter in the callable signature:

```camp
export newtype async string Loader(
	const char[] path,
	upon scheduler,
	thrown IoError);
```

Calling through the callable supplies scheduler as an ordinary argument or relies on non-`postpone` forwarding/defaulting:

```camp
string text = await loader(path, scheduler);
```

Callable newtypes keep the same ABI representation as their underlying callable form. Scheduler participation is visible as an ordinary source parameter where present.

Conceptual expanded call shape:

```text
loader.call(loader.context, path, scheduler, complete, complete_context)
```

## 18. ABI model

### 18.1 Delegate-like parameter component order

When a delegate-like value appears as a parameter, its ABI components are emitted in this order:

1. call target;
2. context pointer.

The first component keeps the declared parameter name. The context component is named `<parameter>_context`.

Example:

```camp
void filter(delegate bool(int value) predicate);
```

ABI component order:

```text
predicate          // call target
predicate_context  // context pointer
```

The call target receives the context as its first runtime argument:

```text
predicate(predicate_context, value)
```

This same ordering applies to `delegate`, `once`, `iter`, `async`, and `async iter` parameter expansion unless a more specific existing rule says otherwise.

### 18.2 Async export without scheduler

Source:

```camp
export async int addAsync(int x, int y, thrown CalcError);
```

Conceptual C surface:

```c
void addAsync(
	int32_t x,
	int32_t y,
	void (*complete)(void* complete_context, int32_t result, CalcError error),
	void* complete_context);
```

### 18.3 Async export with scheduler

Source:

```camp
export async string loadAsync(
	const char[] path,
	upon scheduler,
	thrown IoError);
```

Conceptual C surface:

```c
void loadAsync(
	const char* path,
	Scheduler* scheduler,
	void (*complete)(void* complete_context, const char* result, IoError error),
	void* complete_context);
```

A scheduler appears in the ABI only when the source declaration includes an `upon` parameter. A library may expose its own scheduler type:

```camp
export class AcmeScheduler
{
	void* alloc(nuint size);
	void free(escaped void* ptr);
	void post(once void(escaped this) continuation);
}

export async void doWorkAsync(upon escaped AcmeScheduler* scheduler, thrown AcmeError);
```

Compiler recognition remains pattern-based.

## 19. Metadata

Metadata is source-level. Required updates:

1. Add `upon` to parameter modifiers.
2. Preserve source type spelling for scheduler parameters.
3. Continue emitting `"async": true` for async functions and methods.
4. Continue emitting callable newtypes with `"callableType": "async"` or `"async iter"`.
5. Do not emit generated async frame types, scheduled-continuation helper functions, postponed-operation capture types, lambda capture context types, or async/lambda lowering internals in the primary source-level metadata view.

Example parameter metadata:

```json
{
  "name": "scheduler",
  "modifier": "upon",
  "type": "Scheduler*",
  "defaultValue": "null"
}
```

If metadata later gains explicit lifetime fields, a bare async `upon` parameter reports `escaped` according to that schema.

## 20. Compact compiler rules

```text
Scheduler pattern:
    alloc(size)
    free(ptr)
    post(once void(escaped this) continuation)

once:
    guaranteed exactly-once call
    no intrinsic context deletion semantic
    deletion belongs to the callable producer

escaped once lambda with generated context:
    allocate capture context
    store allocator/free path in context
    generated call target frees context after body produces result/error slots
    explicit delete delegate is invalid

escaped ordinary delegate lambda with generated context:
    must be written with new delegate
    allocate capture context
    store allocator/free path in context
    no automatic deletion by default
    may self-delete only through valid delete delegate forms

_context inside lambda:
    hidden lowered context parameter is named _context
    _context is implementation-owned in lambda bodies
    ordinary reads/writes/passing/member access are invalid

delete delegate:
    valid only inside new delegate lambdas
    invalid for once, scoped, fn, and ordinary bare lambdas
    void lambda: delete delegate may be final body statement
    non-void lambda: use finally delete delegate
    finally delete delegate must be the last finally cleanup registration
    in a finally block, delete delegate must be the final statement

upon:
    declaration parameter modifier only
    one upon parameter per callable signature
    bare async form is escaped Scheduler* = null
    no statement or expression form
    no argument-list keyword form

within in async routines:
    bare async form is escaped Allocator* = null
    ordinary non-async shorthand remains unscoped Allocator* = null

Supplying scheduler/allocator:
    pass ordinary positional or named arguments
    omitted upon outside postpone forwards current routine scheduler or default
    omitted within outside postpone uses existing within forwarding/defaulting

await operand:
    directly followed by one method-call expression or chain ending in a method call
    no prefix operator between await and method expression
    final omitted parameter must match once void(...)

Scheduler selection at await:
    awaited call's supplied upon value when target has upon
    else current routine upon parameter
    else null

Frame allocation:
    selected scheduler
    else selected allocator
    else malloc
    allocation is assumed to succeed

Ordinary new/delete:
    existing allocator rules only
    scheduler has no effect unless explicitly used as allocator

Await completion:
    store result/error slots
    post once resume continuation when scheduler is non-null
    otherwise resume directly

postpone:
    directly followed by one method-call expression or chain ending in a method call
    performs partial function application
    filled argument slots are exactly slots supplied by postponement syntax
    implicit upon/within forwarding and defaults do not fill postponed slots
    filled slots are evaluated immediately and captured
    unfilled slots become parameters of returned once delegate
    returned delegate parameters use canonical target source parameter order
    named arguments may fill arbitrary slots
    omitted slots include within, upon, defaults, and async completion
    scheduler/allocator for postponed method are not captured unless supplied explicitly
    returned once delegate deletes postponed capture storage after invoking postponed target
    if async completion slot remains open, returned delegate is awaitable
    if async completion slot is captured, returned delegate is not awaitable

Delegate-like parameter ABI order:
    call target first
    context pointer second
    call target receives context as first runtime argument

Export ABI:
    scheduler parameter appears only when source declares upon
    completion callback components use delegate-like order: complete, complete_context
```
