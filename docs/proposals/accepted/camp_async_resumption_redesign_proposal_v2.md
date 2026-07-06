# Camp Async Resumption Redesign Proposal

**Status:** replacement proposal for compiler and spec update  
**Audience:** LLM or human agent updating the Camp compiler, spec, tests, and metadata output  
**Baseline sources:** `camp_unified_spec_v20.md`, `CAMP_LLM_CODE_GUIDE.md`, `camp_doc_comments_metadata_supplement.md`, and the already-implemented callable-context / `postpone` supplement  
**Supersedes:** the previously implemented async scheduler / `upon` design  
**Date:** 2026-07-03

This proposal replaces only the old scheduler-based async resumption model. It removes `upon` and language-recognized schedulers, and routes every suspending `await` through a selected ordinary object that provides `resumeAsync(...)`.

The compiler has already implemented the revised `once`, lambda `context`, `delete context`, and `postpone` semantics from the previous supplement. Do not redesign or reimplement those features as part of this work, except where this document explicitly says the new await-resumption model affects them.

Where this proposal conflicts with the current spec or the previous scheduler supplement, this proposal is the intended behavior.

## 1. Scope

Implement these changes:

- remove the `upon` keyword and every compiler feature based on it;
- remove language-recognized `Scheduler` / `post(...)` / scheduler allocation behavior;
- add async resumer selection through `this` or `@awaitwith`;
- add `@noawait` for async definitions that cannot suspend;
- lower every suspending `await` through `resumeAsync(...)`;
- update diagnostics, metadata, tests, and spec text accordingly.

Do not change these already-implemented features except as noted in §9:

- `once` callable semantics;
- escaped once lambda self-cleanup;
- lambda `context` reserved name and `delete context` rules;
- `postpone` partial-application semantics;
- delegate-like ABI component order.

## 2. Delete the previous scheduling design

Remove all language, compiler, test, metadata, and documentation support for:

- `upon` as a keyword;
- `upon` parameters;
- `upon` argument handling;
- lexical or expression `upon` forms;
- implicit `upon` forwarding;
- language-recognized `Scheduler` patterns;
- `Scheduler.post(...)` recognition;
- scheduler-based async-frame allocation;
- scheduler-based continuation posting;
- direct-resume fallback behavior.

A user may still define an ordinary type named `Scheduler`, but it has no language meaning. A scheduler-like library type participates in async resumption only if it is selected as a resumer and provides a compatible `resumeAsync(...)` method.

## 3. Unchanged async foundation

Camp async remains structurally callback-shaped.

An `async` callable still lowers to `void` plus a final completion callback. `await` still consumes an awaitable call whose final completion callback is omitted at the source call site. Async callable types and async callable newtypes remain structural callable forms. No task object, promise object, thread-local scheduler, or hidden runtime scheduler is introduced.

The generated resume continuation used by `await` is an escaped `once void()` callable. This proposal relies on the already-implemented `once` callable semantics and does not redefine them.

## 4. Resumer selection

Every concrete async definition with a Camp body is classified as either:

1. **await-capable**, meaning the body may suspend and therefore requires a selected resumer; or
2. **no-await**, meaning the body is marked `@noawait` and may not contain `await`.

For an await-capable async definition, the selected resumer is:

1. the single ordinary parameter marked `@awaitwith`, when present;
2. otherwise the receiver `this`, when the definition has a receiver.

If no selected resumer exists, the async definition is invalid.

Examples:

```camp
escaped class View
{
	void resumeAsync(escaped once void() continuation)
	{
		this.dispatcher.enqueue(continuation);
	}

	async void refresh(thrown UiError)
	{
		auto model = await this.loadModelAsync();
		this.render(model);
	}
}
```

```camp
async void copyAsync(
	@awaitwith Dispatcher* dispatcher,
	AsyncReader reader,
	AsyncWriter writer,
	thrown IoError)
{
	...
	await reader.readAsync(...);
	...
}
```

A free or static async function has no receiver. It must either mark an ordinary parameter with `@awaitwith` or be marked `@noawait`.

## 5. `resumeAsync` pattern

The selected resumer type must provide exactly one viable `resumeAsync` method matching one of these normalized patterns.

### 5.1 Ordinary resumer method

```camp
void resumeAsync(escaped once void() continuation);
```

Equivalent callable-this spelling is accepted when normalized to the same hidden-context lifetime:

```camp
void resumeAsync(once void(escaped this) continuation);
```

The continuation is an escaped one-shot callable. The resumer may call it inline or store it for later. It must eventually invoke the continuation exactly once.

### 5.2 Async resumer method

```camp
async void resumeAsync();
```

This is equivalent because `async void resumeAsync()` lowers to a method whose final completion callback has the required escaped `once void()` continuation shape. Await lowering supplies the resume continuation as the explicit final completion callback of this async call.

The async `resumeAsync` form must not declare ordinary parameters or a thrown result. If it has a Camp body, that body is validated under the same async rules as any other async definition.

### 5.3 Candidate selection

Method lookup uses ordinary Camp method lookup rules, including out-of-scope receiver methods.

If no viable `resumeAsync` method exists, the async definition is invalid unless it is marked `@noawait`.

If more than one viable `resumeAsync` candidate exists after ordinary lookup and overload resolution, the async definition is invalid due to ambiguous resumption.

`resumeAsync` is ordinary source surface. It is not an intrinsic and does not change ABI layout.

## 6. `@awaitwith`

`@awaitwith` marks the ordinary parameter used to resume the async state machine after each `await`.

Rules:

- valid only on a concrete async function or method definition with a Camp body;
- not valid in function type declarations, callable newtype declarations, interface signatures, abstract method declarations, or extern declarations;
- at most one parameter may be marked;
- valid only on an ordinary runtime parameter;
- invalid on `out`, `thrown`, `within`, `sizeof`, `typenameof`, `vtableof`, overload-selector, or generated completion parameters;
- the marked parameter's type must provide a compatible `resumeAsync` method;
- the marked parameter remains an ordinary ABI-visible parameter.

The attribute affects only await lowering in the implementation body. It is not part of callable type compatibility.

If the selected resumer is used after suspension, ordinary async-frame lifetime rules apply. The resumer value must be escaped or otherwise proven to outlive the async frame.

## 7. `@noawait`

`@noawait` marks a concrete async definition whose body cannot suspend.

```camp
@noawait
async int addAsync(int a, int b)
{
	return a + b;
}
```

Rules:

- valid only on a concrete async function or method definition with a Camp body;
- not valid in function type declarations, callable newtype declarations, interface signatures, abstract method declarations, or extern declarations;
- the body may not contain `await`;
- no selected resumer is required;
- the callable still has the ordinary async source and ABI shape.

An async body with no `await` but without `@noawait` still requires a selected resumer. The absence of suspension is a declared body contract only when `@noawait` is present.

## 8. `await` lowering

`await` remains followed directly by one method-call expression or member/property/indexer chain ending in an awaitable call. No prefix operator may appear between `await` and the call expression.

For each `await`, the compiler-generated completion callback:

1. stores non-error result slots and error slot state into the async frame;
2. creates an escaped `once void()` continuation that resumes the suspended frame;
3. invokes the selected resumer's `resumeAsync(...)` method with that continuation;
4. returns to the awaited operation's completion caller.

There is no direct-resume branch in generated await code. Direct behavior is represented by an ordinary resumer implementation that calls the continuation immediately:

```camp
struct DirectResumer
{
	void resumeAsync(escaped once void() continuation)
	{
		continuation();
	}
}
```

The async frame must be stable before the awaited operation is invoked, because the awaited operation may complete inline and the selected `resumeAsync(...)` method may also invoke the continuation inline.

Conceptual lowering:

```text
ensure async frame exists and is stable
call awaited operation with generated completion
return until completion/resumption

completion(result/error):
    store result/error in frame
    selectedResumer.resumeAsync(resumeContinuation)

resumeContinuation():
    continue async state machine from suspension point
```

If the selected `resumeAsync` method is itself `async void resumeAsync()`, lowering supplies `resumeContinuation` as that async call's explicit final completion callback.

## 9. Interaction with already-implemented `postpone`

Do not redesign `postpone`. Its partial-application, owned context, and returned `once` delegate behavior are already implemented.

Only these changes are required for the resumer redesign:

- remove any remaining `upon` slot handling from postponed-call binding;
- treat a parameter marked `@awaitwith` exactly as an ordinary source parameter slot for postponed-call binding;
- if an `@awaitwith` parameter is supplied in the postponed call, it is captured like any other supplied argument;
- if an `@awaitwith` parameter is omitted, it becomes a parameter of the returned `once` delegate like any other omitted argument;
- `@noawait` has no special postponed-call slot and does not affect the returned delegate shape;
- the existing rule for async completion slots remains: a postponed async call is awaitable only when the final async completion slot remains omitted.

Examples:

```camp
auto later = postpone copyAsync(source: src);
// The @awaitwith parameter remains open if it was not supplied.

await later(dispatcher, dest, buffer);
// The dispatcher fills the ordinary @awaitwith parameter slot.
```

```camp
auto laterOnDispatcher = postpone copyAsync(dispatcher, source: src);
// The dispatcher is captured if it fills the @awaitwith parameter slot.
```

Do not add new `postpone` cleanup, lambda `context`, or `once` behavior as part of this redesign.

## 10. Async-frame allocation

Async-frame allocation is allocator-based only. The selected resumer does not allocate or free async frames merely by being the resumer.

Remove all scheduler frame allocation paths. Use the existing allocator/fallback model for compiler-generated async frames:

1. a valid selected `within` allocator when one is available for the frame;
2. otherwise visible fallback allocation such as `malloc` / `free`.

Generated frame allocation assumes success. Emit the allocation call and use the returned pointer as the frame pointer. Do not emit a null check, allocation-error completion, or allocation-failure helper.

Ordinary source `new` and pointer-form `delete` continue to use existing `within` allocator rules. Resumer selection has no effect on ordinary source allocation.

## 11. Manual async calls

An async function may still be called without `await` by supplying the final completion callback explicitly.

Manual calls do not create a caller await continuation and therefore do not use the caller's selected resumer. The callee's implementation may use its own selected resumer if its body awaits.

```camp
readAsync(path, complete);
```

The final completion argument is an ordinary explicit argument under the structural async ABI.

## 12. Parser and AST changes

Remove old scheduling syntax:

- remove `upon` from keyword/reserved-word handling;
- remove `upon` parameter modifier parsing;
- remove `upon` argument parsing;
- remove AST/bindable nodes representing scheduler parameters or `upon` contexts.

Add semantic attributes:

- parse `@awaitwith` on ordinary parameters;
- parse `@noawait` on function/method definitions.

Reject `@awaitwith` and `@noawait` where this proposal says they are invalid.

Do not modify parser/AST support for already-implemented `postpone`, `once`, lambda `context`, or `delete context`, except to remove `upon` interactions and recognize `@awaitwith` as an ordinary parameter attribute for postponed calls.

## 13. Binding and semantic analysis changes

For each concrete async definition with a Camp body:

1. If `@noawait` is present, reject any `await` in the body and skip resumer requirement.
2. Otherwise select the resumer:
   - parameter marked `@awaitwith`, if present;
   - else receiver `this`, if present;
   - else no resumer.
3. If no resumer exists, report an error.
4. Validate that the selected resumer type has exactly one viable compatible `resumeAsync` method.
5. Validate async-frame lifetime requirements for the selected resumer when the body may suspend.

Validation of async callable type compatibility must not include `@awaitwith` or `@noawait`, because those attributes belong to implementations, not callable types.

## 14. Lowering changes

Remove old lowering paths:

- no scheduler field in async frame;
- no scheduler `alloc` / `free` path;
- no scheduler `post` call;
- no direct-resume branch;
- no implicit scheduler forwarding.

New await lowering stores the selected resumer in the async frame when it must be used after suspension. The generated completion callback invokes `selectedResumer.resumeAsync(resumeContinuation)` using the compatible pattern selected by semantic analysis.

The generated resume continuation is an escaped `once void()` callable. Its context is the async frame or a compiler-generated wrapper associated with that frame. Cleanup of the async frame follows the existing async completion/finalization path and existing `once`/producer rules.

## 15. ABI and C emission changes

Remove all generated ABI artifacts related to language-level schedulers.

`@awaitwith` does not add, remove, reorder, or rename ABI parameters. It marks an existing ordinary source parameter for use by the implementation body.

`@noawait` does not change ABI shape.

Async exported signatures remain structural async signatures with final completion callback components. Do not change delegate-like ABI component order.

## 16. Metadata changes

Remove `upon` from parameter metadata modifiers.

`@awaitwith` and `@noawait` are source-level semantic attributes. Metadata output may preserve them as attributes if the metadata schema emits source attributes for such declarations. Do not model them as callable-type modifiers and do not emit generated resumer helper details, async frames, continuation thunks, or scheduler artifacts.

## 17. Diagnostics to add

Add clear diagnostics for:

- any use of `upon` syntax;
- async definition without `@noawait` and without a selected resumer;
- duplicate `@awaitwith` parameters;
- `@awaitwith` on a non-ordinary parameter;
- selected resumer lacks compatible `resumeAsync`;
- selected resumer has ambiguous compatible `resumeAsync` candidates;
- `resumeAsync` continuation parameter is not `escaped`;
- `resumeAsync` continuation parameter is not `once void()`;
- `@noawait` async body contains `await`;
- `@noawait` or `@awaitwith` used in callable type declarations, callable newtypes, interface signatures, abstract declarations, or extern declarations;
- selected resumer fails async-frame lifetime requirements;
- old scheduler-specific lowering or metadata paths are still reachable.

## 18. Test plan

Remove or rewrite every test that expects `upon`, language-recognized `Scheduler`, scheduler allocation, scheduler posting, direct resume fallback, or scheduler metadata.

Add tests for:

1. async receiver method uses `this.resumeAsync(escaped once void())`;
2. async receiver method accepts `async void resumeAsync()` as equivalent;
3. async receiver method without `resumeAsync` is rejected unless `@noawait`;
4. static/free async definition requires `@awaitwith` or `@noawait`;
5. `@awaitwith` parameter controls await lowering;
6. duplicate `@awaitwith` is rejected;
7. `@awaitwith` target without compatible `resumeAsync` is rejected;
8. non-escaped continuation parameter in `resumeAsync` is rejected;
9. `@noawait` body rejects `await`;
10. `@noawait` async method still emits structural async ABI;
11. await lowering always calls `resumeAsync`, even when the resumer invokes the continuation inline;
12. direct-resume code path is absent;
13. async-frame allocation no longer consults scheduler logic;
14. postponed async call treats `@awaitwith` as an ordinary capturable/omittable parameter slot;
15. postponed async call is awaitable according to the already-implemented completion-slot rule.

Do not add new tests for general `postpone`, lambda `context`, escaped once lambda cleanup, or `delete context` as part of this redesign unless needed to prove no regression from the await-resumer change.

## 19. Spec update checklist

Update the spec and supplements as follows:

- remove all `upon` language text;
- remove language-level scheduler text;
- add resumer selection rules for async definitions;
- add `resumeAsync` pattern rules;
- add `@awaitwith` and `@noawait` rules;
- update `await` lowering to route through `resumeAsync`;
- remove direct-resume fallback language;
- remove scheduler-based frame allocation;
- keep allocator-based async-frame allocation;
- update `postpone` text only to replace old `upon` interactions with ordinary `@awaitwith` parameter-slot behavior;
- do not restate or redesign already-implemented `once`, lambda `context`, `delete context`, or `postpone` semantics beyond that compatibility note;
- update metadata guidance to remove `upon` and not treat implementation-only resumption attributes as callable type modifiers.
