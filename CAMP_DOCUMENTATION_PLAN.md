# Camp Documentation Reorganization Plan

This plan describes how to turn the current Camp documentation set into a
version 1 documentation system. The goal is not to invent new language
semantics. The goal is to preserve every current semantic detail, verify it
against the compiler where possible, and publish it in a shape that is useful to
language users, compiler writers, compiler contributors, and LLM coding agents.

## Goals

- Produce user-facing language documentation with an index and one Markdown file
  per top-level section.
- Produce a compiler supplement for `campc`, build files, packages, targets,
  artifacts, metadata JSON, and related tooling behavior.
- Produce compiler-writer semantic supplements for details that are critical to
  implementation correctness but too deep for ordinary Camp users.
- Produce one compiler development guide for people and agents modifying this
  repository.
- Produce one LLM-focused Camp coding guide that can be paired with standard
  library metadata to generate correct Camp code.
- Produce a proposals area containing proposals that are rejected or not yet
  implemented.
- Remove historical narrative from normative docs. Version 1 docs should say
  what Camp is, not what it used to be.
- Preserve the current `docs/` folder by moving it to `archive/docs/` before
  creating the new `docs/` tree. Do not delete or overwrite the existing docs.
- Keep private machine-specific setup context out of the repository. Put that
  context under `local/`, omit `local/` from the repo, and provide those files
  separately to LLMs working on this machine.

## Proposed Documentation Tree

```text
docs/
  language/
    index.md
    01-overview.md
    02-lexical-structure.md
    03-namespaces-imports-visibility.md
    04-declarations.md
    05-type-system-overview.md
    06-primitive-values-and-literals.md
    07-pointers-qualifiers-and-conversions.md
    08-functions-and-callables.md
    09-expressions-and-operators.md
    10-statements-and-control-flow.md
    11-structs-classes-and-lifecycle.md
    12-interfaces-and-dispatch.md
    13-enums-newtypes-and-inline-constants.md
    14-arrays-slices-optionals-and-strings.md
    15-errors-thrown-and-cleanup.md
    16-lifetimes-allocation-and-within.md
    17-generics-and-type-capabilities.md
    18-iterators-foreach-and-generators.md
    19-async-await-and-postpone.md
    20-attributes-and-doc-comments.md
    21-standard-library-and-interop.md
  compiler/
    index.md
    01-campc-command-line.md
    02-build-files-and-pragmas.md
    03-package-system.md
    04-targets-and-native-builds.md
    05-artifacts-cache-and-output-layout.md
    06-metadata-json.md
    07-dumps-diagnostics-and-introspection.md
    08-language-server-and-editor-tooling.md
    09-standard-library-build-integration.md
  semantics/
    index.md
    01-binding-analysis-and-lowering-pipeline.md
    02-expanded-forms-and-abi-shapes.md
    03-conversions-raw-carriers-and-fence-casts.md
    04-constof-and-signature-compatibility.md
    05-lifetime-analysis-and-flow-facts.md
    06-generics-erasure-and-capabilities.md
    07-callable-lowering-and-context-ownership.md
    08-async-resumption-lowering.md
    09-interface-vtables-and-dynamic-dispatch.md
    10-construction-destruction-and-allocation.md
    11-metadata-api-surface-and-symbols.md
    12-target-capabilities-and-c-emission.md
    13-diagnostics-source-ranges-and-error-quality.md
  compiler-development-guide.md
  camp-llm-coding-guide.md
  OutstandingIssues.md
  proposals/
    accepted/
    pending/
    rejected/
archive/
  docs/
local/
  README.md or machine-context.md
```

Number prefixes are intentional. They make the reading order stable in plain
file listings and static-site generators. Cross-references should use relative
links, not generated HTML URLs.

`local/` must be ignored by git. It may contain paths, installed tool locations,
machine-specific setup notes, local shell aliases, and other context useful to
LLMs on this machine. No private details about this specific machine should be
written into committed docs.

The existing `docs/proposals/accepted/` directory should be treated as source
material during the rewrite. For the final public documentation shape, accepted
and implemented proposals should be consumed into normative docs, not treated as
active proposals. If keeping historical proposal files in the repository is
useful, keep them as historical accepted proposal files or in a clearly named
archive.

Going forward, new accepted proposals should remain in
`docs/proposals/accepted/` after acceptance. They should not be updated after
acceptance except to add the acceptance metadata at the time of acceptance.
Their accepted rules must be integrated into canonical docs at acceptance time,
and the proposal should state that the canonical source is now the docs, not
the historical proposal.

## Source Inventory

Primary source documents:

- `docs/camp_unified_spec_v38.md`: main language source. It currently covers
  basic types, data structures, statements and expressions, lifetime, advanced
  features, generics, and standard library notes.
- `docs/camp_declarations_statements_grammar.txt`: declaration, type,
  preprocessor, and statement grammar.
- `docs/camp_expressions_grammar.txt`: expression grammar, precedence surface,
  casts, construction, lambdas, and argument forms.
- `docs/camp_conversion_semantics_draft_v4.md`: conversion levels, `unsafe`,
  raw carriers, target specs, callable conversions, arrays, optionals, and
  generic constructed types.
- `docs/camp_doc_comments_metadata_supplement.md`: doc comments, metadata
  attributes, symbol links, metadata JSON shape, declaration fields, and
  consumer guidance.
- `docs/camp_async_scheduler_design_v7.md`: superseded historical async design.
  Use only to preserve still-current details after checking them against the
  unified spec, accepted resumption proposal, compiler code, and tests.
- `extras/CAMP_LLM_CODE_GUIDE.md`: current LLM guide and examples.
- `tests/README.md`: compiler test workflow and commit-gate expectations.
- `src/camp-lsp/README.md`: editor and language server setup.

Proposal source documents:

- Accepted/mostly implemented sources to mine into normative docs:
  `camp_constof_feature_proposal.md`,
  `camp_constof_conversions_signature_supplement.md`,
  `camp_conversion_semantics_unsafe_raw_carriers_proposal.md`,
  `camp_default_interface_methods_proposal.md`,
  `camp_inline_constants_and_fixed_enums_proposal.md`,
  `camp_lifetime_analysis_implementation_plan.md`,
  `camp_receiver_classtype_typenameof_proposal.md`,
  async/resumption proposals and implementation plans.
- Pending candidates to classify:
  `docs/proposals/camp_objc_interop_implementation_plan.md` appears pending
  because target capabilities mention Objective-C support but the source tree
  does not show a complete Objective-C syntax/emission pipeline.
- Implemented-or-mostly-implemented root proposal to mine, then classify:
  `docs/proposals/camp_artifact_cache_directory_layout_proposal.md`; current
  code contains `--out-dir`, cache/pkg layout, project references, static/shared
  dependency modes, artifact directories, and removed `--build-dir` diagnostics.
- Rejected proposals to keep under `docs/proposals/rejected/`:
  `camp_extern_class_nonextern_constructor_proposal.md` and
  `camp_interface_implementation_visibility_proposal.md`.

Compiler source to verify against:

- Command line and build files: `src/campc/Program.cs`,
  `src/Camp.Compiler/CompilerDriver.cs`,
  `src/Camp.Compiler/CampProjectLoader.cs`.
- Package system: `PackageCommands`, `PackageSpec`, `PackageSourceSpec`, and
  package-related request fields in `src/campc/Program.cs` and
  `src/Camp.Compiler/CompilerDriver.cs`.
- Artifact and target layout: `src/Camp.Compiler/BuildArtifactLayout.cs`,
  `src/Camp.Compiler/NativeBuildDriver.cs`,
  `src/Camp.Compiler/TargetCatalog.cs`, and `targets/**/*.ini`.
- Parser and grammar: `src/Camp.Compiler/CampTokenizer.cs`,
  `src/Camp.Compiler/CampParser.cs`, `src/Camp.Compiler/SyntaxNode.cs`,
  plus the grammar text files.
- Binding, semantic analysis, and lowering:
  `src/Camp.Compiler/BindableNodeAnalyzer*.cs`,
  `src/Camp.Compiler/BindableNodeBuilder*.cs`,
  `src/Camp.Compiler/BindableNodeExpander.cs`,
  `src/Camp.Compiler/BindableNodeLowerer.cs`,
  `src/Camp.Compiler/ExpandedFormService.cs`,
  `src/Camp.Compiler/CallableShapeService.cs`,
  `src/Camp.Compiler/GeneratedDeclarationFactory.cs`.
- Emission and metadata: `src/Camp.Compiler/CCodeEmitter.cs`,
  `src/Camp.Compiler/MetadataJsonSerializer.cs`,
  `src/Camp.Compiler/DocCommentTranslator.cs`,
  `src/Camp.Compiler/CompilerXmlSerializer.cs`,
  `src/Camp.Compiler/BindableNodeCodeSerializer.cs`.
- Tests and examples: `tests/Api`, `tests/Ast`, `tests/CCompile`,
  `tests/CEmit`, `tests/Declarations`, `tests/Diagnostics`,
  `tests/Lowering`, `tests/LoweringXml`, `tests/Metadata`, `tests/Std`,
  `tests/StdRun`, and semantic tests in
  `src/Camp.Compiler.TestRunner/SemanticTests.cs`.

## Preservation Strategy

Before writing final docs, create a temporary traceability table in `tmp/` with
one row per heading or grammar rule from the source docs:

```text
source file | heading/rule | target doc | target section | status | notes
```

Statuses should be:

- `copied`: detail moved directly into the target doc.
- `summarized`: detail preserved in shorter wording.
- `cross-referenced`: detail lives elsewhere and this section links to it.
- `semantic-supplement`: too deep for user docs and moved to `docs/semantics`.
- `compiler-supplement`: tooling or metadata detail moved to `docs/compiler`.
- `proposal-pending`: not current language behavior and moved to pending
  proposals.
- `proposal-rejected`: rejected design preserved as proposal history.
- `omitted-as-history`: old-change narrative intentionally dropped because it
  does not describe version 1 behavior.

Do not delete a semantic detail merely because it is obscure. Either preserve it
in a normative document, move it to a semantic supplement, or mark it as
historical/proposal material with a reason.

When documentation work uncovers uncertainty that cannot be resolved by reading
the current docs, reading compiler source, and running focused smoke tests, add
it to `docs/OutstandingIssues.md`. Number documentation issues in the same
style as compiler bugs so they can be triaged alongside `src/campc/OutstandingBugs.md`.
When smoke testing shows behavior that is certainly a compiler bug, log it in
`src/campc/OutstandingBugs.md` instead of burying it in documentation notes.

After drafting, audit the docs with searches for historical language:

```sh
rg -n "no longer|removed|previous|formerly|superseded|current compiler|not yet implemented" docs/language docs/compiler docs/semantics docs/*.md
```

Some phrases are appropriate in proposals and maybe in the compiler development
guide. They should not appear in the normative user language docs except where
the wording is intentionally "Camp does not support X because Y."

## User-Facing Language Documentation

Audience: programmers reading or writing Camp code. These docs should explain
the language in a learnable order, with short examples, precise rules, and links
to deeper details.

Do include:

- Current language syntax and semantics.
- Short, idiomatic Camp examples.
- User-visible diagnostics and restrictions when they affect how code should be
  written.
- Cross-references to compiler and semantic supplements for implementation-only
  details.
- A minimal standard library overview only where useful for explaining language
  behavior or common examples.

Do not include:

- Lowered C shapes, ABI slot order, generated helper details, or compiler pass
  internals except as brief intuition.
- Historical migration narrative from older Camp designs.
- Proposal staging checklists.
- Exhaustive standard library API reference. Avoid documenting APIs merely
  because they exist; otherwise the language docs will churn whenever stdlib
  APIs change.

### `docs/language/index.md`

Include:

- One paragraph describing this as the Camp language reference.
- A table of contents listing the 21 top-level files and their H2 subsections
  only.
- No detailed semantics. The index is navigation, not a mini-spec.

Sources:

- Generated from the H1/H2 headings of the user docs after drafting.

### 1. `01-overview.md`

Subsections:

- What Camp Is
- Language Goals
- Language Non-Goals
- Who Camp Is For
- Who Camp Is Not For
- Novel Aspects
- Influences And Borrowed Ideas
- Source Files And Compilation Units
- A Minimal Program
- Documentation Map
- Syntax Used In Examples

Include:

- Basic orientation to Camp as a compiled language targeting C/native builds.
- A discussion of Camp's goals, non-goals, intended audience, non-audience,
  novel aspects, and borrowed ideas from other languages.
- A clear explanation of where Camp is deliberately different from C, C++,
  C#, Rust, Swift, and similar systems languages, without turning the overview
  into a comparison matrix.
- A minimal `main` or simple exported function example.
- Explain that more advanced concepts are introduced later.
- Link to compiler supplement for `campc`.

Do not include:

- Full CLI usage.
- Package/target metadata.
- Deep type-system rules.

Sources:

- Unified spec overview implied by all sections.
- `src/campc/Program.cs` for the existence of `build` and `run`, linked but
  not explained here.
- Simple examples from `tests/CCompile/basic_functions.camp` or equivalent.

### 2. `02-lexical-structure.md`

Subsections:

- Files, Whitespace, And Comments
- Identifiers And Keywords
- Literals
- Strings And Characters
- Preprocessor Directives
- Grammar Notation

Include:

- Token-level rules and examples.
- Keywords that matter to the grammar.
- Literal forms, numeric/string/character notes, and reserved identifiers.
- `#define`, `#undef`, `#if`, `#elif`, `#else`, `#endif`, `#within`, and
  `#build` at syntax level only.

Do not include:

- Detailed build option semantics for `#build`; link to compiler supplement.
- Metadata doc-comment lowering; link to section 20.

Sources:

- Grammar text files.
- `CampTokenizer.cs`, `NumericLiteralParser.cs`, `CampPreprocessor.cs`.
- Diagnostics tests for literals and reserved identifiers.

### 3. `03-namespaces-imports-visibility.md`

Subsections:

- Qualified Names
- `using`
- `export as`
- Visibility Keywords
- Source Symbols And ABI Symbols
- Name Lookup Summary

Include:

- Namespace qualification, imports, aliases in `using`, namespace export.
- `export`, `public`, `extern`, and private/default visibility as user-visible
  source concepts.
- `@symbol` as the user-visible ABI-name override, with a link to metadata/API
  details.

Do not include:

- Internal `SymbolNameService` algorithms or generated symbol collision logic
  beyond user-facing effects.

Sources:

- Unified spec 5.1.
- Tests: `public_visibility`, `namespace_export`, `symbol_override`,
  `alias_api`, `primitive_flattened_symbols`.
- `SymbolNameService.cs`, `BindableNodeAnalyzer.ExportVisibility.cs`.

### 4. `04-declarations.md`

Subsections:

- Declaration Forms
- Declaration Grammar Summary

Include:

- Top-level and member declarations.
- Function, type, alias, field, enum-value, parameter, and member declaration
  shapes at a syntax level.
- A short forward reference to the later attributes and doc-comment section.

Do not include:

- Full metadata JSON schema.
- Attribute and documentation-comment semantics.

Sources:

- Grammar text files.
- Declaration sections of the unified spec.
- Parser and bindable-node declaration code.

### 5. `05-type-system-overview.md`

Subsections:

- Type Categories
- Type Spelling
- Value, Reference, And Expanded Forms
- Type Qualifiers
- Target Specifiers And Call Specifiers
- When Types Are Nominal

Include:

- A high-level map of primitives, pointers, arrays, optionals, callables,
  structs, classes, interfaces, enums, newtypes, generics, and expanded forms.
- Short definitions only. Link to later sections for detailed behavior.

Do not include:

- Conversion matrices, lowering rules, or ABI shapes.

Sources:

- Unified spec 1 and 2.
- Grammar `type`.
- `BindableNodeAnalyzer.TypeBinding.cs`, `ExpandedFormService.cs`.

### 6. `06-primitive-values-and-literals.md`

Subsections:

- Integer Types
- Natural Integer Types
- Floating-Point Types
- Boolean Values
- Character Types
- `void`, `default`, `null`, And `untyped`
- Literal Conversion Rules

Include:

- Primitive type list and meaning.
- Target-dependent natural integer width at user level.
- Literal typing and common conversion behavior.

Do not include:

- Target INI schema except a link to the compiler target supplement.
- Raw-carrier conversion deep rules except a link to section 7 and semantics.

Sources:

- Unified spec 1.1.
- Conversion draft for `nint`, `nuint`, and `untyped`.
- `NumericLiteralParser.cs`, target INI `ctype`, `nint`, and `pointer` data.
- Primitive/literal tests.

### 7. `07-pointers-qualifiers-and-conversions.md`

Subsections:

- Pointer Types
- `const`, `volatile`, And `constof`
- Target Type Specifiers
- Implicit And Explicit Conversions
- `unsafe` Casts
- Raw Carriers: `void*`, `fn*`, `nint`, `nuint`, `untyped`
- Conversion Limits And Reconstruction

Include:

- User-facing conversion categories and cast syntax.
- `constof(anchor)` as source-level behavior.
- Practical examples for common pointer, callable, array, optional, and raw
  carrier conversions.
- Link to semantic supplements for full classifier and signature compatibility.

Do not include:

- Full conversion classifier tables if they are mainly for compiler writers.
- ABI slot compatibility details.

Sources:

- Unified spec 1.2 and 1.12.
- Conversion draft.
- `constof` proposal and supplement.
- Tests: `conversion_*`, `constof_*`, `bool_numeric_conversions`,
  `raw_function_pointer_stage1`.
- `BindableNodeAnalyzer.MethodBody.cs`, `BindableNodeAnalyzer.ConstOf.cs`.

### 8. `08-functions-and-callables.md`

Subsections:

- Function Declarations
- Parameters And Arguments
- Default Arguments
- Named Arguments
- Receiver Parameters
- Callable Types
- Delegates, `once`, And Function Pointers
- Callable Newtypes
- Lambdas

Include:

- Function syntax, parameter modifiers (`in`, `out`, `thrown`, `overload`,
  `within` by link), receiver forms, default and named arguments.
- Differences between `fn`, `delegate`, `once`, and callable `newtype`.
- Lambda forms and target typing at user level.

Do not include:

- Context layout, deletion thunks, ABI parameter order, or lowered calls.

Sources:

- Unified spec 1.3, 1.4, 1.8, 5.5.
- LLM guide callable and lambda sections.
- Tests: callable ascription, delegate, lambda, default arguments, overloads.
- `CallableShapeService.cs`, `BindableNodeAnalyzer.Callables.cs`.

### 9. `09-expressions-and-operators.md`

Subsections:

- Expression Grammar
- Operator Precedence
- Calls, Indexing, And Member Access
- Casts
- Construction Expressions
- Initializer Lists
- `sizeof`, `vtableof`, `typenameof`, And `symbolof`
- Target Typing

Include:

- Expression forms and operator summary.
- Short examples for calls, indexing, member access, casts, array literals,
  initializer lists, and special expressions.
- `symbolof` only as metadata-attribute expression.

Do not include:

- Statement control flow.
- Lowering of special expressions.

Sources:

- Expression grammar file.
- Unified spec 3.2 through 3.6 and relevant advanced sections.
- Receiver/classtype/typenameof proposal.
- Tests: typed initializers, `typenameof`, `vtableof`, `sizeof`, member access.

### 10. `10-statements-and-control-flow.md`

Subsections:

- Blocks And Empty Statements
- Local Declarations
- `if`, `while`, `do`, And `for`
- `switch`, `case`, And `default`
- `break`, `continue`, `goto`, And Labels
- `return` And `yield`
- `foreach`
- Statement Conditions

Include:

- Statement syntax and user-visible behavior.
- Keep `foreach` as a short introduction and link to iterator details.
- Keep `yield` as a short introduction and link to iterator details.

Do not include:

- Iterator protocol lowering.
- Lifetime flow analysis details.

Sources:

- Declaration/statement grammar file.
- Unified spec 3.1, 3.8, 5.2.
- Tests: `switch_statement`, `foreach_*`, iterator tests.

### 11. `11-structs-classes-and-lifecycle.md`

Subsections:

- Structs
- Classes
- Fields And Static Fields
- Methods And Receivers
- Constructors
- Destructors
- `init`, `new`, And `delete`
- Abstract, Virtual, Override, Sealed, And Extern Types

Include:

- User-visible object model for structs and classes.
- Lifecycle syntax and default behavior.
- Allocation surface linked to lifetimes and `within`.
- Extern class restrictions as current rules, not rejected-proposal history.

Do not include:

- Generated constructor/destructor helper names.
- C layout details except user-visible ordering/compatibility notes.

Sources:

- Unified spec 2.1 through 2.3 and 4.4.
- Rejected extern constructor proposal only to ensure the current rule is clear.
- Tests: lifecycle, virtual, extern class, static fields, field layout.
- `BindableNodeAnalyzer.ClassType.cs`,
  `BindableNodeAnalyzer.DeclarationValidation.cs`.

### 12. `12-interfaces-and-dispatch.md`

Subsections:

- Interface Declarations
- Implementing Interfaces
- Interface Inheritance
- Required, Defaulted, And Optional Methods
- Interface Calls
- Interface Conversions
- `vtableof`

Include:

- User-level rules for interfaces and dispatch.
- Default and optional interface method syntax and behavior.
- Interface conversion examples for structs/classes.

Do not include:

- Vtable struct layout or slot matching algorithms.

Sources:

- Unified spec 2.4, 6.3, 6.4.
- Default interface methods proposal.
- Rejected interface visibility proposal for current-rule wording only.
- Tests: `interface_*`, `default_interface_methods`, `vtableof`.
- `BindableNodeAnalyzer.Lowering.Interfaces.cs`.

### 13. `13-enums-newtypes-and-inline-constants.md`

Subsections:

- Enums
- Fixed-Representation Enums
- Enum Values And Symbols
- `newtype`
- Callable Newtypes
- Inline Constants
- Type-Scope Constants

Include:

- Current enum, fixed enum, `@symbol`, newtype, and inline constant behavior.
- Short examples for numeric newtypes and callable newtypes.

Do not include:

- Metadata object schema for inline constants except a link.

Sources:

- Unified spec 1.10 and 1.11.
- Inline constants and fixed enums proposal.
- Tests: `inline_constants_fixed_enums`, `newtype_*`,
  `enum_value_c_symbols`, callable newtype tests.

### 14. `14-arrays-slices-optionals-and-strings.md`

Subsections:

- Array Values
- Fixed-Size Arrays
- Array Literals
- Slices And Ranges
- Optional Values
- String Literals
- Counted Text
- Standard Array And String Helpers

Include:

- Array carrier model at source level.
- Fixed storage and fixed-size arrays.
- Slice syntax, from-end operator use, optionals, strings and counted text.
- Link to standard library details.

Do not include:

- Expanded array carrier component ABI except brief source-level intuition.

Sources:

- Unified spec 1.6, 1.7, 1.9, 3.9, 7.1.
- LLM guide arrays and strings.
- Tests: array, fixed array, string, slice, optional, std string tests.
- `lib/std/src/std_array.camp`, `std_string.camp`, `std_utf8.c`.

### 15. `15-errors-thrown-and-cleanup.md`

Subsections:

- `thrown` Parameters
- `throw`
- `try`, `catch`, And `finally`
- Catch Arguments
- Cleanup Expressions
- Error Propagation In Calls

Include:

- User model for thrown slots and cleanup.
- Examples of `catch auto`, catch expressions, and `finally delete`.
- Link async error propagation to async section.

Do not include:

- Lowered exception/control-flow transformations.

Sources:

- Unified spec 3.7.
- Tests: `thrown_parameter_forwarding`, `catch_argument_variable`,
  iter thrown API, async thrown tests.
- `BindableNodeAnalyzer.Lowering.Exceptions.cs`.

### 16. `16-lifetimes-allocation-and-within.md`

Subsections:

- Lifetime Annotations
- Default Lifetimes
- `escaped`, `scoped`, And `unscoped`
- Lifetime Anchors
- Allocators And `within`
- Source-Level Allocation
- Safe Lifetime Casts

Include:

- User-level lifetime rules and examples.
- `within` parameters, statements, expressions, default allocator policy, and
  `new`/`delete` interaction.
- Enough diagnostics guidance to help users fix code.

Do not include:

- Flow fact lattice, slot/value fact propagation, or full algorithm.

Sources:

- Unified spec 4.
- Lifetime implementation plan for rules to preserve.
- Tests: `lifetime_*`, `within_*`, allocator/lifecycle tests.
- `BindableNodeAnalyzer.LifetimeFacts.cs`, method body lifetime checks.

### 17. `17-generics-and-type-capabilities.md`

Subsections:

- Generic Type And Function Declarations
- Constraints
- `T: any`
- `T: copyable`
- Interface Constraints
- `sizeof(T)`, `typenameof(T)`, And `vtableof(T: Interface)`
- Generic Construction And Destruction
- Generic Static Members

Include:

- User-facing generic model, erased/materialized behavior where necessary for
  writing correct code.
- Examples for array element stride, generic constructors, and interface
  constraints.

Do not include:

- Generic lowering details except links.

Sources:

- Unified spec 6.
- Receiver/classtype/typenameof proposal.
- LLM guide generics section.
- Tests: `generic_*`, `copyable_*`, `sizeof`, `typenameof`, `vtableof`.
- `BindableNodeAnalyzer.GenericCapabilities.cs`.

### 18. `18-iterators-foreach-and-generators.md`

Subsections:

- Iterator Type Forms
- Iterator Protocol
- Generator Functions
- `yield`
- `foreach`
- Iterator Cleanup
- Iterator Limitations

Include:

- User-level iterator syntax and protocol.
- Examples for array and class iterators.
- Current `await foreach` and `async iter` status as "Camp reserves..." only if
  the syntax is accepted/reserved but not implemented; otherwise keep it in
  proposals.

Do not include:

- Expanded iterator state-machine details.

Sources:

- Unified spec 5.2 and 5.4.
- Grammar `iter` and `async iter`.
- Tests: iterator/generator/foreach cases.
- `BindableNodeAnalyzer.Expansion.Iterators.cs`,
  iterator lowering code.

### 19. `19-async-await-and-postpone.md`

Subsections:

- Async Callable Shape
- Completion Callbacks
- `await`
- Resumer Selection
- `@awaitwith`
- `@noawait`
- `postpone`
- Async Frames And Allocation
- Async Lambdas And Callable Values
- Async Error Propagation

Include:

- Current resumer-based async model.
- `resumeAsync` pattern and `@awaitwith`/`@noawait` user-visible behavior.
- `postpone` as user-facing partial application.
- Simple examples.

Do not include:

- Superseded scheduler/`upon` design except a clear current rule if `upon` is a
  diagnosed invalid surface.
- State-machine lowering details beyond brief intuition.

Sources:

- Unified spec 5.3 and async-related sections.
- Accepted async resumption redesign proposal and implementation plan.
- Superseded scheduler supplement only after verifying details against current
  code/tests.
- Tests: `async_resumption_*`, `async_*`, `postpone`, `lambda_escaped_async`.
- `BindableNodeAnalyzer.MethodBody.cs`,
  `BindableNodeAnalyzer.Lowering.*` async-related code.

### 20. `20-attributes-and-doc-comments.md`

Subsections:

- Attribute Syntax
- Metadata Attributes
- Documentation Comments
- Child Documentation Targets
- Symbol Links
- Literal Regions
- Direct Attribute Authoring
- Relationship To Metadata JSON

Include:

- Attribute syntax and common metadata attributes.
- `///` and `/** */` comments, child targets such as `- value:`, doc links such
  as `[Symbol]`, and literal regions.
- The source-level relationship between doc comments and metadata attributes.
- A link to compiler metadata JSON docs for emitted shape.

Do not include:

- Full metadata JSON schema.
- Internal translation algorithms except where needed to explain source
  behavior.

Sources:

- Doc comments supplement.
- `DocCommentTranslator.cs`, `MetadataJsonSerializer.cs`.
- Tests: `doc_comments_api`, metadata and diagnostic doc-comment tests.

### 21. `21-standard-library-and-interop.md`

Subsections:

- Standard Library Availability
- Arrays And Strings
- Console And Streams
- Files
- Time And Math
- Native Interop Declarations
- Call Specs And Type Specs
- Target-Conditioned Code

Include:

- Minimal user-facing standard library overview grounded in `lib/std/src`,
  included only where it clarifies language behavior or common code patterns.
- Links to compiler package/build docs for how std is included or omitted.
- `extern`, target call specs/type specs, and native declarations at source
  level.

Do not include:

- Full API reference.
- Detailed API coverage that would need to change whenever ordinary stdlib APIs
  change.
- Target INI schema or native build command templates.

Sources:

- Unified spec 7.
- `lib/global.camp`, `lib/std/src/*.camp`.
- Target INI files for call specs/type specs available to users.
- Std and StdRun tests.

## Compiler Supplement

Audience: users invoking the compiler, configuring builds, packaging Camp code,
or consuming compiler-emitted metadata.

Do include:

- Exact command forms and option semantics.
- Build file and `#build` pragma behavior.
- Package source/install/use behavior.
- Target INI and native build behavior.
- Output, cache, and artifact layout.
- Metadata JSON shape and visibility modes.
- Diffs between dump modes and generated files.

Do not include:

- General language tutorial prose.
- Semantic algorithm internals unless needed to explain a command output.
- Historical CLI changes as narrative. Current docs may say an option is not
  supported, but should not center removed-option history.

### `docs/compiler/index.md`

List compiler supplement files and H2 subsections only.

### 1. `01-campc-command-line.md`

Subsections:

- Command Overview
- `build`
- `run`
- `dump`
- `restore`
- `pkg`
- `help`
- Common Build Options
- Exit Codes And Diagnostics
- Response Files

Sources:

- `src/campc/Program.cs` command tree and parsers.
- `CompilerRequest` fields in `CompilerDriver.cs`.
- Command-line tests.

### 2. `02-build-files-and-pragmas.md`

Subsections:

- `.campbuild` Files
- Bare Build File Expansion
- Response File Tokenization
- `#build` Pragmas
- Local And Global Pragmas
- Precedence Rules
- Source And Include Patterns
- Conditional Build Symbols

Sources:

- `ResponseFileExpander`, `BuildPragmaReader`, `CommandLineOptionParser` in
  `src/campc/Program.cs`.
- `CampProjectLoader.cs` language-server build loading.
- Grammar preprocessor directive rules.

### 3. `03-package-system.md`

Subsections:

- Package Specs
- Version Specs
- Link Kinds: `api`, `static`, And `shared`
- Package Sources
- Global And Local Package Roots
- Installing And Uninstalling
- `--use`, `--use-source`, And `#build`
- Restore Behavior
- Project References

Sources:

- `PackageCommands`, `PackageSpec`, `ProjectReferenceSpec` in
  `src/campc/Program.cs`.
- `CompilerDriver.TryPreparePackage` and related package code.
- Artifact cache proposal for intended layout, verified against code.

### 4. `04-targets-and-native-builds.md`

Subsections:

- Target Catalog
- Target Inheritance
- Variants
- Defines
- C Types
- Call Specs And Type Specs
- Natural Integer And Pointer Widths
- Toolchains
- Profiles
- Build Templates
- Frameworks
- Subsystems

Sources:

- `TargetCatalog.cs`, `NativeBuildDriver.cs`.
- `targets/**/*.ini`.
- Build and MSVC smoke tests.

### 5. `05-artifacts-cache-and-output-layout.md`

Subsections:

- Output Directories
- Artifact Directory Names
- Build Intermediates
- Static Libraries
- Shared Libraries
- Executables
- Runtime Files
- Link Files
- Package Cache Layout
- Project Reference Cache Checks
- Cleaning And Rebuilding

Sources:

- `BuildArtifactLayout.cs`, `NativeBuildDriver.cs`,
  project-reference code in `src/campc/Program.cs`.
- Artifact cache proposal, verified against current implementation.
- Tests around command line, project loader, package build, and std package
  cache.

### 6. `06-metadata-json.md`

Subsections:

- Emitting Metadata
- Visibility Modes: `none`, `export`, `public`, `all`
- Top-Level JSON Shape
- Metadata IDs
- Declaration Objects
- Type Objects
- Function And Method Objects
- Inline Constants
- Aliases
- Attributes And Symbol Links
- Consumer Guidance

Sources:

- Doc comments and metadata supplement.
- `MetadataJsonSerializer.cs`.
- Metadata tests.

### 7. `07-dumps-diagnostics-and-introspection.md`

Subsections:

- Dump Kinds
- Tokens
- CST
- AST
- Declarations
- Lowering
- Metadata
- XML Output
- API Inspection
- Diagnostic Format
- Using Dumps In Tests

Sources:

- `CompilerInspectMode` and dump handling in `CompilerDriver.cs`.
- `CompilerXmlSerializer.cs`, `BindableNodeCodeSerializer.cs`.
- Golden tests and `tests/README.md`.

### 8. `08-language-server-and-editor-tooling.md`

Subsections:

- `camp-lsp`
- Project Discovery
- Editor Setup
- Diagnostics
- Hover
- Go To Definition
- Completion
- Limitations And Backlog

Sources:

- `src/camp-lsp/README.md`, `src/camp-lsp/Program.cs`,
  `CampLanguageService.cs`.
- LSP tests and backlog for current/pending split.

### 9. `09-standard-library-build-integration.md`

Subsections:

- Default Standard Library Inclusion
- `--nostdlib`
- `lib/global.camp`
- Standard Library Package Build
- Native Helper Sources
- StdRun Package Cache
- Consuming Std Metadata

Sources:

- `lib/global.camp`, `lib/std/src`.
- `CompilerDriver` package preparation.
- `tests/Std`, `tests/StdRun`, and `tests/README.md`.

## Semantic Supplements

Audience: compiler writers and advanced maintainers. These docs are normative
for implementation behavior, diagnostics, and lowering, but are not required
reading for ordinary Camp users.

Do include:

- Exact binding and lowering invariants.
- Classifier tables and edge cases.
- ABI carrier shapes and generated helper behavior.
- Diagnostic obligations and source-range expectations.
- Links to tests that exercise each rule.

Do not include:

- Introductory language tutorials.
- Long user examples unless they illustrate an implementation edge case.
- Proposal staging history except when noting pending/rejected design in the
  proposals area.

### `docs/semantics/index.md`

List semantic supplement files and H2 subsections only. Include a short
"reading order for compiler work" note.

### 1. `01-binding-analysis-and-lowering-pipeline.md`

Subsections:

- Parse Model
- Bindable Node Model
- Analysis Scopes
- Declaration Passes
- Body Analysis
- Expansion
- Lowering
- Emission
- Generated Declaration Factory
- Provenance And Source Ranges

Sources:

- `CampParser.cs`, `SyntaxNode.cs`, `BindableNode*.cs`,
  `BindableNodeAnalyzer.Passes.cs`, `GeneratedDeclarationFactory.cs`,
  `Refactor.md` for preserved architecture rationale.

### 2. `02-expanded-forms-and-abi-shapes.md`

Subsections:

- Expanded Form Definition
- Arrays
- Delegates And `once`
- Iterators
- Async Callable Shapes
- Grouped Params
- Thrown Params
- Component Naming
- API Surface Versus Lowered Shape

Sources:

- Unified spec compiler-expanded forms.
- `ExpandedFormService.cs`, `BindableNodeAnalyzer.Expansion*.cs`,
  `BindableNodeAnalyzer.ParamsComponents.cs`,
  `BindableNodeAnalyzer.Lowering.Params*.cs`.
- Tests: expanded forms, API headers, lowering XML.

### 3. `03-conversions-raw-carriers-and-fence-casts.md`

Subsections:

- Conversion Levels
- Target Specifier Domains
- Raw Carrier Families
- Const And Volatile Overrides
- Pointer Family Rules
- Callable Rules
- Array And Optional Rules
- Generic Constructed Types
- Diagnostics And Warnings
- Test Matrix

Sources:

- Conversion draft and accepted implementation proposal.
- `ClassifyConversion` and related code in `BindableNodeAnalyzer.MethodBody.cs`.
- Target conversion INI sections.
- Conversion tests.

### 4. `04-constof-and-signature-compatibility.md`

Subsections:

- Anchor Binding
- Caller-Visible Substitution
- Callee Implementation View
- Storage Conversions
- Parameter Passing
- Return And `out` Positions
- Callable Variance
- Override Exactness
- Lambda Target Typing
- `constof(this)`
- Diagnostics

Sources:

- `constof` proposal and supplement.
- `BindableNodeAnalyzer.ConstOf.cs`, type binding and call matching code.
- `constof_*` tests.

### 5. `05-lifetime-analysis-and-flow-facts.md`

Subsections:

- Bound Lifetime Model
- Lifetime Anchors
- Slot Facts And Value Facts
- Defaults
- Assignment And Storage
- Return, Yield, And Delete
- Call-Site Relation Solving
- Constructors And Retained Values
- Delegates And Captures
- Iterators And Generated Contexts
- Generics
- Diagnostics

Sources:

- Lifetime implementation plan.
- `BindableNodeAnalyzer.LifetimeFacts.cs`,
  `BindableNodeAnalyzer.MethodBody.cs`.
- Lifetime and within tests.

### 6. `06-generics-erasure-and-capabilities.md`

Subsections:

- Generic Parameter Binding
- Constraints
- Erased Versus Materialized Values
- `T: any`
- `T: copyable`
- Size, VTable, And Type Name Capabilities
- Generic Arrays And Iterators
- Generic Construction And Destruction
- Static Members
- Diagnostics

Sources:

- Unified spec generics.
- `BindableNodeAnalyzer.GenericCapabilities.cs`,
  `BindableNodeAnalyzer.TypeBinding.cs`.
- Generic tests and semantic unit tests.

### 7. `07-callable-lowering-and-context-ownership.md`

Subsections:

- Callable Shape Service
- Direct Functions
- Delegates
- `once`
- Callable Newtypes
- Method References
- Lambdas
- Escaped Context Allocation
- Capture Layout
- Context Deletion
- Default Arguments And Thunks

Sources:

- Unified spec callables and lambdas.
- LLM guide lowering patterns.
- `CallableShapeService.cs`,
  `BindableNodeAnalyzer.Lowering.Lambdas.cs`,
  instance-call lowering, params lowering.
- Lambda/delegate/callable tests.

### 8. `08-async-resumption-lowering.md`

Subsections:

- Async Callable Expansion
- Await Site Collection
- Completion Callback Shape
- Resumer Selection
- `resumeAsync`
- `@awaitwith`
- `@noawait`
- State Machine Frames
- Tail Await Forwarding
- Error Propagation
- `postpone`
- Async Diagnostics

Sources:

- Unified spec async section.
- Async resumption redesign proposal and implementation plan.
- Current async tests.
- Async-related analyzer and lowering code.

### 9. `09-interface-vtables-and-dynamic-dispatch.md`

Subsections:

- Interface Shape
- Slot Function Types
- Required Slots
- Defaulted Slots
- Optional Slots
- Struct Conformance
- Class Conformance
- Interface Inheritance
- VTable Generation
- `vtableof`
- Interface Conversions
- Diagnostics

Sources:

- Unified spec interfaces.
- Default interface methods proposal.
- `BindableNodeAnalyzer.Lowering.Interfaces.cs`,
  interface conformance code.
- Interface tests.

### 10. `10-construction-destruction-and-allocation.md`

Subsections:

- Constructor Binding
- Default Constructors
- Destructors
- Base Initialization
- `init`
- `new`
- `delete`
- Allocator Selection
- Async And Iterator Restrictions
- Extern Type Boundaries
- Diagnostics

Sources:

- Unified spec data structures and lifetime sections.
- Rejected extern constructor proposal for current negative rules.
- Lifecycle/allocation code and tests.

### 11. `11-metadata-api-surface-and-symbols.md`

Subsections:

- API Header Model
- Metadata View Model
- Export/Public/All Filtering
- Symbol Names
- Metadata IDs
- Doc Comment Translation
- Stubs
- Type And Function Object Details
- Generated Versus Source Declarations

Sources:

- Metadata supplement.
- `MetadataJsonSerializer.cs`, `DocCommentTranslator.cs`,
  API serialization code.
- API and metadata tests.

### 12. `12-target-capabilities-and-c-emission.md`

Subsections:

- Target Definition Resolution
- Target-Owned Defines
- Type Specs And Call Specs
- Conversion Policy Tables
- Primitive C Spelling
- C Reserved Identifiers
- Symbol Emission
- Shared Library Export/Import
- Object, Static, Shared, And Executable Artifacts
- Objective-C Capability Boundary

Sources:

- `TargetCatalog.cs`, `CCodeEmitter.cs`, `NativeBuildDriver.cs`.
- Target INI files.
- Target capability tests and C emission tests.

### 13. `13-diagnostics-source-ranges-and-error-quality.md`

Subsections:

- Diagnostic Severity
- Stable Codes
- Parser Diagnostics
- Analysis Diagnostics
- Source Ranges
- Warnings
- Golden Diagnostic Tests
- LSP Diagnostic Mapping
- Error Message Style

Sources:

- `CompilerDiagnostic.cs`, `CampParser.cs`, analyzer `Report` sites.
- `DiagnosticStructureTests.cs`, diagnostics golden tests, LSP tests.
- `Refactor.md` diagnostic-code section for architecture notes.

## Compiler Development Guide

Target file: `docs/compiler-development-guide.md`.

Audience: human contributors and LLM agents editing this compiler repository.

Subsections:

- Repository Layout
- C# Solution Projects
- Compiler Pipeline Orientation
- Documentation Layout And Update Rules
- Proposal Lifecycle
- Working In A Dirty Tree
- Using `tmp/`
- Build Commands
- Targeted Test Workflow
- Commit Gate
- Golden Tests
- Semantic Unit Tests
- LSP Tests
- Coverage
- Updating Expected Files
- Diagnostics Expectations
- Code Style
- Source Code Comments As Local Instructions
- Per-Project READMEs
- Before Commit Checklist

Include:

- The current C# solution structure:
  `src/Camp.Compiler`, `src/campc`, `src/camp-lsp`,
  `src/Camp.Compiler.TestRunner`, `src/Camp.Compiler.Coverage`.
- The rule that compiler-source comments are the preferred home for local
  coding instructions about a particular compiler file, type, method, or tricky
  invariant. It is acceptable to add source comments as part of the
  documentation rewrite when the instruction belongs next to the code.
- The rule that general compiler coding instructions, multi-project guidance,
  repository workflows, and broad coding standards belong in docs.
- The rule that each C# source project folder should continue to have its own
  `README.md` with basic usage, setup, testing, and coding instructions, linking
  back to shared docs when the guidance is broader than that project.
- Test workflow from `tests/README.md`, including targeted `dotnet vstest`,
  `test-fast.proj`, full `dotnet test`, and coverage.
- `tmp/` rules: allowed for generated traces, scratch examples, rendered docs,
  coverage reports, and compiler outputs; never for source-of-truth docs.
- Documentation maintenance rules from this plan:
  user docs, compiler supplement, semantic supplements, compiler guide, LLM
  guide, proposals.
- Rule that semantic changes must update relevant docs and tests in the same
  change.
- Rule that accepted implemented proposals must be folded into normative docs.
- Rule that private machine-specific context belongs in untracked `local/`
  files, not committed docs.

Do not include:

- Full language reference prose.
- Long implementation details that belong in semantic supplements.

Sources:

- `tests/README.md`, project files, solution layout, `Refactor.md`,
  `src/camp-lsp/README.md`.

## LLM Camp Coding Guide

Target file: `docs/camp-llm-coding-guide.md`.

Audience: LLM agents asked to write Camp application/library code, not compiler
code. The file should be compact enough to include in an LLM context together
with standard library metadata.

Subsections:

- Purpose And Assumptions
- High-Priority Rules
- Minimal Syntax
- Namespaces And Visibility
- Types Cheat Sheet
- Function And Callable Patterns
- Struct/Class Patterns
- Arrays, Strings, And Optionals
- Lifetimes And Allocation
- Error Handling
- Generics
- Iterators
- Async
- Interop
- Standard Library Conventions
- Common Pitfalls
- Canonical Idioms
- Self-Check Before Returning Code

Include:

- Condensed, prescriptive rules.
- Code idioms from the existing LLM guide after updating them against current
  docs and tests.
- Pitfalls that an LLM is likely to get wrong: hidden expanded forms, `within`,
  `constof`, `thrown`, array carrier versus element type, callable value shapes,
  generic capability parameters, async completion callbacks, and lifetime casts.
- References to metadata usage: "prefer metadata over guessing standard library
  signatures."

Do not include:

- Compiler implementation details.
- Historical proposal text.
- Full standard library reference.

Sources:

- `extras/CAMP_LLM_CODE_GUIDE.md`.
- Final user docs.
- Final compiler supplement for CLI/build/package snippets.
- Standard library source and generated metadata.

## Proposals

Target directory: `docs/proposals/`.

Final public shape:

```text
docs/proposals/
  accepted/
  pending/
  rejected/
```

Do not create an index file in `docs/proposals/` or in its status
subdirectories. Proposal files should be discoverable by numbered filenames and
ordinary directory listings.

Proposal filenames:

- Number proposal files from oldest to newest within each status directory.
- Use a zero-padded numeric prefix followed by a short slug, such as
  `001-default-interface-methods.md`.
- Preserve chronological order when moving a proposal between status
  directories. If exact dates are unclear during the initial rewrite, choose the
  best order from file history and note the uncertainty in the rewrite notes.

All proposal files should include at the top:

- Title
- Status
- Proposal date
- Last updated date

Accepted proposal files should also include:

- Accepted date
- Canonical docs updated: yes/no, with links when known
- A note that the accepted proposal is historical after acceptance and the
  canonical source is the current docs

Rejected proposal files should also include:

- Rejected date
- A note that rejected proposals should not be updated after rejection except
  for mechanical renumbering or archive-path changes during the documentation
  rewrite

Pending proposal format:

- Title
- Status
- Proposal date
- Last updated date
- Summary
- Motivation
- Proposed surface
- Current implementation status
- Open questions
- Acceptance criteria
- Documentation impact
- Test impact
- Uncertainty introduced by compiler changes

Rejected proposal format:

- Title
- Status
- Proposal date
- Last updated date
- Rejected date
- Summary
- Rejected design
- Rejection rationale
- Current rule
- Future direction, if any

Classification strategy:

- Existing accepted and implemented proposals should be mined into
  `docs/language`, `docs/compiler`, or `docs/semantics` as already described in
  this plan. They may remain as historical accepted proposal files after their
  details are integrated, but the docs are canonical.
- New accepted proposals should remain in `docs/proposals/accepted/`, should not
  be updated after acceptance, and must state that their accepted details were
  integrated into the docs at acceptance time.
- If a proposal is accepted but only partly implemented, split it: implemented
  rules go into normative docs, incomplete work becomes a pending proposal with
  an explicit current implementation status.
- If a root proposal is actually implemented, mine it and move it out of the
  pending directory.
- If a root proposal is not implemented, move it under `pending/`.
- Keep rejected proposals under `rejected/`, but add a "Current rule" section so
  readers do not need to infer the actual language behavior from rejection text.
- Pending proposals should be kept aligned with compiler changes. If a compiler
  change creates uncertainty about a pending proposal, update the proposal with
  that uncertainty and list it as something that MUST be resolved before the
  proposal can be implemented.
- Accepted proposals should not be updated after acceptance. If the compiler
  changes later, update the canonical docs, not the accepted proposal.
- Rejected proposals should not be updated after rejection as a rule.

Initial best-guess classification:

- Pending: Objective-C interop.
- Rejected: non-extern constructors on derived extern classes; interface
  implementation visibility.
- Needs verification before classification: artifact/cache layout proposal.
- Accepted/implemented to mine: `constof`, conversion semantics, default
  interface methods, inline constants/fixed enums, lifetime analysis,
  receiver-preserving return/`classtype`/`typenameof`, async resumption.

## Writing Process

1. Move the current `docs/` tree to `archive/docs/` before creating the new
   `docs/` tree. Do not delete or overwrite existing docs.
2. Add `local/` to the ignore rules if it is not already ignored, and keep
   machine-specific context there.
3. Build the traceability table from all current source docs.
4. Classify proposal files as implemented, pending, rejected, accepted, or
   split.
5. Draft `docs/language` in reading order, using only concepts already
   introduced or cross-referencing forward when necessary.
6. Draft `docs/compiler` from CLI/build/package/target/metadata code and tests.
7. Draft `docs/semantics` from accepted proposals, implementation files, and
   tests.
8. Draft `docs/compiler-development-guide.md`.
9. Rewrite `docs/camp-llm-coding-guide.md` from the final user docs and current
   standard library surface.
10. Number and classify proposal files under `accepted/`, `pending/`, and
    `rejected/`.
11. Run documentation audits.
12. Review traceability table for unhandled source headings/rules.

## Prose And Style Conventions

- Write normative docs in present tense: "Camp supports...", "A function
  declares...", "The compiler reports...".
- Avoid historical phrases in normative docs: "no longer", "removed",
  "previously", "formerly", "superseded", "old design".
- When a negative rule is instructive, write it as current behavior:
  "Camp does not allow non-extern constructors on derived extern classes because
  the lifecycle boundary belongs to the native base type."
- Use "must" for hard language/compiler requirements, "may" for permitted
  behavior, "should" for recommended style, and "can" for capability.
- Prefer short examples over large programs. Examples should focus on one idea.
- Avoid placeholder names and related verbiage such as `foo`, `bar`, and `baz`
  in example code. Prefer meaningful names tied to the example's domain.
- Use fenced code blocks with language tags: `camp`, `sh`, `json`, `text`, `c`.
- Examples in early sections must not depend on advanced concepts introduced
  later unless the example explicitly links forward.
- Define each term on first use. Reuse the same term throughout.
- Use relative links for cross-references.
- Do not duplicate a rule in multiple places. State it once and link to it.
- Keep H1 to the document title. Use H2 for index-visible subsections. Use H3
  for local organization only when needed.
- Use plain Markdown tables for matrices and option lists.
- Put implementation-only notes in semantic supplements, not user docs.
- Put compiler invocation details in compiler supplements, not language docs.
- Keep proposal language clearly non-normative.

## Cross-Reference Conventions

- Use file-relative links:
  `[Lifetimes](16-lifetimes-allocation-and-within.md)`.
- Link to a subsection when the target heading is stable:
  `[Raw Carriers](07-pointers-qualifiers-and-conversions.md#raw-carriers-void-fn-nint-nuint-untyped)`.
- Prefer one authoritative destination for each rule.
- If a user doc needs compiler-writer detail, add a sentence like:
  "Compiler writers should see
  [Conversions, Raw Carriers, And Fence Casts](../semantics/03-conversions-raw-carriers-and-fence-casts.md)."
- Do not cross-link every mention. Link first mention or places where the reader
  naturally needs more detail.

## Example Verification Strategy

- Each language document should include at least one example that is already
  represented by an existing test or that can later be compiled in
  `tmp/docs-examples`.
- Prefer existing golden tests as example sources because they already define
  expected behavior.
- Examples that should compile standalone should be written so they can be
  verified later, but do not run full example compilation tests as part of this
  planning step.
- While producing the docs, use focused smoke tests to verify current compiler
  behavior when reading docs and source code leaves uncertainty about a
  semantic claim.
- For examples intended to produce diagnostics, mention the diagnostic behavior
  without requiring readers to see the exact full message unless the exact
  wording is important.
- Do not create committed example baselines unless examples become part of a
  future docs test suite.

## Documentation Quality Gates

Run these before considering the rewrite complete:

```sh
rg -n "TODO|TBD|FIXME" docs/language docs/compiler docs/semantics docs/*.md
rg -n "no longer|removed|previous|formerly|superseded|old design|current compiler|not yet implemented" docs/language docs/compiler docs/semantics docs/*.md
rg -n "\\[[^\\]]+\\]\\([^)]*\\)" docs/language docs/compiler docs/semantics docs/*.md
dotnet build src/camplang.sln
dotnet msbuild src/test-fast.proj -p:NoBuild=true
```

For the final documentation PR, also run the full non-skipped suite:

```sh
dotnet test src/camplang.sln
```

If the docs-only change does not touch compiler behavior, targeted example
compilation and `test-fast` are enough during drafting, but the full suite is
still the preferred commit gate from `tests/README.md`.

## Work Breakdown

### Phase 1: Inventory And Classification

Deliverables:

- `tmp/docs-source-map.tsv`.
- `tmp/proposal-classification.md`.
- Confirmed final proposal list.
- Current `docs/` moved to `archive/docs/`.
- `local/` ignored by git.

Tasks:

- Archive the existing documentation tree before creating the replacement
  `docs/` tree.
- Set up `local/` for machine-specific context and ensure it is omitted from the
  repository.
- Extract headings from every current doc and proposal.
- Extract grammar rules from the two grammar files.
- Map each heading/rule to a target document.
- Classify proposals as accepted, implemented, pending, rejected, or split.
- Assign numbered proposal filenames from oldest to newest within each status
  directory.
- Identify implementation files/tests that verify each high-risk rule.

### Phase 2: Language Docs

Deliverables:

- `docs/language/index.md`.
- 21 language section files.
- `docs/OutstandingIssues.md` for unresolved documentation questions.

Tasks:

- Draft in numeric order.
- Use code examples from tests where possible.
- Add cross-references instead of repeating rules.
- Audit for unintroduced later concepts.
- Update traceability rows as each source heading is consumed.
- Use smoke tests only when docs plus source leave semantic uncertainty.
- Record unresolved documentation uncertainty in `docs/OutstandingIssues.md`.

### Phase 3: Compiler Supplement

Deliverables:

- `docs/compiler/index.md`.
- 9 compiler supplement files.

Tasks:

- Document current `campc` commands and options from code.
- Document `.campbuild`, response files, `#build`, and precedence.
- Document package/project-reference/cache behavior.
- Document targets and native builds from INI schema and build driver.
- Move metadata JSON details out of user docs and into this supplement.

### Phase 4: Semantic Supplements

Deliverables:

- `docs/semantics/index.md`.
- 13 semantic supplement files.

Tasks:

- Convert accepted proposals into current normative compiler-writer rules.
- Check every algorithmic claim against implementation files.
- Link each supplement to relevant tests.
- Preserve edge cases and diagnostics that ordinary users do not need.
- Add source comments when an implementation instruction belongs next to the
  relevant compiler code rather than in general docs.

### Phase 5: Guides

Deliverables:

- `docs/compiler-development-guide.md`.
- `docs/camp-llm-coding-guide.md`.

Tasks:

- Merge repository workflow from `tests/README.md`, project layout, and docs
  maintenance rules into the compiler development guide.
- Ensure each C# source project folder keeps a `README.md` with project-specific
  usage, setup, testing, and coding instructions.
- Rewrite the LLM guide using final docs and current examples.
- Remove stale proposal/history language.

### Phase 6: Proposals

Deliverables:

- Numbered accepted, pending, and rejected proposal files.
- No proposal index files.

Tasks:

- Keep new accepted proposals under `docs/proposals/accepted/` as historical
  files after integrating their details into canonical docs.
- Move pending root proposals into `pending/`.
- Keep rejected proposals in `rejected/`, adding current-rule sections.
- Add proposal dates and last-updated dates.
- Add rejected dates to rejected proposals and treat them as immutable after
  rejection.
- Keep pending proposals aligned with compiler changes and record uncertainty
  that must be resolved before implementation.

### Phase 7: Audit And Polish

Deliverables:

- Clean traceability table with no unhandled semantic rows.
- Clean link/history/TODO audits.
- Verification command results.

Tasks:

- Check index files against section files.
- Check examples for compile or diagnostic accuracy.
- Search for historical language.
- Search for duplicated rule definitions.
- Run targeted docs-example checks and compiler tests.
- Log confirmed compiler bugs in `src/campc/OutstandingBugs.md`.

## Risk Areas

- Async: the scheduler supplement is superseded, while tests and proposals show
  a resumer-based design. Async docs need extra verification.
- Artifact/cache layout: the proposal appears largely implemented, but it should
  be classified by code and tests before moving it.
- Objective-C interop: target capability hooks exist, but the full feature
  appears pending. Avoid documenting it as user-facing current behavior.
- `constof` and lifetime interactions: details are split across proposals,
  spec, analyzer code, and tests. Preserve edge cases in semantic supplements.
- Raw carriers and target specs: user docs need simple guidance, while the
  semantic supplement must keep full conversion rules.
- Standard library: current spec covers selected APIs, while `lib/std/src` has
  more. Keep the language docs to the minimum useful standard-library surface
  so ordinary API churn does not force documentation churn.
- Accepted proposal files: deleting or hiding them too early could lose
  rationale. Mine them only after traceability rows are complete.

## Resolved Planning Decisions

1. Should accepted and implemented proposal files remain in the repository after
   the rewrite?
   Decision: yes. Existing accepted proposals should be mined into canonical
   docs as described above. New accepted proposals should remain in
   `docs/proposals/accepted/` as historical files after their details are
   integrated into the docs.

2. Should the top-level user docs directory be named `docs/language/`?
   Decision: yes.

3. Should the user-facing language docs include a full standard library API
   reference?
   Decision: no. Include only a minimum of standard-library API, and only where
   useful, so docs avoid churn when APIs change.

4. Should examples be guaranteed compilable?
   Decision: yes, but do not run compilation tests for the examples as part of
   this planning work. Verify them later. Fragments should be labeled by context
   rather than presented as complete programs.

5. Should pending proposals include accepted-but-partial work?
   Decision: yes. Implemented portions should move into normative docs, and
   incomplete portions should become pending proposal entries with explicit
   current implementation status.

6. Should docs include static-site front matter?
   Decision: no. Plain Markdown with stable H1/H2 headings is easier to read in
   source and can be transformed into HTML later.

7. Should the LLM guide live under `docs/` or remain in `extras/`?
   Decision: move the normative LLM guide to
   `docs/camp-llm-coding-guide.md`.

8. Should `tmp/` traceability files be committed?
   Decision: no. They are working artifacts. If the traceability map is useful
   long-term, convert it into a curated `docs/documentation-source-map.md` before
   committing.
