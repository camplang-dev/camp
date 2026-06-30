# Camp Conversion Semantics Implementation Proposal

Status: proposed
Source semantics document: `docs/camp_conversion_semantics_draft_v4.md`
Audience: implementation agents working in the Camp compiler
Final disposition: when fully implemented and verified, move this file to `docs/proposals/accepted/` with all completion criteria struck through.

## Summary

Implement the conversion model described by `docs/camp_conversion_semantics_draft_v4.md`: explicit `unsafe` casts, raw carrier types including `fn*`, fence-cast rules, target-defined typespec conversion policy, callable ABI-slot compatibility, and non-tunneling conversion behavior for arrays, delegates, optionals, and generic constructed types.

Diagnostics are part of the feature. When `unsafe` is required but missing, the compiler must explain the particular contract being broken, such as const removal, callable lifetime erasure, class downcast, interface slot fabrication, physical pointer-depth change, or target-domain narrowing. When a raw fence is required, the diagnostic should name the required fence family and briefly explain why a direct cast is not available. When `unsafe` is written but the cast is already implicit or ordinary explicit, the compiler should emit a warning and continue compilation.

This proposal intentionally takes priority over the compiler’s current partial conversion behavior. Current implementation already has useful pieces, including `TargetTypeSpecTypeReference`, target callspec/typespec parsing, `TypeShape`-based conversion helpers, and `DiagnosticSeverity.Warning`, but the conversion model is still mostly boolean (`CanImplicitlyConvert` / `CanExplicitlyConvert`) and therefore cannot express “explicit but not unsafe,” “unsafe,” “fence required,” “reconstruct,” or “forbidden” with high-quality diagnostics.

## Current Codebase Touchpoints

- Parser/model:
  - `src/Camp.Compiler/CampParser.cs`
  - `src/Camp.Compiler/SyntaxNode.cs`
  - `src/Camp.Compiler/BindableNode.cs`
  - `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs`
  - `src/Camp.Compiler/BindableNodeBuilder.cs`
- Type binding and target spec validation:
  - `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.TypeShapes.cs`
  - `src/Camp.Compiler/TargetCatalog.cs`
- Conversion and method-body analysis:
  - `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.cs`
  - `src/Camp.Compiler/CallableShapeService.cs`
  - `src/Camp.Compiler/BindableNodeAnalyzer.Callables.cs`
- Emission and serialization:
  - `src/Camp.Compiler/CCodeEmitter.cs`
  - `src/Camp.Compiler/BindableNodeCodeSerializer.cs`
  - `src/Camp.Compiler/MetadataJsonSerializer.cs`
  - `src/Camp.Compiler/CompilerXmlSerializer.cs`
- Diagnostics:
  - `src/Camp.Compiler/CompilerDiagnostic.cs`
  - `src/Camp.Compiler/CompilerDriver.cs`
  - `src/Camp.Compiler.TestRunner/*`
- Target metadata:
  - `targets/*.ini`
  - especially `targets/msvc-win16-x86.ini` for `_near`, `_far`, and `_huge` emitter-only coverage.

## Implementation Principles

1. Preserve source-level intent. Do not silently convert casts that should require `unsafe`, a raw fence, or reconstruction.
2. Value conversions happen only at value boundaries. They must not tunnel through generic arguments, array element types, delegate signatures, or function signatures unless the document explicitly allows ABI-slot compatibility.
3. `untyped` is the universal scalar fence. `untyped*` remains a pointer to `untyped` storage, not the universal fence.
4. `void*` is the data-pointer fence. `fn*` is the function-pointer fence. They are not interchangeable except through `untyped` or a target-defined escape.
5. Target-defined typespec conversion policy must be explicit enough to distinguish implicit, explicit, unsafe, fence-only, and forbidden conversions.
6. Diagnostics should be short, source-ranged, and specific. Prefer one precise diagnostic to a cascade.
7. Tests should be dense: cover multiple matrix cells in one fixture where that remains readable.
8. `_near`, `_far`, and `_huge` tests should stop at parser/API/lowering/CEmit unless a future target lane can compile and run them.

## Stage 1: Syntax, Model, Diagnostics, And Warning Plumbing

Goal: make the new surface representable and make warnings real compiler outputs that do not block compilation.

### Implementation

- Reserve `unsafe` as a Camp keyword.
- Extend cast syntax to accept:
  - `(unsafe T)value`
  - `(unsafe scoped(anchor) T*)value` where existing lifetime/type cast grammar supports the combined type.
  - `(unsafe)value` should be rejected unless the semantics document later adds a standalone unsafe assertion form.
- Add an `Unsafe` marker to `CastExpressionSyntax` / `CastExpression`, preserving source range for both the `unsafe` keyword and the cast as a whole.
- Add a first-class raw function pointer type, likely `RawFunctionPointerTypeReference`, for `fn*`.
  - `fn*` may carry a target typespec: `fn* _far`.
  - `fn*` may not carry a callspec: `fn* _stdcall` must be an error.
  - `fn*` is not callable.
- Validate target specifier placement from the semantics document:
  - concrete callable types may carry callspecs and function-pointer typespecs;
  - `void*`, `fn*`, `nint`, `nuint`, and expanded carriers may carry typespecs where target-defined;
  - `untyped` may carry neither typespec nor callspec;
  - callspecs on non-callable raw carriers are errors.
- Preserve the new syntax in:
  - AST/XML tests;
  - lowering serialization;
  - Camp API output;
  - metadata JSON, where `fn*` and target specifiers appear in source-level type strings.
- Wire diagnostic severity properly:
  - `ParseDiagnostic`, `BindDiagnostic`, and `AnalysisDiagnostic` already have severity, but driver printing and success gating currently need audit.
  - warnings print as `warning:` and do not make `CompilationPipeline` or `CompilerDriver` fail.
  - golden diagnostics support expected warnings.
  - command-line build returns success when only warnings are present.
- Add a helper such as `Warn(...)` next to existing analyzer `Report(...)`.

### Diagnostics

- `unsafe` in a non-cast location: `Keyword 'unsafe' is only valid in cast syntax.`
- Callspec on `fn*`: `Callspec '_stdcall' cannot be applied to 'fn*'; use a concrete fn type.`
- Callspec on `void*`/`nint`/`nuint`: `Callspec '_stdcall' cannot be applied to data-pointer or integer carrier type.`
- Typespec/callspec on `untyped`: `Raw carrier 'untyped' cannot have target specifiers.`
- Calling `fn*`: `Raw function pointer 'fn*' is not callable; cast it to a concrete fn type first.`

### Tests

- Dense parser/API/metadata fixture covering:
  - `(unsafe T*)value`;
  - `fn*`, `fn* _far`, and concrete `fn _far _pascal nint()`;
  - `void* _far`, `nint _far`, `int[] _near`;
  - rejected `fn* _stdcall`, `void* _stdcall`, `untyped _far`.
- Diagnostics fixture proving warnings do not fail compilation.
- Command-line smoke: a source file that emits an unnecessary `unsafe` warning in a later stage must still produce an artifact.

### Completion Criteria

- [x] ~~`unsafe` is reserved and parsed only in cast syntax.~~
- [x] ~~`CastExpression` preserves an unsafe marker and source range.~~
- [x] ~~`fn*` is represented distinctly from `fn ...` and from `void*`.~~
- [x] ~~Target specifier placement rules from section 1 of the semantics document are enforced.~~
- [x] ~~Warnings print as warnings, not errors, and do not prevent successful build/lowering/emission.~~
- [x] ~~AST/API/metadata/diagnostics tests cover the new syntax and invalid specifier placement.~~
- [x] ~~Full non-skipped suite passes and the stage is committed.~~

## Stage 2: Conversion Classifier And Scalar/Data-Pointer Rules

Goal: replace boolean conversion answers with a classifier capable of producing correct cast levels and diagnostics for data pointers, integer carriers, `void*`, `untyped`, const/volatile, lifetimes, and physical pointer depth.

### Implementation

- Introduce a central conversion classifier, for example:
  - `Implicit`
  - `Explicit`
  - `Unsafe`
  - `FenceRequired`
  - `ReconstructRequired`
  - `Forbidden`
- Include a short reason enum/string:
  - `ConstRemoval`
  - `VolatileRemoval`
  - `PhysicalDepthChange`
  - `ClassDowncast`
  - `ClassSidecast`
  - `InterfaceSlotFabrication`
  - `TargetDomainNarrowing`
  - `FamilyCrossing`
  - `ArrayElementRewrite`
  - `DelegateSignatureRewrite`
  - etc.
- Keep compatibility wrappers for existing call sites:
  - `CanImplicitlyConvert` maps to classifier `Implicit`.
  - `CanExplicitlyConvert` maps to `Implicit` or `Explicit` only, not `Unsafe`.
  - cast analysis checks ordinary explicit casts against `Explicit`, and `unsafe` casts against `Unsafe` or lower.
- Implement overriding rules from section 5:
  - adding `const`/`volatile` is implicit when the rest is valid;
  - removing `const`/`volatile` through typed pointer/`void*`/`fn*` requires `unsafe`;
  - changing data-pointer lifetime remains ordinary explicit;
  - changing physical pointer depth through typed pointer/`void*` requires `unsafe`;
  - `nint`, `nuint`, and `untyped` discard qualifier/lifetime/depth information according to the semantics document.
- Implement data pointer family rules:
  - primitive/value-newtype pointer to primitive/value-newtype pointer: explicit at same physical depth;
  - struct pointer to struct pointer: explicit at same physical depth;
  - cross-family data pointer casts require matching-depth `void*` fence or `untyped`;
  - class upcast remains implicit;
  - class downcast or sidecast with equal constructed base requires `unsafe`;
  - unrelated class casts require `void*` or `untyped` fence;
  - interface pointer physical depth rules are represented and diagnosed.
- Implement scalar raw carrier rules:
  - data pointer to/from compatible `nint`/`nuint` is explicit, not unsafe;
  - function pointer to `nint`/`nuint` is invalid; use `fn*` or `untyped`;
  - pointer/function/integer to `untyped` is explicit;
  - `untyped` to representable scalar/pointer/function target is explicit with no additional `unsafe`.
- Emit warning when `unsafe` is written for an implicit or ordinary explicit conversion:
  - `warning: unsafe is not required for this cast; the conversion is ordinary explicit.`

### Diagnostics

- Missing unsafe for const removal: `Cast removes const; write '(unsafe T*)' to acknowledge mutable access.`
- Missing unsafe for physical depth: `Cast changes pointer indirection depth; write 'unsafe' or use a matching-depth fence.`
- Missing fence for family crossing: `Cannot directly cast 'byte*' to 'PacketHeader*'; cast through 'void*' to erase the data-pointer family.`
- Missing fence for unrelated classes: `Classes 'FileHandle' and 'EditControl' do not share a constructed base; use a raw fence before casting.`
- Interface slot fabrication: `Cast would invent an interface slot for 'IEditable'; use an unsafe raw fence or an explicit conversion helper.`

### Tests

- Dense CCompile/Diagnostics matrix:
  - const add/remove through typed pointer and `void*`;
  - lifetime cast remains explicit;
  - same-family primitive and struct pointer explicit casts;
  - cross-family pointer fence required;
  - class upcast/downcast/sidecast/unrelated fence;
  - interface physical-depth examples;
  - data pointer to/from `nint`/`nuint`;
  - pointer/function/integer to/from `untyped`.
- Warning tests:
  - unnecessary `unsafe` on numeric cast;
  - unnecessary `unsafe` on data pointer to `nuint`;
  - unnecessary `unsafe` on ordinary same-family struct pointer cast.

### Completion Criteria

- [ ] A central classifier reports conversion level and reason.
- [ ] Existing implicit-conversion behavior uses the classifier without broad regressions.
- [ ] Ordinary casts reject conversions that require `unsafe`.
- [ ] `unsafe` casts accept unsafe conversions and warn when unnecessary.
- [ ] Fence-required and reconstruct-required diagnostics name the needed strategy.
- [ ] Data-pointer, class, interface, integer-carrier, and `untyped` tests cover positive and negative cases.
- [ ] Full non-skipped suite passes and the stage is committed.

## Stage 3: Target-Defined Typespec Conversion Policy

Goal: make target-specific conversion policy explicit and apply it at value boundaries without tunneling through constructed types.

### Implementation

- Extend target metadata beyond the current `TypeSpecOrder` widening heuristic.
  - Keep existing simple order as a compatibility shorthand if useful, but lower it into explicit policy during target loading.
  - Add explicit conversion sections. Proposed shape:
    ```ini
    [conversion.data_pointer]
    _near->_far=implicit
    _far->_near=unsafe
    _rom->_ram=fence

    [conversion.function_pointer]
    _near->_far=explicit

    [conversion.nint]
    _near->_far=implicit

    [conversion.abi_slot]
    _near->_far=compatible
    ```
  - Allowed levels: `implicit`, `explicit`, `unsafe`, `fence`, `forbidden`, plus `compatible` for ABI slot checks where appropriate.
- Validate target metadata:
  - source and target spec names must exist;
  - levels must be known;
  - carrier names must be supported;
  - callspecs must not appear in typespec conversion sections.
- Apply typespec policy by carrier:
  - data pointer value conversions;
  - `void*` fences by data-pointer domain;
  - `fn*` fences by function-pointer domain;
  - `nint`/`nuint` carrier conversions;
  - expanded array carrier conversions such as `int[] _near` where the array carrier components move together.
- Preserve non-tunneling:
  - `byte* _near -> byte* _far` may be implicit;
  - `(byte* _near)[] -> (byte* _far)[]` remains invalid/reconstruct;
  - `Container<byte* _near>* -> Container<byte* _far>*` follows class/generic invariance;
  - delegate/function signatures do not become compatible unless ABI-slot policy says so in Stage 4.
- Add or update a target fixture for tests using `_near`, `_far`, `_huge`, and possibly synthetic `_rom`/`_ram`.
  - For `_near`/`_far`/`_huge`, stop at CEmit/API/lowering on current test lanes.
  - Do not require C compilation or execution for those domains until a real target lane exists.

### Diagnostics

- Target-domain narrowing: `Cast from '_far' to '_near' data pointer requires unsafe for target 'msvc-win16-x86'.`
- Fence-only domain: `Target does not define a typed conversion from '_rom' to '_ram'; use 'untyped' for a raw escape.`
- Non-tunneling: `Typespec value conversions do not apply to array element types; reconstruct the array.`
- Bad target metadata: `Target conversion '_near->_missing' references unknown typespec '_missing'.`

### Tests

- Target-catalog tests for conversion metadata validation and inherited target behavior.
- CEmit/API tests using `msvc-win16-x86`:
  - `_near -> _far` implicit data pointer;
  - `_far -> _near` unsafe data pointer;
  - `void* _far` fence recovery;
  - `fn* _far` syntax and emission only;
  - `int[] _near` expanded carrier emission.
- Diagnostics tests:
  - no tunneling through array element types;
  - no tunneling through generic arguments;
  - callspec used where typespec conversion policy is expected.

### Completion Criteria

- [ ] Target metadata can express conversion levels per carrier.
- [ ] Existing targets continue to load; current behavior is either preserved or deliberately updated.
- [ ] Typespec conversions apply at value boundaries only.
- [ ] Array/generic/delegate/function signature tunneling is rejected with specific diagnostics.
- [ ] `_near`/`_far`/`_huge` coverage reaches CEmit/API/lowering without compile/run requirements.
- [ ] Full non-skipped suite passes and the stage is committed.

## Stage 4: Callable, `fn*`, Callable Newtype, And Delegate Rules

Goal: implement the function-pointer side of the semantics: `fn*`, direct callable compatibility, callspec handling, callable lifetime unsafe casts, and delegate invariance.

### Implementation

- Make `fn*` a scalar raw function-pointer carrier:
  - concrete function/callable target to `fn*`: explicit;
  - `fn*` to concrete function type: explicit;
  - `fn*` to `untyped` and `untyped` to concrete function type: explicit;
  - `fn*` is not callable.
- Function/data crossing:
  - function pointer to `void*` requires `unsafe`;
  - prefer `fn*` or `untyped` diagnostics where appropriate;
  - `void*` to function pointer should require `unsafe` or `untyped` according to target family policy.
- Direct callable-to-callable conversion:
  - same concrete anonymous callable type: implicit/explicit depending on context;
  - widening-compatible anonymous callable type: implicit when no nominal boundary is crossed;
  - anonymous callable to compatible callable newtype: explicit unless existing assignment/ascription rules intentionally permit it;
  - same carrier but not widening-compatible: `unsafe`, unless target requires fence;
  - function-pointer target typespec changes: `fn*` or `untyped` fence unless target ABI-slot policy allows direct compatibility.
- Implement ABI-slot-compatible callable signature checks:
  - callspec identical or target-compatible;
  - input parameters contravariant over ABI-compatible slots;
  - return/out/produced values covariant over ABI-compatible slots;
  - no inserted value conversions;
  - callable lifetime annotations unchanged for ordinary compatibility.
- Callable lifetime changes:
  - direct cast changing callable lifetime annotation requires `unsafe`;
  - after `fn*`/`untyped`, recovery is ordinary explicit.
- Callable newtypes:
  - preserve nominal boundaries;
  - ascription compatibility uses the same widening-compatible direct signature logic;
  - C/API/metadata should show `fn*` and concrete callable types correctly.
- Delegates and other multivalue callable values:
  - delegate values remain invariant;
  - no whole-delegate cast to `fn*`, `nint`, or `untyped`;
  - diagnostics recommend reconstructing delegate or operating on explicit `call`/`context` components.

### Diagnostics

- `fn*` call: `Raw function pointer 'fn*' is not callable; cast to a concrete fn type first.`
- Function to data pointer: `Function pointer to data pointer conversion requires unsafe; use 'fn*' to erase only the function signature.`
- Callable lifetime change: `Cast changes callable lifetime contract; write 'unsafe' to acknowledge the hidden context/result lifetime change.`
- Signature rewrite: `Callable signatures are not ABI-slot compatible; use an unsafe cast or an 'fn*' fence.`
- Delegate invariant: `Delegate values cannot be cast to 'fn*'; rebuild the delegate or cast its call component.`

### Tests

- CCompile:
  - `fn int(int)` to `fn*` and back;
  - changed signature through `fn*`;
  - `untyped` universal function fence;
  - concrete callable widening compatibility for const input/output;
  - unsafe callable lifetime change.
- Diagnostics:
  - `fn*` call;
  - function pointer to `nint`/`nuint`;
  - function pointer to `void*` without `unsafe`;
  - direct incompatible callable signature without `unsafe`/fence;
  - delegate whole-value cast to `fn*`.
- CEmit-only target-spec callable tests:
  - `fn* _far`;
  - `fn _far _pascal nint()`;
  - ABI-slot incompatible `_near`/`_far` function signatures.

### Completion Criteria

- [ ] `fn*` conversion and emission works as the function-pointer fence.
- [ ] Direct callable compatibility uses ABI-slot variance, not ordinary value conversion.
- [ ] Callable lifetime changes require `unsafe`.
- [ ] Delegate values remain invariant and cannot be cast as whole values to scalar raw carriers.
- [ ] Callable diagnostics distinguish unsafe-required from fence-required from reconstruct-required.
- [ ] Full non-skipped suite passes and the stage is committed.

## Stage 5: Multivalue Hardening, Standard Library Migration, Docs, And Final Audit

Goal: finish the non-tunneling surfaces, update affected code/tests/docs, and verify the implementation against the semantics document end to end.

### Implementation

- Arrays:
  - array types are invariant;
  - adding const to element view remains implicit;
  - removing const from element view requires `unsafe`;
  - array carrier typespec conversion follows target policy;
  - element type/element typespec/family rewrites require reconstruction;
  - diagnostics mention `.elements` and `.length` reconstruction when helpful.
- Optionals:
  - `T? -> U?` is allowed only when payload conversion exists at the same or lower safety level;
  - optional payload conversions preserve `.specified`;
  - no optional cast when payload would require reconstruction.
- Generic constructed types:
  - constructed generic types are invariant;
  - class pointer rules can use exact constructed bases;
  - hidden `sizeof(T)` / `vtableof(T: I)` capabilities do not change cast category.
- Standard library and tests:
  - update any casts that now require `unsafe`, a fence, or reconstruction;
  - remove casts that now produce unnecessary-unsafe warnings unless the test is intentionally checking the warning;
  - keep stdlib code idiomatic and avoid `unsafe` where a typed helper or reconstruction is clearer.
- Metadata and serialization:
  - metadata output includes enough source-level type information to distinguish `fn*`, concrete `fn`, `unsafe` cast syntax where expressions are emitted, target specs, and callspecs;
  - API headers preserve source-level `fn*` and target specifier spelling where relevant.
- Documentation:
  - keep `docs/camp_conversion_semantics_draft_v4.md` in the repo as the authoritative semantics document unless it is renamed by a later instruction;
  - update the unified spec with programmer-facing conversion levels and examples;
  - update grammar docs for `unsafe` casts and `fn*`;
  - update `extras/CAMP_LLM_CODE_GUIDE.md` with when to use ordinary explicit, unsafe, fence, or reconstruction;
  - update `extras/Camp.sublime-syntax` for `unsafe` and `fn*`;
  - update metadata supplement if type metadata changes.
- Final audit:
  - re-read `docs/camp_conversion_semantics_draft_v4.md`;
  - map each section to implementation/tests;
  - add missing tests before completion;
  - move this proposal to `docs/proposals/accepted/` after all criteria are struck through.

### Diagnostics

- Array rewrite: `Array casts cannot change element type; reconstruct the array with explicit elements and length.`
- Optional payload reconstruct: `Optional payload conversion would require reconstruction; rebuild the optional value.`
- Generic invariance: `Generic arguments are invariant; value conversions do not rewrite 'Container<T>' arguments.`
- Hidden capability: `Generic capability 'sizeof(T)' does not make this cast valid; use a fence or helper conversion.`

### Tests

- Dense diagnostics fixture:
  - array element rewrite;
  - optional payload rewrite;
  - delegate signature rewrite;
  - generic argument rewrite;
  - hidden `sizeof(T)`/`vtableof` does not enable cast.
- CCompile/StdRun:
  - valid array const add/remove with `unsafe`;
  - optional payload numeric conversion;
  - stdlib affected APIs still compile and run.
- Metadata/API:
  - `fn*`, target specs, callspecs, and raw carriers preserve source-level spelling.
- Final validation lanes:
  - macOS full non-skipped suite;
  - Windows/MSVC full non-skipped suite;
  - Linux/WSL full non-skipped suite;
  - CEmit-only target tests for `_near`, `_far`, `_huge`.

### Completion Criteria

- [ ] Arrays, optionals, delegates, and generics obey non-tunneling/reconstruct rules.
- [ ] Standard library and existing tests are updated for `unsafe`, fences, or reconstruction.
- [ ] Metadata/API/grammar/spec/LLM guide/Sublime syntax are updated.
- [ ] Final audit maps every section of `camp_conversion_semantics_draft_v4.md` to implementation or tests.
- [ ] No `.actual` files remain.
- [ ] macOS, Windows/MSVC, and Linux/WSL full non-skipped suites pass.
- [ ] This proposal is moved to `docs/proposals/accepted/` with completed criteria struck through.
- [ ] Final stage is committed.

## Suggested Diagnostic Style Guide

Use one-line messages where possible:

- `Cast removes const; write 'unsafe' to acknowledge mutable access.`
- `Cast changes pointer indirection depth; use 'unsafe' or a matching-depth raw fence.`
- `Cannot directly cast between data-pointer families; cast through 'void*' first.`
- `Callable cast changes lifetime contract; write 'unsafe' to acknowledge hidden context/result lifetime changes.`
- `Array casts cannot change element type; reconstruct the array.`
- `Generic arguments are invariant; value conversions do not rewrite constructed generic types.`
- `unsafe is not required for this cast; the conversion is ordinary explicit.`

When a target policy is involved, name the target and typespecs:

- `Target 'msvc-win16-x86' requires unsafe for '_far' to '_near' data-pointer conversion.`
- `Target 'example-rom' does not define a typed conversion from '_rom' to '_ram'; use 'untyped' for a raw escape.`

## Risk Notes

- Warning support is likely to expose hidden assumptions because `AnalysisResult.Success` and driver diagnostic printing currently need severity-aware behavior.
- The current `CanImplicitlyConvert` / `CanExplicitlyConvert` boolean API is deeply used. The safest migration is to add a classifier and keep wrappers until all call sites are deliberately converted.
- `untyped` currently appears in pointer-oriented helper names such as `IsUntypedPointerType`; the semantics document distinguishes `untyped` from `untyped*`. This is a likely source of regressions and should be audited early.
- Target-defined conversions should not be modeled only by ordered widening. The order heuristic cannot express unsafe, fence-only, forbidden, or carrier-specific rules.
- Callable compatibility already has recent `constof` variance work. The new callable ABI-slot compatibility should build on that service rather than fork another signature comparer.
- `_near` / `_far` / `_huge` C emission may reveal declarator formatting bugs. Keep those tests CEmit-only for now, per instruction.
