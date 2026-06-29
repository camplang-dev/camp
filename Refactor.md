# Camp Compiler Refactor Plan

This plan is a staged quality refactor roadmap. Do not start a stage until the
user explicitly asks for it. When a stage is completed, edit this file and strike
through that stage heading and its completion checklist items before committing.

Every stage must:

- keep compiler behavior unchanged unless the stage explicitly says otherwise;
- add or update only focused tests needed to protect the refactor;
- run targeted tests while developing;
- run the full non-skipped suite before committing:
  `dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll`;
- commit after the full suite passes;
- log unrelated non-trivial pre-existing bugs in `tests/OutstandingBugs.md`
  instead of detouring into large fixes.

## ~~1. Centralize Callable Shape Logic~~

Summary: Replace the current scattered/string-heavy callable shape handling with
one authoritative callable-shape service. This is the highest value first step
because callable compatibility currently touches ascription, lambdas,
interfaces, `constof`, callspecs, `this`, iterators, and method references.

Plan of action:

- Extract `CallableShape`, `ThisContract`, parsing, formatting, expansion, and
  compatibility helpers out of `BindableNodeAnalyzer.Callables.cs` into a
  dedicated internal service/model.
- Keep existing public behavior for `fn`, `delegate`, `iter`, callable newtypes,
  callspecs, expanded parameters, explicit callable `this`, and `constof`
  variance.
- Replace duplicated signature comparison logic in declaration validation,
  interface slot validation, callable ascription, lambda target typing, and
  method-reference conversion with calls into the shared service.
- Preserve existing diagnostics text unless a test proves a diagnostic is
  currently misleading.

Risks:

- Callable shape code still relies on formatted type strings in several places;
  changing too much at once could alter overload or interface compatibility.
- `iter` and `constof` surfaces have special cases that must remain covered.

Success outcomes:

- One service owns callable shape construction, expansion, and compatibility.
- Existing callable/ascription/lambda/interface tests pass unchanged or with only
  intentional golden updates.
- Future callable features have one obvious place to integrate.

Completion checklist:

- [x] ~~Shared callable shape service introduced.~~
- [x] ~~Existing callable shape call sites migrated.~~
- [x] ~~Targeted callable/ascription/lambda/interface tests pass.~~
- [x] ~~Full suite passes.~~
- [x] ~~Stage committed and this section struck through.~~

## ~~2. Centralize Expanded-Form Handling~~

Summary: Consolidate arrays, delegates, optionals, iterators, materialized params,
hidden components, and hidden arguments behind one expanded-form service. The
goal is to reduce recurring bugs where `.length`, `.context`, out returns, or
hidden ABI arguments are missed in one lowering path.

Plan of action:

- Extract `ParamsComponentShape`, component lookup, component expression
  creation, expanded argument expansion, and materialized params helpers from
  `ParamsComponents`, `Lowering.Expressions`, `Lowering.InstanceCalls`,
  `Lowering.ParamsDeclarations`, lambdas, iterators, and lifetime handling into
  a shared service.
- Define clear operations: source components, ABI components, component
  expression creation, argument expansion, return/out expansion, and materialized
  storage shape.
- Migrate call/receiver/property/lambda/iterator lowering to the shared service
  without changing emitted C.
- Keep current fixed-array span view behavior routed through the same service
  where practical.

Risks:

- This code is used by many unrelated features; partial migration could create
  inconsistencies.
- Some existing helpers combine analysis and lowering concerns; extract in small
  moves with tests after each cluster.

Success outcomes:

- Expanded-form rules are no longer rediscovered in multiple lowering files.
- Hidden argument bugs become easier to diagnose.
- Existing array/delegate/optional/iter/materialized params tests pass.

Completion checklist:

- [x] ~~Expanded-form service introduced.~~
- [x] ~~Main lowering call sites migrated.~~
- [x] ~~Targeted expanded-form, delegate, array, iterator, and lambda tests pass.~~
- [x] ~~Full suite passes.~~
- [x] ~~Stage committed and this section struck through.~~

## ~~3. Add Semantic Test Helpers~~

Summary: Add tests that assert compiler semantic facts directly instead of only
checking large goldens. Keep the existing golden system, but add helper APIs for
small assertions about callable shapes, symbols, generated helpers, ABI
parameter lists, metadata surfaces, and target capabilities.

Plan of action:

- Add reusable test helpers in `src/Camp.Compiler.TestRunner` that compile a
  source string through selected phases and expose bound/lowered declarations.
- Add a small set of dense semantic tests for:
  callable shape compatibility;
  expanded ABI parameter lists;
  source vs ABI symbol names;
  generated lifecycle/interface/lambda/iterator helpers;
  metadata/API visibility decisions.
- Keep these tests narrow and avoid duplicating golden coverage.
- Document when to prefer semantic tests over new golden files.

Risks:

- Tests can become coupled to private implementation details if helper
  boundaries are too low-level.
- Over-testing internals could make later refactors harder.

Success outcomes:

- Future refactors can verify semantic invariants without updating large C
  goldens unnecessarily.
- Full suite runtime does not materially increase.

Completion checklist:

- [x] ~~Semantic test helper added.~~
- [x] ~~Initial dense semantic tests added.~~
- [x] ~~Test guidance documented in existing test instructions.~~
- [x] ~~Full suite passes.~~
- [x] ~~Stage committed and this section struck through.~~

## 4. Introduce Stable Diagnostic Codes

Summary: Add structured diagnostic codes while preserving current diagnostic
messages. This prepares for LSP and makes tests less brittle without forcing a
large diagnostic rewrite.

Plan of action:

- Extend parse/bind/analysis/native diagnostics with an optional stable code,
  severity, primary range, and message.
- Keep current human-readable output format by default unless the code already
  has a structured mode.
- Start assigning codes to high-traffic diagnostics in callable binding,
  expanded-form lowering, lifecycle, lifetime, and target/capability checks.
- Update tests only where adding codes intentionally changes output; otherwise
  keep existing goldens stable.

Risks:

- Multiple diagnostic classes currently exist (`ParseDiagnostic`,
  `BindDiagnostic`, `AnalysisDiagnostic`, emitter/native diagnostics as strings).
- Adding codes everywhere at once would be noisy; stage should create the
  structure and migrate representative areas only.

Success outcomes:

- New diagnostics can be emitted with stable IDs.
- LSP-facing code has a clear diagnostic data shape to consume later.
- Existing console output remains familiar.

Completion checklist:

- [ ] Shared diagnostic data shape introduced.
- [ ] Representative diagnostics use stable codes.
- [ ] Existing diagnostic output compatibility preserved or intentionally
      updated.
- [ ] Full suite passes.
- [ ] Stage committed and this section struck through.

## 5. Separate Source Symbols From ABI Symbols

Summary: Make naming explicit by distinguishing source names, callable names,
flattened symbols, `@symbol` overrides, exported API names, and C identifiers.
This addresses a recurring source of header, metadata, overload, and collision
bugs.

Plan of action:

- Introduce a small symbol/name model used by declarations instead of treating
  `Name`, `FullCallableName`, `Symbol`, and emitter names as interchangeable.
- Centralize symbol collision checks currently spread across declaration
  analysis, inline constants, expanded component checks, generated declarations,
  and `@symbol` validation.
- Keep serialized Camp API output and C names identical to today.
- Add semantic tests for source-name lookup, overridden symbol lookup, old
  flattened-name rejection, expanded component collisions, enum constants, and
  generated helper collisions.

Risks:

- Name lookup relies on both source names and symbols in some places.
- API and metadata output intentionally expose different naming layers.

Success outcomes:

- The compiler has one clear answer for "what name is this?" in source, Camp
  symbol, ABI, C, API, and metadata contexts.
- Symbol collision regressions become less likely.

Completion checklist:

- [ ] Symbol/name model introduced.
- [ ] Collision checks route through shared helpers.
- [ ] Representative symbol semantic tests pass.
- [ ] Full suite passes.
- [ ] Stage committed and this section struck through.

## 6. Create A Generated Declaration Factory

Summary: Route generated constructors, destructors, create/destroy helpers,
interface accessors, vtables, iterator state/methods, lambda helpers, `sizeof`,
`vtableof`, and `typenameof` support fields through one factory-style API. This
keeps generated declaration ownership, visibility, provenance, symbols, and API
metadata policy consistent.

Plan of action:

- Add a generated declaration factory/helper layer inside the analyzer.
- Migrate lifecycle generation in `Expansion.cs`, interface/vtable generation,
  iterator generation, lambda helper generation, and hidden generic capability
  fields gradually.
- Ensure every generated declaration records a reason/category and source
  origin when available.
- Preserve existing output and symbol names.

Risks:

- Generated declarations are intertwined with analysis order.
- Some generated declarations intentionally appear in API headers while others
  must remain private implementation details.

Success outcomes:

- Generated declaration policy is centralized and easier to audit.
- API/metadata/C-emission filters no longer need as many name-based guesses.

Completion checklist:

- [ ] Generated declaration factory introduced.
- [ ] Lifecycle and at least two other generated declaration families migrated.
- [ ] Generated declaration provenance/category available to serializers.
- [ ] Full suite passes.
- [ ] Stage committed and this section struck through.

## 7. Preserve Source Provenance On Generated And Lowered Nodes

Summary: Add consistent provenance metadata to generated/lowered nodes so
diagnostics, metadata, API serialization, and future LSP features can explain
what source construct produced a generated node.

Plan of action:

- Add a lightweight provenance record to `BindableNode` or a side table keyed by
  nodes.
- Track source syntax, source symbol, generated reason, and user-facing
  visibility for generated/lowered declarations and important lowered
  expressions.
- Replace fragile checks such as source syntax null/name-prefix tests where
  provenance is enough.
- Use provenance in at least metadata/API filtering and one diagnostic path.

Risks:

- Adding fields to all nodes can make serializers noisy if not carefully
  excluded.
- Lowering clones may drop provenance unless clone helpers are updated.

Success outcomes:

- Generated implementation details can be hidden or explained consistently.
- Future LSP hover/go-to/diagnostic work has reliable source mapping.

Completion checklist:

- [ ] Provenance model introduced.
- [ ] Major generated declaration paths set provenance.
- [ ] At least one serializer/filter path consumes provenance.
- [ ] Full suite passes.
- [ ] Stage committed and this section struck through.

## 8. Extract Target Capability Checks

Summary: Move target/platform/backend capability decisions into a clear target
capability layer. Start small with existing target data such as framework
support, callspec/typespec metadata, natural integer widths, memory model,
native build templates, and C emitter flags.

Plan of action:

- Add a `TargetCapabilities` view built from `TargetDefinition`.
- Move ad hoc target checks from analyzer, C emitter, native build driver, and
  command-line build validation into named capability queries.
- Add capability queries for current behavior: frameworks, function pointer
  callspec placement, natural integer width, pointer width, C emitter flags,
  shared/static/exec support, and selected C language features already assumed.
- Do not redesign target INI schema beyond what is needed to expose existing
  behavior cleanly.

Risks:

- Target definitions are consumed by analyzer, emitter, and native build driver;
  careless changes could break command-line builds.
- Pre-C99/MSVC/WASM capability policy is future work, not this stage.

Success outcomes:

- Unsupported target behavior is reported through capability checks, not
  backend accidents.
- Adding platform-specific emitters has a clearer integration point.

Completion checklist:

- [ ] `TargetCapabilities` introduced.
- [ ] Existing framework/native/callspec-width checks migrated.
- [ ] Target/command-line tests pass.
- [ ] Full suite passes.
- [ ] Stage committed and this section struck through.

## 9. Clarify Semantic Analysis Pass Boundaries

Summary: Make the analyzer pipeline easier to reason about by naming and
separating pass responsibilities without a wholesale rewrite. Current partial
files already suggest pass groupings, but declaration collection, validation,
body analysis, lowering, export visibility, and generated helper creation still
overlap heavily.

Plan of action:

- Document the current analyzer pass order in code comments or a small internal
  pass plan.
- Introduce explicit pass methods/classes for declaration collection, type
  binding, signature validation, body analysis, generation/expansion, lowering,
  export visibility, and final validation.
- Move only low-risk orchestration logic first; avoid changing semantic
  algorithms unless needed to isolate a pass.
- Add assertions or semantic tests that key generated helpers are available only
  after the intended pass.

Risks:

- This is higher complexity because many concerns are interwoven.
- Moving pass order can expose latent bugs in generated declarations or
  visibility checks.

Success outcomes:

- A new engineer can tell which phase owns a semantic concern.
- Future LSP can reuse early passes without requiring full lowering/emission.

Completion checklist:

- [ ] Analyzer pass order documented.
- [ ] Pass orchestration extracted or named clearly.
- [ ] No behavioral changes except intentional bug fixes.
- [ ] Full suite passes.
- [ ] Stage committed and this section struck through.

## 10. Begin A Backend ABI Surface Model

Summary: Introduce the first backend-neutral ABI/export surface model, starting
with declarations needed by C headers and metadata. This is the bridge toward
multi-language headers, multi-target shared-library headers, pluggable emitters,
MSIL, WASM, GCC/MSVC variants, and future async lowering.

Plan of action:

- Define an `AbiSurface` model for exported and private ABI-visible
  declarations: types, functions, variables, constants, callable signatures,
  expanded parameter lists, symbols, visibility, and target-specific modifiers.
- Populate it from the lowered/bound compilation without changing existing C
  output.
- Move a small, safe part of C header generation to consume `AbiSurface` first,
  preferably function prototypes or exported variables/constants.
- Keep implementation-body C emission on the existing tree for this stage.
- Add semantic tests that compare `AbiSurface` against expected exported C/API
  facts for a dense fixture with classes, structs, interfaces, callables,
  expanded forms, and constants.

Risks:

- This is the first step toward a larger backend IR and can grow too broad if
  allowed to include body lowering.
- Header output is sensitive; migrate one projection slice at a time.

Success outcomes:

- The compiler has an explicit ABI/export model separate from source semantics
  and C text emission.
- Future C++, C#, Swift, Rust, Java, TypeScript/WASM, MSIL, and alternate C
  emitters have a natural model to consume.

Completion checklist:

- [ ] Initial `AbiSurface` model introduced.
- [ ] C header generation consumes one safe slice of `AbiSurface`.
- [ ] Dense ABI semantic test added.
- [ ] Full suite passes.
- [ ] Stage committed and this section struck through.
