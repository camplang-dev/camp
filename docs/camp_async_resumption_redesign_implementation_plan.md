# Camp Async Resumption Redesign Implementation Plan

Status: planned  
Source proposal: `docs/proposals/camp_async_resumption_redesign_proposal_v2.md`  
Audience: implementation agents working in the Camp compiler

## Summary

Replace the old scheduler-based async resumption model with the resumer model
from `camp_async_resumption_redesign_proposal_v2.md`.

Camp async remains callback-shaped: an `async` declaration still lowers to a
`void` ABI function with a final completion callback. The redesign removes
`upon`, language-recognized `Scheduler` patterns, scheduler frame allocation,
scheduler `post(...)`, implicit scheduler forwarding, and direct-resume fallback
behavior. Every suspending `await` instead resumes through a selected ordinary
object that provides a compatible `resumeAsync(...)` method.

The selected resumer for a concrete async definition with a Camp body is:

1. the single ordinary parameter marked `@resumewith`, when present;
2. otherwise the receiver `this`, when the definition has one.

An async body that cannot suspend may be marked `@noawait`; it then requires no
selected resumer and may not contain `await`.

This work intentionally does not redesign `once`, lambda `context`,
`delete context`, or `postpone` except where the removal of `upon` affects
postponed-call slot handling.

## Global Rules

- Each stage must finish with targeted tests, a full non-skipped suite pass on
  macOS, Windows/MSVC, and WSL/Linux, and a commit.
- Completion criteria are struck through as each item is completed.
- Do not commit failing tests or `.actual` files.
- Diagnostics must include source ranges and should explain the specific rule
  violated.
- Unrelated bugs discovered during implementation should be fixed only when
  trivial; otherwise log them in `tests/OutstandingBugs.md` using the next
  available bug number.
- After the final stage, all traces of the old active `upon` design must be
  gone from the current spec, LLM guide, LSP docs, grammar, syntax, metadata
  supplement, active tests, and active metadata/API goldens. Historical mentions
  may remain only in clearly superseded/rejected proposal history.

## Stage 1: Attribute Surface And Validation Scaffold

Goal: introduce `@resumewith` and `@noawait` as source-level semantic
attributes without switching async lowering yet.

### Implementation

- Add special semantic handling for `@resumewith` on parameters.
- Add special semantic handling for `@noawait` on concrete async definitions.
- Add model fields or normalized helpers for:
  - `FunctionDefinition.IsNoAwait`;
  - `ParameterDefinition.IsResumeWith`;
  - future `AsyncResumerInfo` / resumer selection state.
- Validate `@noawait` placement:
  - valid only on concrete async functions or methods with Camp bodies;
  - invalid on function type declarations, callable newtypes, interface
    signatures, abstract declarations, extern declarations, and non-async
    declarations.
- Validate `@resumewith` placement:
  - valid only on one ordinary runtime parameter of a concrete async function or
    method with a Camp body;
  - invalid on `out`, `thrown`, `within`, `sizeof`, `typenameof`, `vtableof`,
    overload selector, generated completion parameters, callable newtypes,
    function types, interfaces, abstract declarations, and extern declarations.
- Enforce the `@noawait` body rule: no `await` may appear anywhere in that async
  body.
- Ensure `@resumewith` and `@noawait` do not participate in callable type
  compatibility and do not change ABI shape.
- Keep current scheduler-based lowering temporarily so this stage can land
  safely before the behavior switchover.

### Tests

- Add dense diagnostics coverage for invalid `@noawait` and `@resumewith`
  placements.
- Add positive API/metadata coverage proving these are source attributes, not
  callable type modifiers.
- Add C/API coverage proving `@noawait async` still emits the ordinary async
  callback-shaped ABI.
- Add a negative fixture proving `@noawait` rejects an `await` expression.
- Run targeted diagnostics/API/metadata/CEmit tests during development.

### Completion Criteria

- [x] ~~`@noawait` and `@resumewith` parse, bind, serialize, and diagnose
      correctly.~~
- [x] ~~`@noawait` rejects suspension with a clear source-ranged diagnostic.~~
- [x] ~~`@resumewith` is accepted only on a single ordinary runtime parameter of a
      concrete async body.~~
- [x] ~~Neither attribute changes callable type compatibility or ABI shape.~~
- [x] ~~Stage 1 targeted tests pass.~~
- [x] ~~Full macOS suite passes.~~
- [x] ~~Full Windows/MSVC suite passes.~~
- [x] ~~Full WSL/Linux suite passes.~~
- [x] ~~Stage 1 is committed.~~

## Stage 2: Resumer Semantics And Lowering Switchover

Goal: replace `upon` scheduler behavior in compiler semantics and generated C
with ordinary resumer selection and `resumeAsync(...)` invocation.

### Implementation

- Remove active `upon` support from parser, bindable model, analyzer,
  serializers, metadata, and C emission:
  - remove `ParameterModifier.Upon`;
  - remove `upon` parameter modifier parsing;
  - remove scheduler pattern validation;
  - remove implicit `upon` forwarding;
  - remove `upon` metadata modifier output;
  - remove scheduler frame fields and scheduler C helpers.
- Make old `upon` syntax fail with a useful migration diagnostic, preferably:
  `The 'upon' scheduler parameter modifier was removed; use @resumewith on an ordinary parameter or a receiver resumeAsync method.`
- Implement resumer selection for every concrete async body:
  - `@resumewith` parameter wins;
  - otherwise receiver `this`;
  - otherwise report an error unless the body is marked `@noawait`.
- Validate exactly one viable compatible `resumeAsync` method on the selected
  resumer type.
- Accept ordinary resumer pattern:

  ```camp
  void resumeAsync(escaped once void() continuation);
  ```

- Accept equivalent callable-this spelling:

  ```camp
  void resumeAsync(once void(escaped this) continuation);
  ```

- Accept async resumer pattern:

  ```camp
  async void resumeAsync();
  ```

  The async form must have no ordinary parameters and no thrown result.

- Reject selected resumers with:
  - no compatible `resumeAsync`;
  - multiple viable compatible `resumeAsync` methods;
  - non-escaped continuation parameter;
  - non-`once void()` continuation parameter;
  - wrong return type;
  - invalid async `resumeAsync` shape.
- Update async-frame lifetime validation so the selected resumer is escaped or
  otherwise proven to outlive the async frame when it is used after suspension.
- Update async C emission:
  - remove scheduler allocation/free paths;
  - remove scheduler `post(...)` calls;
  - remove direct-resume branches;
  - allocate async frames through the selected `within` allocator when valid,
    otherwise fallback `malloc`/`free`;
  - generated completion stores result/error, creates an escaped
    `once void()` resume continuation, then calls
    `selectedResumer.resumeAsync(...)`;
  - for async `resumeAsync()`, supply the resume continuation as the explicit
    final completion callback.
- Preserve manual async calls; explicit final completion arguments remain
  ordinary structural async ABI arguments and do not use the caller's resumer.
- Update `postpone` binding:
  - remove remaining `upon` slot handling;
  - treat an `@resumewith` parameter as an ordinary source parameter slot;
  - supplied `@resumewith` slots are captured like any other argument;
  - omitted `@resumewith` slots become returned `once` delegate parameters.

### Tests

- Rewrite or remove every active test expecting `upon`, language-recognized
  `Scheduler`, scheduler allocation, scheduler posting, direct resume fallback,
  or scheduler metadata.
- Add runtime/CCompile tests for:
  - async receiver method resuming through `this.resumeAsync(...)`;
  - async receiver method using async `resumeAsync()`;
  - static/free async function using `@resumewith`;
  - inline direct behavior implemented by a resumer that calls the continuation;
  - manual async calls not using caller resumer;
  - postponed async calls capturing or exposing `@resumewith` as an ordinary
    slot.
- Add CEmit tests proving:
  - no scheduler `post(...)`;
  - no scheduler `alloc`/`free`;
  - no direct-resume branch;
  - generated continuation call has the expected escaped `once void()` shape.
- Add diagnostics for:
  - async body without selected resumer and without `@noawait`;
  - duplicate `@resumewith`;
  - bad/missing/ambiguous `resumeAsync`;
  - non-escaped/non-once continuation;
  - old `upon` syntax.

### Completion Criteria

- [ ] `upon` is no longer an active compiler feature.
- [ ] Old `upon` syntax produces a clear migration diagnostic.
- [ ] Concrete suspending async bodies require a selected resumer.
- [ ] `@resumewith` and receiver-based resumer selection both work.
- [ ] Ordinary and async `resumeAsync` forms both work.
- [ ] Await lowering always resumes through `resumeAsync(...)`.
- [ ] Scheduler allocation/free/posting and direct-resume fallback are removed
      from generated C.
- [ ] `postpone` treats `@resumewith` as an ordinary source parameter slot.
- [ ] Stage 2 targeted tests pass.
- [ ] Full macOS suite passes.
- [ ] Full Windows/MSVC suite passes.
- [ ] Full WSL/Linux suite passes.
- [ ] Stage 2 is committed.

## Stage 3: Documentation, Metadata, LSP, And Final Erasure

Goal: make the redesigned async model the only current documented async
resumption model and move the redesign proposal to accepted.

### Implementation

- Increment the unified spec filename according to the standing rule, likely
  from `camp_unified_spec_v21.md` to `camp_unified_spec_v22.md`.
- Rewrite current async spec sections:
  - remove all `upon` language text;
  - remove language-level scheduler text;
  - remove scheduler `post(...)` recognition;
  - remove direct-resume fallback language;
  - remove scheduler-based frame allocation;
  - add resumer selection through `this` / `@resumewith`;
  - add `resumeAsync` pattern rules;
  - add `@noawait` rules;
  - update `await` lowering to route through `resumeAsync`;
  - keep allocator/fallback async-frame allocation;
  - update `postpone` notes so `@resumewith` is just an ordinary parameter slot.
- Move or clearly supersede the old scheduler supplement:
  - `docs/camp_async_scheduler_design_v7.md` must not read as current design.
  - The old accepted scheduler implementation plan must be clearly historical
    or superseded.
- Move `docs/proposals/camp_async_resumption_redesign_proposal_v2.md` to
  `docs/proposals/accepted/` once all criteria are complete.
- Update `extras/CAMP_LLM_CODE_GUIDE.md`:
  - remove `upon` from reserved words and async guidance;
  - add `@resumewith`, `@noawait`, resumer examples, and anti-patterns.
- Update grammar documents:
  - remove `upon` parameter modifier;
  - document source attributes where the grammar docs describe them.
- Update `extras/Camp.sublime-syntax`:
  - remove `upon` keyword highlighting;
  - ensure `async`, `await`, `postpone`, `once`, `resumewith`, and `noawait`
    are highlighted appropriately.
- Update `docs/camp_lsp.md`:
  - LSP should surface source-level async/resumer attributes;
  - generated frames/continuations remain hidden;
  - no scheduler/`upon` guidance remains.
- Update `docs/camp_doc_comments_metadata_supplement.md`:
  - remove `upon`;
  - state that `@resumewith` and `@noawait` are source attributes when emitted,
    not callable type modifiers;
  - no generated resumer/frame/continuation details are emitted.

### Tests And Audit

- Run `rg` over active docs, extras, source, tests, metadata/API goldens, and
  syntax files for stale active-design claims:
  - `upon`;
  - `Scheduler selection`;
  - `scheduler allocation`;
  - `scheduler post`;
  - `direct resume`.
- Historical mentions may remain only in clearly superseded/rejected proposal
  history.
- Verify no active metadata/API golden emits `modifier: "upon"`.
- Verify no current spec section describes `upon` as a language feature.
- Run targeted metadata/API/docs-related tests as needed before full suite.

### Completion Criteria

- [ ] Unified spec is version-incremented and updated.
- [ ] Current spec contains no active `upon` scheduler design text.
- [ ] Metadata supplement matches the resumer model.
- [ ] LLM guide matches the resumer model.
- [ ] Grammar docs match the resumer model.
- [ ] Sublime syntax matches the resumer model.
- [ ] LSP docs match the resumer model.
- [ ] Old scheduler supplement/proposal is clearly superseded or historical.
- [ ] Resumption redesign proposal is moved to `docs/proposals/accepted/`.
- [ ] No active metadata/API golden emits `upon`.
- [ ] Documentation/source audit finds no stale active scheduler claims.
- [ ] Stage 3 targeted tests pass.
- [ ] Full macOS suite passes.
- [ ] Full Windows/MSVC suite passes.
- [ ] Full WSL/Linux suite passes.
- [ ] Stage 3 is committed.
