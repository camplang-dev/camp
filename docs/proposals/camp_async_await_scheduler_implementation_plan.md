# Camp Async/Await Scheduler Implementation Proposal

Status: proposed  
Primary design source: `docs/camp_async_scheduler_design_v7.md`  
Baseline language source: `docs/camp_unified_spec_v20.md`  
Audience: implementation agents working in the Camp compiler

## Overview

This proposal implements Camp async/await as specified by `camp_async_scheduler_design_v7.md`. Where that design supplement conflicts with `camp_unified_spec_v20.md`, the supplement wins, except for one later design decision captured by this proposal: awaitable completions may have at most one non-error success parameter, and multi-result await/deconstruction is not part of Camp for this implementation. Async iterators and `await foreach` are explicitly deferred; this work covers async functions, methods, callable types, callable newtypes, property accessors after rewriting, lambdas used with async-shaped callables, manual async calls, `await`, `postpone`, `once`, `upon`, scheduler-driven continuation posting, async-frame allocation/deallocation, lifetime checks across suspension, metadata/API/C emission, and documentation/tooling updates.

Camp async remains structurally callback-shaped. An `async` declaration lowers to an ordinary ABI function whose final parameter is an omitted-at-source `once void(...)` completion callback. Awaitable calls are calls whose final omitted parameter is a compatible `once` completion callback. Awaited calls may produce zero or one non-error success result. A completion `thrown` slot is allowed and follows ordinary `catch` call-site syntax; if not explicitly caught, it is rethrown inside the async state machine. Completion callbacks with more than one non-error parameter are not awaitable. Async functions can also be called manually by supplying the final completion callback explicitly.

Schedulers are explicit values, not thread-local state and not lexical contexts. An `upon` parameter is a declaration-side parameter modifier only. A callable signature may have at most one `upon` parameter. Bare `upon scheduler` in an async routine means `upon escaped Scheduler* scheduler = null`, with `Scheduler` resolved by visible name lookup. A scheduler used by generated async code must provide compatible `alloc(nuint)`, `free(escaped void*)`, and `post(once void(escaped this))` methods. The scheduler controls compiler-generated async frame allocation, frame deallocation, and continuation posting only; ordinary source `new` and pointer-form `delete` continue to use the existing allocator rules.

Async-frame allocation chooses storage in this order: selected non-null scheduler, selected non-null `within` allocator, then visible fallback `malloc`. The frame stores enough deallocation information to later use the matching scheduler, allocator, or fallback `free`. Allocation is assumed to succeed; generated code does not emit null checks or allocation-failure completions. The frame must be stable before awaited operations or scheduler `post(...)` calls can run, because both callees and schedulers may invoke callbacks inline.

`await` is followed directly by one method-call expression or a chain ending in a method call. It is valid only inside async functions or async lambdas. Async lambdas follow the same async body rules as async functions after target typing: their source result/error shape rewrites through a final completion callback, they may suspend, and any generated capture context and async frame must have independent and correctly ordered cleanup. Scheduler selection for each await is: the awaited call's supplied `upon` argument after call matching, otherwise the current async routine's `upon` parameter, otherwise `null`. A non-null selected scheduler posts a generated once continuation; `null` resumes directly.

`postpone` is followed directly by one method-call expression or chain ending in a method call. It performs partial application over the target call surface: supplied slots are evaluated immediately and captured, while omitted slots become parameters of the returned `once` delegate in canonical source parameter order. Implicit `upon` forwarding, implicit `within` forwarding, and default arguments do not fill postponed slots. A postponed context deletes itself after invoking the postponed target; for postponed async calls, deletion happens after the async function has been invoked, not after its later completion.

`once` means the callable is guaranteed to be called exactly once. It does not by itself define context ownership. Compiler-generated producers that own context may self-delete: escaped lambdas target-typed to `once` with generated capture context, and `postpone` contexts. Escaped ordinary delegate lambdas with generated context may opt into cleanup only through the special `delete context` forms. The hidden lambda context parameter is named `context`; ordinary reads, writes, passing, and member access of that name are invalid.

Metadata remains source-level. It must preserve `upon` parameters, scheduler type spelling, async callable forms, and async flags, while excluding generated async frames, continuation helpers, postponed-call context types, lambda context types, and lowering internals. Exported ABI surfaces expose explicit callback-shaped signatures and source-declared scheduler parameters only.

## Current Codebase Touchpoints

- Parser and model:
  - `src/Camp.Compiler/CampParser.cs`
  - `src/Camp.Compiler/SyntaxNode.cs`
  - `src/Camp.Compiler/BindableNode.cs`
  - `src/Camp.Compiler/BindableNodeBuilder.*.cs`
- Type binding, callable binding, and signatures:
  - `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.Callables.cs`
  - `src/Camp.Compiler/CallableShapeService.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs`
- Lowering and generated code:
  - `src/Camp.Compiler/BindableNodeAnalyzer.Lowering*.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.Rewrite.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Lambdas.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.ParamsDeclarations.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.ParamsExpressions.cs`
- Lifetime and flow:
  - `src/Camp.Compiler/BindableNodeAnalyzer.LifetimeFacts.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.Flow.cs`
- Emission and serialization:
  - `src/Camp.Compiler/CCodeEmitter.cs`
  - `src/Camp.Compiler/BindableNodeCodeSerializer.cs`
  - `src/Camp.Compiler/MetadataJsonSerializer.cs`
  - `src/Camp.Compiler/CompilerXmlSerializer.cs`
- Tests and docs:
  - `tests/Ast`, `tests/Api`, `tests/CEmit`, `tests/CCompile`, `tests/Diagnostics`, `tests/Metadata`, `tests/StdRun`
  - `docs/camp_unified_spec_v20.md`
  - `docs/camp_doc_comments_metadata_supplement.md`
  - grammar docs in `docs/`
  - `extras/CAMP_LLM_CODE_GUIDE.md`
  - `extras/Camp.sublime-syntax`

## Global Implementation Rules

- Each phase must finish with targeted tests, full non-skipped suite passes on all supported validation platforms, and a commit. At minimum this means the primary macOS lane, the Windows/MSVC lane, and the Linux/GCC lane that are available in the project workflow.
- Completion criteria should be struck through in this document as each phase is completed.
- Prefer dense tests that cover multiple related cases, but do not hide distinct failure modes in a single unreadable fixture.
- Do not commit failing tests or `.actual` files.
- Diagnostics must be source-ranged, brief, and specific.
- Unrelated bugs discovered during implementation should be fixed only when trivial; otherwise log them in `tests/OutstandingBugs.md` using the next available bug number.
- Async iterators and `await foreach` are deferred. Do not implement async iterator state machines in these phases, except to preserve existing parser/model support and reject unsupported surfaces clearly.
- `init T[n]` array allocation expressions are invalid inside async methods, async functions, and async lambdas, just as they are invalid inside generator bodies. Async code that needs frame-stable storage must use fixed storage where legal, ordinary allocation, or another explicit storage strategy.

## Phase 1: Syntax, AST Model, And Callable Surface

Goal: make the full source surface representable and validate the grammar-level constraints before lowering.

### Implementation

- Audit and complete parsing/model support for:
  - `async` callable forms;
  - `once` callable forms;
  - `await` expressions;
  - `postpone` expressions;
  - `upon` parameter modifier;
  - async callable newtype declarations;
  - async lambdas through target-typed async callable forms;
  - async method/property/interface signatures.
- Ensure `upon` is accepted only as a declaration-side parameter modifier where parameter modifiers are legal.
- Reject `upon` as a statement keyword, expression keyword, prefix operator, argument-list modifier, local declaration modifier, field modifier, or type qualifier.
- Enforce at most one `upon` parameter per callable signature.
- Parse `async iter` only as existing syntax, but mark async-iterator implementation as deferred for this proposal.
- Preserve the source spelling through AST/XML, lowering serialization, and API output.
- Ensure async lambda syntax and target-typed async callable syntax remain represented distinctly enough for later body lowering.

### Tests

- AST/API fixtures covering async functions, methods, interface methods, property accessors, callable newtypes, async callable parameters, async lambdas, `once` callable parameters, bare `upon scheduler`, explicit `upon escaped MyScheduler* scheduler = null`, `await`, and `postpone`.
- Diagnostics fixtures for:
  - two `upon` parameters;
  - `upon` in call arguments;
  - `upon` on locals/fields;
  - `await` outside async;
  - `postpone` followed by a non-call expression;
  - async iter implementation attempts that are still deferred.

### Completion Criteria

- [x] ~~`upon`, `await`, `postpone`, `async`, and `once` syntax is represented in the AST/bindable model.~~
- [ ] Async lambdas are represented as target-typed async callable bodies, not ordinary synchronous lambdas.
- [ ] Invalid `upon` placements produce clear diagnostics with line information.
- [x] ~~Async iter implementation surfaces are rejected or deferred without breaking existing syntax tests.~~
- [x] ~~AST/API/diagnostic tests cover the source surface.~~
- [x] ~~Full suite passes and the phase is committed.~~

## Phase 2: Async Signature Binding And ABI Expansion

Goal: implement source-to-ABI signature rewriting for async declarations and async callable types.

### Implementation

- Rewrite async function/method signatures to `void` ABI functions with a final `once void(...)` completion parameter.
- Completion parameters contain:
  - non-void return slot, when present;
  - completion `thrown` slot, when the async declaration has a thrown parameter;
  - no `out` slots from the source async declaration, because async functions may not have `out` parameters.
- Completion parameters may contain at most one non-error success result slot. A completion shape with two or more non-error parameters is a valid `once` callable shape, but it is not an awaitable completion shape.
- Preserve source-level async signatures in Camp API and metadata while emitting ABI callback components in C.
- Ensure delegate-like ABI component order is call target first, context pointer second for `delegate`, `once`, `iter`, `async`, and eventually `async iter`.
- Ensure exported async functions and methods are callable from C through the rewritten completion callback shape.
- Ensure async callable newtypes have correct source metadata: `callableType: "async"`, return type, callspec/typespec, parameters, and `upon` parameter modifiers where present.

### Tests

- CEmit/API/Metadata fixtures for:
  - `export async void f()`;
  - `export async int f()`;
  - `export async void f(thrown E error)`;
  - `export async int f(thrown E error)`;
  - async instance methods;
  - async interface declarations;
  - async callable newtypes with and without `upon`;
  - manual async declarations with explicit completion callback.
- Diagnostics fixtures for `out` parameters on async functions, malformed completion shapes, and completion callbacks with more than one non-error result parameter used as awaitables.

### Completion Criteria

- [x] ~~Async declarations lower to callback-shaped ABI functions.~~
- [x] ~~Exported C signatures use delegate-like callback component order.~~
- [x] ~~Camp API and metadata preserve the source-level async surface.~~
- [x] ~~Async functions reject source `out` parameters.~~
- [x] ~~CEmit/API/Metadata/Diagnostics tests cover all basic signature shapes.~~
- [x] ~~Full suite passes and the phase is committed.~~

## Phase 3: Scheduler Pattern, `upon`, And Async `within` Defaults

Goal: bind scheduler parameters and allocator defaults according to the scheduler supplement.

### Implementation

- Implement bare `upon scheduler` expansion to `upon escaped Scheduler* scheduler = null` in async routines and async callable contracts.
- Resolve bare `Scheduler` by ordinary visible name lookup.
- Validate scheduler compatibility by pattern:
  - `void* alloc(nuint size)`;
  - `void free(escaped void* ptr)`;
  - `void post(once void(escaped this) continuation)` or a newtype whose underlying callable has that shape.
- Preserve explicitly written scheduler types.
- Implement async-specific bare `within allocator` expansion to `within escaped Allocator* allocator = null`.
- Keep ordinary non-async `within allocator` as `within unscoped Allocator* allocator = null`.
- Reject `within unscoped` in an async routine when the allocator may be retained for async-frame deallocation or used after suspension.
- Implement ordinary call argument matching for `upon`; do not add argument-list `upon` syntax.
- Implement omitted `upon` forwarding outside `postpone`: when an async call omits an `upon` argument, supply the current async routine's scheduler when one exists; otherwise use the callee default.
- Preserve ordinary `within` forwarding/defaulting outside `postpone`, using the async-specific escaped default where the callee used bare async `within allocator`.

### Tests

- CCompile/API/Metadata tests for bare and explicit `upon` parameters.
- Diagnostics for:
  - missing visible `Scheduler` type for bare `upon`;
  - scheduler without `alloc`;
  - scheduler without `free`;
  - scheduler without compatible `post`;
  - `post` with wrong callable category;
  - `post` with wrong `this` lifetime;
  - multiple `upon` parameters;
  - async `within unscoped` retained past suspension.
- Regression tests proving non-async `within` defaults are unchanged.

### Completion Criteria

- [x] ~~Bare `upon scheduler` binds to escaped `Scheduler* = null`.~~
- [x] ~~Scheduler pattern validation is implemented and diagnostics are specific.~~
- [x] ~~Async bare `within allocator` defaults to escaped allocator while non-async behavior remains unchanged.~~
- [x] ~~`upon` appears correctly in API and metadata.~~
- [x] ~~Targeted scheduler/default tests pass.~~
- [x] ~~Full suite passes and the phase is committed.~~

## Phase 4: Async Body Lowering Without Suspension

Goal: lower async functions that contain no `await`, proving completion callback behavior before state machines.

### Implementation

- Lower async `return` to completion-callback calls.
- Lower async `throw`/thrown results to completion-callback error calls.
- Lower async lambda returns/errors through the target async callable's completion callback.
- Assign default values to ordinary result/error slots when required.
- Ensure a no-await async function does not allocate an async frame.
- Support async methods, async free functions, async interface implementations where applicable, and async property accessors after property rewriting.
- Ensure manual async calls with explicit completion callbacks work without `await`.
- Ensure completion callbacks may be lambdas, function references, method references, or callable newtype values as allowed by existing callable conversion rules.

### Tests

- CCompile/StdRun tests for no-await async:
  - void completion;
  - value completion;
  - thrown success and thrown error;
  - async method receiver;
  - async property getter/setter surface after rewriting;
  - explicit manual completion callback;
  - async lambda with explicit completion behavior through target typing.
- CEmit tests proving no frame allocation occurs for no-await async functions.
- Diagnostics for async return type mismatch and malformed completion callback usage.

### Completion Criteria

- [x] ~~Async functions without `await` lower to direct completion callback calls.~~
- [x] ~~No-await async functions do not allocate frames.~~
- [x] ~~Manual async calls with explicit completion callbacks work.~~
- [x] ~~Async property accessor rewriting composes with async completion rewriting.~~
- [ ] Async lambdas without `await` lower through target completion callbacks.
- [x] ~~Targeted CCompile/StdRun/CEmit/Diagnostics tests pass.~~
- [ ] Full suite passes and the phase is committed.

Note: async lambda lowering remains open for Phase 10, which owns async lambda target typing, captures, and context cleanup.

## Phase 5: Async Frame Model And State-Machine Skeleton

Goal: introduce generated async frames and resume functions, before completing full await semantics.

### Implementation

- Add generated frame type support for async routines that may suspend.
- Lift state needed after suspension into the frame:
  - parameters used after suspension;
  - locals live across suspension;
  - result/error slots;
  - selected scheduler for each suspension;
  - deallocation source;
  - state discriminator/program counter.
- Generate resume function(s) that continue from suspension points.
- Ensure generated frames, resume helpers, and internal temporaries are not emitted in source-level metadata.
- Ensure generated names avoid collisions with source identifiers and C reserved words.
- Ensure finally/delete cleanup registrations that cross suspension are represented in the frame and run in the correct order.

### Tests

- CEmit tests inspecting generated frame layout and resume functions for one-await and multi-await functions.
- CCompile tests for:
  - local values used before but not after await;
  - local values used after await and therefore lifted;
  - parameters used after await;
  - nested blocks with variables of the same source name.
- Diagnostics for unsupported values that cannot be lifted.

### Completion Criteria

- [ ] Async frames are generated only when suspension is possible.
- [ ] Live values across suspension are lifted into frames.
- [ ] Generated names are collision-safe.
- [ ] Source-level metadata omits generated frame/resume declarations.
- [ ] Frame/resume CEmit and CCompile tests pass.
- [ ] Full suite passes and the phase is committed.

## Phase 6: `await` Binding, Result Slots, And Error Propagation

Goal: implement semantic binding and lowering for awaited calls.

### Implementation

- Require `await` operands to be one method-call expression or a chain ending in one method call.
- Reject prefix operators between `await` and the awaited method expression.
- Accept awaitable targets when the final omitted parameter is a `once void(...)` callable with:
  - no `out` parameters;
  - at most one `thrown` parameter;
  - zero or one non-error completion parameter used as the awaited success result.
- Reject completion callbacks with two or more non-error parameters as non-awaitable, with a diagnostic that explains Camp does not currently support multi-result await.
- Support awaited calls through:
  - free functions;
  - instance methods;
  - callable values;
  - property/indexer accessor rewriting;
  - async callable newtypes;
  - postponed once delegates that preserve async shape.
- Bind single-result and void-result awaits.
- Support ordinary thrown-parameter call syntax on awaited calls, including `await someMethod(catch auto err)` and `await someMethod(catch _)`.
- When an awaited thrown slot is explicitly caught, lower the generated completion so the caught value is assigned and normal async execution resumes according to ordinary catch-call semantics.
- When an awaited thrown slot is not explicitly caught, rethrow it through the containing async function's error path.
- Preserve line information for missing completion, non-once completion, wrong completion return type, and invalid result binding diagnostics.

### Tests

- CCompile/StdRun tests:
  - `await` returning void;
  - `await` returning one value;
  - completion with thrown success/error paths rethrown through the containing async function;
  - completion with thrown slot caught using `catch auto err`;
  - completion with thrown slot caught using `catch _`;
  - awaited receiver methods;
  - awaited property getter;
  - awaited callable newtype invocation.
- Diagnostics tests:
  - `await` outside async;
  - await target missing final completion;
  - final completion not `once`;
  - final completion returns non-void;
  - completion has `out`;
  - completion has multiple `thrown` parameters;
  - completion has more than one non-error parameter;
  - `auto (x, y) = await ...` deconstruction syntax.

### Completion Criteria

- [ ] Awaitable structural shape rules are enforced.
- [ ] Awaited void and single-result success slots bind correctly.
- [ ] Completion thrown slots rethrow through the containing async function's error path when not explicitly caught.
- [ ] Awaited thrown slots can be consumed with `catch auto err` and `catch _`.
- [ ] Multi-success-result completion callbacks are rejected as non-awaitable.
- [ ] Awaited property/indexer/callable forms work.
- [ ] Positive and negative await tests pass.
- [ ] Full suite passes and the phase is committed.

## Phase 7: Scheduler Selection, Posting, And Frame Allocation

Goal: wire `await` into scheduler-driven continuation posting and frame allocation/deallocation.

### Implementation

- Select scheduler per await:
  1. awaited call's supplied `upon` argument when target has one;
  2. current async routine's `upon` parameter when present;
  3. `null`.
- Store the selected scheduler in the frame before invoking the awaited operation.
- Allocate frame storage in order:
  1. selected non-null scheduler;
  2. selected non-null `within` allocator;
  3. fallback `malloc`.
- Store matching deallocation source in the frame.
- Post resume continuation through `scheduler.post(...)` when selected scheduler is non-null.
- Resume directly when selected scheduler is `null`.
- Ensure the generated `once` scheduled continuation has `escaped this` and can be accepted by scheduler `post`.
- Ensure frame is stable before awaited operations or scheduler posts can invoke callbacks inline.
- Do not emit allocation null checks.

### Tests

- StdRun tests with fake scheduler and fake allocator services that record:
  - allocation call count and size;
  - free call count;
  - post call count;
  - ordering of `alloc`, awaited call, completion, post, resume, free;
  - direct resume path when scheduler is null;
  - allocator fallback when scheduler is null and allocator non-null;
  - malloc/free fallback CCompile path when neither is supplied.
- Tests where awaited callee completes inline.
- Tests where scheduler `post` invokes continuation inline.
- Tests with two awaits using different explicit scheduler arguments, verifying no frame reallocation after the first suspension.

### Completion Criteria

- [ ] Scheduler selection follows the supplement's precedence rules.
- [ ] Frame allocation/deallocation uses scheduler, allocator, or fallback in the correct order.
- [ ] Scheduler `post` receives a compatible once continuation.
- [ ] Inline completion and inline scheduler post are safe.
- [ ] Ordering tests prove frame stability and cleanup.
- [ ] Full suite passes and the phase is committed.

## Phase 8: Lifetime, Definite-Use, And Cleanup Across Suspension

Goal: enforce lifetime and cleanup rules for values crossing suspension points.

### Implementation

- Extend lifetime analysis so values used after suspension must be escaped or proven with `unscoped(...)` to outlive the async frame.
- Treat async frames as ordinary escaped containers for pointer-bearing values.
- Preserve current lifetime semantics for values used only before the first suspension.
- Ensure `within`/`upon` parameters retained in the frame satisfy async lifetime rules.
- Ensure destructors/finally cleanups for lifted values execute exactly once and in source order.
- Reject non-copyable or non-liftable values crossing suspension unless the language has a valid storage strategy for them.
- Reject `init T[n]` array allocation expressions anywhere inside async functions, async methods, and async lambdas.
- Ensure constructor/body changes do not affect call-site lifetime reasoning beyond the function signature.

### Tests

- Diagnostics:
  - scoped pointer local used after await;
  - scoped parameter copied before await and used after await;
  - delegate context with scoped capture crossing await;
  - async `within unscoped` retained across await.
  - `init T[n]` inside an async method/function/lambda.
- Positive tests:
  - scoped value used only before await;
  - escaped pointer used after await;
  - `unscoped(anchor)` value used after await when anchor survives;
  - lifted value with `finally delete` cleanup.
- CEmit/StdRun ordering tests for cleanup after normal completion and error completion.

### Completion Criteria

- [ ] Lifetime checks identify values crossing suspension.
- [ ] Scoped pointer-bearing values cannot be lifted without proof.
- [ ] Escaped and valid unscoped values can be lifted.
- [ ] Cleanup ordering across suspension is deterministic.
- [ ] `init T[n]` expressions are rejected inside async bodies with a clear diagnostic.
- [ ] Lifetime diagnostics are clear and source-ranged.
- [ ] Full suite passes and the phase is committed.

## Phase 9: `postpone` Partial Application

Goal: implement postponed invocation as self-deleting once callable partial application.

### Implementation

- Parse and bind `postpone` followed directly by a method-call expression or chain ending in a method call.
- Bind postponed calls against canonical target slots:
  - receiver;
  - ordinary parameters;
  - defaulted parameters;
  - `within` parameters;
  - `upon` parameters;
  - final async completion parameter when target is async.
- Filled slots are exactly those supplied by postpone syntax; do not fill implicit `upon`, implicit `within`, or default arguments at postponement time.
- Evaluate filled slots immediately and store them in generated postponed context storage.
- Allocate generated postponed context storage through the ordinary generated-context allocation path, storing enough deallocation information to free that context after invocation.
- Omitted slots become parameters of the returned `once` delegate in canonical source parameter order, preserving names, type spelling, modifiers, lifetimes, and defaults where representable.
- On invocation, combine captured and invocation-supplied slots, call the postponed target, and delete postponed context storage after target invocation.
- For postponed async calls, delete context after invoking the async function, not after completion.
- A postponed async call remains awaitable only if the final async completion slot remains unfilled.

### Tests

- CCompile/StdRun tests:
  - basic `postpone f(a)` then call later;
  - receiver capture;
  - named-argument non-prefix capture;
  - omitted default parameter remains a returned delegate parameter/default;
  - `upon` and `within` not implicitly captured;
  - explicit scheduler capture;
  - postponed async call awaited later;
  - postponed async call with explicit completion not awaitable;
  - context deletion after invocation.
- Diagnostics:
  - `postpone` non-call operand;
  - invalid named argument;
  - calling postponed once delegate twice when statically provable;
  - non-liftable captured value.

### Completion Criteria

- [ ] `postpone` performs source-slot partial application.
- [ ] Returned delegate shapes preserve omitted slot order and metadata.
- [ ] Postponed contexts delete themselves through their allocation path.
- [ ] Async postponed calls preserve or lose awaitability according to completion-slot capture.
- [ ] Postpone tests cover positional, named, receiver, scheduler, allocator, default, and async cases.
- [ ] Full suite passes and the phase is committed.

## Phase 10: Lambdas, `once`, And `delete context`

Goal: implement lambda context ownership rules required by async and once callables.

### Implementation

- Rename generated lambda hidden context parameter to `context`.
- Implement async lambda target typing for async callable targets, including `async fn`, `async delegate`, `async once`, and async callable newtypes.
- Ensure async lambdas can contain `await`, lower to completion-callback-shaped call targets, and use the same scheduler/frame rules as async functions.
- Reserve `context` as a special name inside lambda bodies.
- Reject ordinary reads, writes, passing, member access, or address-taking of `context`.
- Implement escaped ordinary delegate lambda context allocation with stored allocator/free path.
- Keep ordinary escaped delegate contexts alive by default; do not auto-delete.
- Implement valid `delete context` forms for escaped ordinary delegate lambdas with generated owned context:
  - final statement for void-returning lambdas;
  - `finally delete context` for non-void lambdas;
  - final `delete context` inside a final cleanup block.
- Reject `delete context` for:
  - scoped delegate lambdas;
  - once lambdas;
  - fn lambdas;
  - non-capturing lambdas;
  - any non-exact context expression.
- Implement escaped `once` lambda auto-deletion after body result/error production.
- Reject explicit `delete context` in once lambdas.
- Ensure generated once-lambda cleanup destroys captured values and frees through the stored allocator/free path.
- Ensure async lambda frame cleanup and lambda capture-context cleanup are ordered independently: completion/resume machinery must not double-free capture context, and once-lambda context deletion must occur after body result/error production.

### Tests

- CCompile/StdRun tests:
  - escaped ordinary delegate lambda with external `delete callback.context`;
  - escaped ordinary delegate lambda with final `delete context`;
  - non-void escaped ordinary delegate lambda with `finally delete context`;
  - escaped once lambda auto-deletes context;
  - once lambda completing through return/error;
  - async lambda awaiting a void operation;
  - async lambda returning a value through completion;
  - async once lambda with captured context and await;
  - async lambda rejected when assigned to a non-async callable target.
- Diagnostics:
  - reading `context`;
  - passing `context`;
  - assigning `context`;
  - `delete context.field`;
  - `delete context` in scoped delegate;
  - `delete context` in once lambda;
  - `delete context` in non-capturing lambda;
  - cleanup registered after `finally delete context`.

### Completion Criteria

- [ ] Lambda hidden context parameter is named `context`.
- [ ] Async lambdas target-type only to async callable targets and may contain `await`.
- [ ] `context` is special and not an ordinary source variable.
- [ ] Escaped once lambdas self-delete generated capture context.
- [ ] Escaped ordinary delegate lambdas support only the valid `delete context` forms.
- [ ] Lambda context ownership tests pass.
- [ ] Async lambda frame and capture-context cleanup ordering is tested.
- [ ] Full suite passes and the phase is committed.

## Phase 11: Integration, Metadata, API, And Interop Hardening

Goal: harden async behavior across existing language surfaces and generated artifacts.

### Implementation

- Ensure async lowering composes with:
  - overload resolution;
  - callable newtype ascription;
  - property getter/setter syntax;
  - interface methods and implementations;
  - extern declarations;
  - generics and erased generic arrays;
  - expanded parameters and materialized params values;
  - `constof`, lifetime annotations, and explicit lifetime casts;
  - target callspecs/typespecs;
  - default arguments and named arguments;
  - `within`, `thrown`, `sizeof`, `vtableof`, and `typenameof`.
- Ensure metadata:
  - emits `modifier: "upon"` for scheduler parameters;
  - preserves source scheduler type spelling;
  - emits async functions/methods as source-level async surfaces;
  - emits callable newtypes with `callableType: "async"`;
  - omits generated async frames, continuation helpers, postponed contexts, and lambda context internals.
- Ensure generated C:
  - compiles for representative async exports;
  - preserves callspec placement;
  - uses stable callback component order;
  - does not emit generated internals into public Camp/C API headers unless they are part of the explicit ABI surface.

### Tests

- Dense integration tests for:
  - overloaded async and non-async names;
  - async callable newtype assignment/ascription;
  - async interface method implementation;
  - extern async imports with manual callback calls;
  - generic async helper with `sizeof(T)`;
  - expanded array/string single-result values;
  - `constof` result through awaited completion;
  - named/default scheduler and allocator arguments;
  - target callspec async callback surfaces.
- Metadata golden tests for exported async functions, methods, interfaces, properties, callable newtypes, and `upon`.
- API golden tests for public/exported async surfaces.
- CCompile tests for generated C on macOS and MSVC-compatible shapes where available.

### Completion Criteria

- [ ] Async composes with major callable, property, interface, generic, and expanded-form surfaces.
- [ ] Metadata/API output is source-level and omits generated internals.
- [ ] Representative async C output compiles.
- [ ] Integration tests cover interactions with current complex language features.
- [ ] Full suite passes and the phase is committed.

## Phase 12: Final Audit, Documentation, Tooling, And Acceptance

Goal: close the implementation by auditing the design, updating documentation/tooling, and moving this proposal to accepted.

### Implementation

- Re-read `docs/camp_async_scheduler_design_v7.md` end to end.
- Re-read relevant sections of `docs/camp_unified_spec_v20.md`.
- Verify every rule in the scheduler design is either implemented, tested, or explicitly deferred.
- Confirm async iterators and `await foreach` remain deferred and clearly documented as such.
- Update `docs/camp_async_scheduler_design_v7.md` or its successor note so it no longer claims plural non-error completion result slots are awaitable.
- Update the unified spec:
  - increment the spec version filename according to the standing rule;
  - revise async sections to match scheduler design v7;
  - remove or correct stale claims such as intrinsic once context deletion semantics;
  - remove multi-result await/deconstruction examples and clarify that awaitable completions may have at most one non-error success parameter;
  - document `upon`, scheduler selection, frame allocation, `postpone`, lambda `context`, and `delete context`.
- Update metadata supplement to describe `upon`, async callable metadata, and omitted generated internals.
- Update grammar documents for `upon`, `await`, `postpone`, async callable forms, and `once` where needed.
- Update `extras/CAMP_LLM_CODE_GUIDE.md` with practical async coding guidance and anti-patterns.
- Update `extras/Camp.sublime-syntax` for `await`, `postpone`, `upon`, `async`, `once`, and special lambda `context` highlighting if supported.
- Update any LSP plugin or LSP-facing syntax/metadata guidance files present in the repo. If no LSP plugin exists yet, add a short note in the LLM guide describing future LSP expectations rather than inventing a plugin.
- Move this proposal from `docs/proposals/` to `docs/proposals/accepted/` after all criteria are complete and struck through.

### Tests

- Documentation consistency pass with `rg` for stale async claims.
- Syntax highlighting fixture or manual review for new keywords.
- Metadata/API final targeted tests.
- Full test suite on the primary platform.
- Windows/MSVC and Linux/GCC validation lanes for the same final revision, matching the all-platform phase gate.

### Completion Criteria

- [ ] Every rule in `camp_async_scheduler_design_v7.md` is audited against implementation and tests.
- [ ] The scheduler supplement's stale multi-result await wording is removed or superseded.
- [ ] Async iterators remain clearly deferred.
- [ ] Unified spec is version-incremented and updated.
- [ ] Metadata supplement is updated.
- [ ] Grammar documents are updated.
- [ ] LLM guide is updated.
- [ ] Sublime syntax is updated.
- [ ] LSP-facing docs/plugin guidance is updated where applicable.
- [ ] No stale async claims remain in docs.
- [ ] This proposal is moved to `docs/proposals/accepted/` with completed criteria struck through.
- [ ] Full suite passes and the final phase is committed.

## Deferred Work

- `async iter` implementation.
- `await foreach`.
- Complete async stream standard-library implementation beyond signatures needed for compiler tests.
- Advanced scheduler library design beyond the pattern required by compiler-generated code.
- Rich LSP semantic annotations for async frames and suspension analysis, unless a later LSP-focused plan is approved.

## Suggested Diagnostic Style

- `Parameter modifier 'upon' is valid only in callable signatures.`
- `Callable signature may declare at most one 'upon' parameter.`
- `Bare 'upon scheduler' requires a visible Scheduler type.`
- `Scheduler type 'X' must provide alloc(nuint), free(escaped void*), and post(once void(escaped this)).`
- `await must be followed directly by a method-call expression.`
- `await may be used only inside an async function or async lambda.`
- `Awaited call is missing the final once completion callback parameter.`
- `Awaited completion callback must return void.`
- `Awaited completion callback may not contain out parameters.`
- `Awaited completion callback may contain at most one thrown parameter.`
- `Awaited completion callback may contain at most one non-error result parameter; multi-result await is not supported.`
- `Value used after await must be escaped or proven to outlive the async frame.`
- `init array expressions are not valid inside async bodies; use fixed storage or explicit allocation instead.`
- `postpone must be followed directly by a method-call expression.`
- `postpone does not capture implicit scheduler, allocator, or default arguments; supply them explicitly to capture them.`
- `context is a special lambda cleanup name and cannot be read or assigned.`
- `delete context is valid only in escaped ordinary delegate lambdas with generated capture context.`
- `once lambdas delete generated context automatically; explicit delete context is not allowed.`
