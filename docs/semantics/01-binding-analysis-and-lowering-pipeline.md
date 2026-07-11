# Binding, Analysis, And Lowering Pipeline

This supplement describes the semantic pipeline compiler writers must preserve.
The central rule is simple: each stage should make one layer of meaning explicit
and should not rely on later stages to validate source-language rules that the
earlier stage owns.

## Source Files And Compilation

A `Compilation` contains source files, target selection, profile, preprocessor
symbols, default `within` allocation policy, parsed trees, bindable trees,
declaration expansion results, lowering results, and owner maps from definitions
back to source files.

Root source files are user build inputs. API headers and package/project
reference surfaces are also source files in the compilation, but they are marked
as API headers so emission, metadata, and API output can filter them correctly.

Compiler writers should keep the distinction intact:

- root files own emitted C and source metadata for the current module;
- API headers contribute declarations and signatures for analysis;
- generated declarations may participate in analysis and emission, but should
  keep provenance back to the source declaration or expression that required
  them.

## Preprocessing

Preprocessing runs before tokenization and parsing. It consumes Camp
preprocessor symbols from:

- the built-in `TRUE` symbol;
- selected profile symbol, such as `DEBUG` or `RELEASE`;
- selected target and variant owned defines;
- command-line or build-pragma defines.

Target-owned defines are recorded separately from user defines so diagnostics can
reject user attempts to supply target-owned symbols manually.

The preprocessor also recognizes file-prelude `#within` policy directives in the
compiler driver/language service path. `#build` is a project-loading concern and
must not become a semantic construct in the bindable tree.

## Tokenization And Parsing

`CampTokenizer` produces token streams with source ranges. `CampParser` produces
syntax nodes and parser diagnostics. Parser diagnostics should:

- point to the tightest useful token or syntax region;
- recover enough to report additional useful errors;
- preserve tokens needed by bindable-node construction and LSP range mapping.

Syntax nodes are not semantic declarations. They preserve source shape,
including trivia-sensitive attachment such as doc comments, but do not decide
overload resolution, generic substitution, lowering, or emitted ABI shape.

## Bindable Node Construction

The builder translates syntax nodes into `BindableNode` objects:

- `Module` for a source file;
- declaration nodes for types, functions, variables, aliases, fields,
  parameters, and enum values;
- type-reference nodes for source type syntax;
- expression and statement nodes for bodies.

Bindable nodes retain `SourceSyntax` where possible. This is essential for:

- diagnostics;
- dumps and XML serialization;
- language-service hover/definition/reference mapping;
- metadata doc-comment attachment;
- owner maps from source files to declarations.

Do not drop `SourceSyntax` merely because a node will be rewritten. If a lowered
node has no direct token, keep provenance through generated declaration info or
through the source node that caused the rewrite.

## Analysis Scopes

Analysis uses lexical and semantic scopes. A scope may contain:

- visible declarations and imported names;
- type parameters and generic constraints;
- receiver information;
- lifetime anchors;
- `constof` anchors;
- local variables and parameters;
- current function, containing type, and current `within` context;
- target and profile information.

Lookup must respect source visibility, namespace imports, type member scopes,
generic parameter scopes, receiver scopes, and body-local declarations. A symbol
found through one scope layer should not silently bypass a more local
declaration with the same name unless the language rule explicitly permits it.

## Analyzer Passes

The analyzer is intentionally multi-pass. The pass order is part of compiler
semantics because generated declarations, interface lowerings, virtual class
state, and body analysis depend on stable earlier facts.

Compiler writers should preserve these broad stages:

1. **Declaration collection:** register top-level and member declarations,
   symbols, generic parameters, and ownership.
2. **Declaration type binding:** bind type references, targets, aliases,
   receiver contracts, parameter defaults, lifetime anchors, and `constof`
   anchors.
3. **Declaration validation:** validate base types, interfaces, overrides,
   callable ascription, constructors, destructors, inline constants, metadata
   attributes, async implementation attributes, and export visibility.
4. **Expansion preparation:** create generated declarations needed for arrays,
   optionals, params, delegates, iterators, async callables, interfaces, virtual
   dispatch, class lifecycle, generic capabilities, and C/API emission.
5. **Body analysis:** analyze statements and expressions, overloads, conversions,
   lifetime facts, `constof` call-site substitution, lambda typing, await/postpone
   shapes, flow rules, and result checks.
6. **Lowering rewrite:** rewrite accepted source constructs into lower-level
   Camp-like forms for dumps and C emission.

Comments in source files should remain the closest documentation for exact pass
entry points and pass names. This document captures the cross-cutting contract.

## Declaration Collection

Declaration collection must make source names available without requiring body
analysis. It records:

- type declarations and nested member declarations;
- free functions and type-scoped functions;
- aliases and callable ascriptions;
- enum values and inline constants;
- field and parameter symbols;
- generated declaration placeholders where needed.

Collection should not make invalid programs appear valid. If a declaration name
is malformed, duplicate, inaccessible, or colliding with generated ABI names,
diagnose it while preserving enough tree shape for follow-up diagnostics.

## Type Binding

Type binding resolves type names and annotates type references with resolved
type strings. It also validates source positions for constructs such as:

- `constof(anchor)`;
- `scoped(...)`, `unscoped(...)`, and `escaped`;
- target type specs and call specs;
- `this` and `classtype`;
- `sizeof`, `typenameof`, and `vtableof` capability parameters;
- callable `this` parameters;
- interface constraints and generic constraints.

Type binding must not erase source spelling prematurely. Metadata and diagnostics
need the original source contract even when C emission uses a simpler or
target-specific spelling.

## Declaration Validation

Declaration validation enforces source-level contracts before lowering. Examples
include:

- interfaces may derive only from interfaces;
- classes/structs may implement interfaces and classes may derive from classes;
- required interface slots must be implemented;
- optional/default interface slots have valid initializers;
- virtual overrides match exactly;
- callable ascriptions match the natural callable reference form;
- `@awaitwith` and `@noawait` are valid only on concrete async definitions with
  Camp bodies;
- inline constants have eligible types and constant initializers;
- exported declarations expose only visible types.

Do not defer these rules to C emission. If a source declaration is semantically
invalid, lowering and emission should not have to guess at a fallback shape.

## Expansion

Expansion creates compiler-visible declarations and components while preserving
the source-facing API. Expansion covers:

- expanded array/optional/delegate/once/async/iterator component shapes;
- params component declarations and materialized `struct(T)` forms;
- generated vtables, interface indirect types, interface fixup thunks, and
  virtual class tables;
- lifecycle helpers for constructors, destructors, `new`, and `delete`;
- iterator and async state types;
- generated API surfaces for exported classes and interfaces;
- helper declarations required by generic capabilities.

Use `GeneratedDeclarationFactory` or established generation helpers so generated
definitions carry category, reason, source provenance, symbol names, visibility,
and generated/source distinctions consistently.

## Body Analysis

Body analysis resolves expression and statement semantics:

- locals, variables, fields, members, properties, and components;
- overload resolution and generic inference;
- target typing and default argument insertion;
- assignment, call, return, yield, throw, catch, and delete checks;
- conversion classification;
- lifetime fact propagation and storage checks;
- `constof` call-site equality and produced-result checks;
- lambda target typing and capture rules;
- async awaitability, resumer validation, and `@noawait` body validation;
- interface and virtual dispatch call sites.

Body analysis should record facts needed by lowering. Lowering should not repeat
source overload resolution or decide whether a conversion is legal.

## Flow Analysis

Flow analysis tracks control-sensitive rules such as:

- definite return and transfer statements;
- optional/default state where needed;
- lifecycle restrictions for constructors/destructors;
- safe cleanup paths;
- async and iterator body restrictions.

Flow diagnostics should be source-facing. The presence of generated cleanup or
state-machine code does not excuse a source body from following source flow
rules.

## Lowering

Lowering rewrites the accepted semantic tree into simpler Camp-like operations:

- expanded values become component arguments or materialized storage;
- source-level interface values become vtable/slot pointer operations;
- virtual dispatch becomes vtable calls or concrete calls;
- constructors/destructors become lifecycle helper calls;
- `new` and `delete` thread allocation contexts;
- lambdas become callable targets and context values;
- async functions become callback-shaped state machines;
- iterators become protocol state machines;
- `sizeof`, `typenameof`, and `vtableof` use explicit capability values where
  required;
- `this`, `classtype`, and `constof` are lowered or erased where the ABI demands
  ordinary types.

Lowering must preserve analysis decisions. It may create helper declarations,
rewrite expressions, and materialize temporaries, but it should not accept a
program that analysis rejected or reject a program merely because a source rule
was not rechecked.

## Emission

Emission serializes the analyzed or lowered model into:

- C source and private/public C headers;
- Camp API headers;
- metadata JSON;
- XML dumps;
- Camp-like declaration and lowering dumps.

Each emission surface has a different audience. C emission is ABI-oriented.
Camp API headers are source-contract-oriented. Metadata is source-level and
tooling-oriented. Dumps are compiler-maintainer views.

Generated helpers should be exposed only when that surface needs them. For
example, C emission needs helper functions, but metadata should usually omit
implementation-only helpers.

## Provenance And Diagnostics

Generated nodes should carry enough provenance to answer:

- which source declaration or expression caused this node;
- whether this helper is part of source API or implementation lowering;
- where a diagnostic should point if the helper cannot be generated or emitted;
- whether metadata/API output should include or omit it.

Diagnostics from generated nodes should prefer the source token that caused
generation. If no exact token exists, point to the nearest source declaration
name or expression span.

## Compiler Writer Checklist

When changing a semantic feature, identify:

- which pass first owns the rule;
- which facts later passes consume;
- which generated declarations or lowered expressions are produced;
- which output surfaces should expose or hide those generated forms;
- which source ranges and diagnostics are expected;
- which tests cover positive behavior, negative behavior, dumps, metadata, API
  output, and target-specific emission.

If a rule needs explanation for all future compiler work, add docs here. If a
rule is local to a complex source method or helper, prefer a source comment near
the code.
