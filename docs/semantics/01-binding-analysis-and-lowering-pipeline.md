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

It also carries three independent mode facts:

- **command mode:** `build`, `run`, `dump`, `test`, or `cover`;
- **declaration participation mode:** production or test module;
- **coverage instrumentation mode:** disabled or production subject.

Do not infer one of these from another. `test` and `cover` compile the root
test module with test-module declaration participation. A coverage subject is
compiled with production declaration participation plus coverage
instrumentation. Ordinary `build`, `run`, and `dump` requests use production
participation unless a test-specific compiler test intentionally constructs a
different request.

Root source files are user build inputs. API headers and package/project
reference surfaces are also source files in the compilation, but they are marked
as API headers so emission, metadata, and API output can filter them correctly.

Compiler writers should keep the distinction intact:

- root files own emitted C and source metadata for the current module;
- API headers contribute declarations and signatures for analysis;
- generated declarations may participate in analysis and emission, but should
  keep provenance back to the source declaration or expression that required
  them.

`campc test` and `campc cover` use the same source pattern, `--api`, and
`--exclude` machinery as ordinary builds. The compiler does not implicitly scan
test directories. If a file should participate only in test/coverage builds, the
request or build-file workflow must select it only for those modes. Within a
file that also participates in production builds, `@test` and `@testonly` are
the source-level mechanisms that remove declarations from the production view.

## Preprocessing

Preprocessing runs before tokenization and parsing. It consumes Camp
preprocessor symbols from:

- the built-in `TRUE` symbol;
- selected profile symbol, such as `DEBUG` or `RELEASE`;
- selected target and variant owned defines;
- command-line or build-pragma defines.

Target-owned defines are recorded separately from user defines so diagnostics can
reject user attempts to supply target-owned symbols manually.

When declaration participation mode is test module, preprocessing also defines
`TEST_MODULE`. This symbol belongs to the compiler-owned test mode. It should
not be defined manually to simulate a test build because it would not enable the
test declaration view, harness generation, manifest output, or coverage runner
behavior.

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
- compiler dumps;
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

## Namespace Context

The parser accepts namespace statements in the file prelude and namespace
blocks at top level:

```camp
namespace App;

namespace Tools
{
	export struct Runner
	{
	}
}

namespace global
{
	extern int puts(string text);
}
```

A namespace statement sets the file default namespace. A namespace block
replaces that default for declarations inside the block; it does not append to
the file default. Nested namespace blocks are invalid. Namespace blocks contain
ordinary declarations only; `using` declarations, namespace statements, and file
metadata attributes remain prelude/file-level constructs.

`global` is the source spelling of the root namespace in namespace declarations
and qualified names. Internally, the root namespace is canonicalized as the
absence of a namespace. The analyzer must still distinguish an explicit root
declaration from a declaration that merely inherited no namespace so metadata
and tooling do not apply a file-default namespace to declarations inside
`namespace global { ... }`.

For source lookup, each declaration and each body is analyzed under its
effective namespace: the surrounding namespace block if present, otherwise the
file namespace statement, otherwise root. Unqualified lookup searches local
scopes, members, the effective namespace, imported namespaces, and the implicit
root `Std` import according to the ordinary lookup order. Qualification with
`Name::member` searches that namespace directly and does not require a `using`;
`global::member` searches the root namespace.

Type identity is namespace-aware. Two types or static classes with the same
simple source name are distinct when their effective namespaces differ. Binding
and compatibility checks should compare resolved type definitions, not only
source spelling or emitted C identifiers.

## Declaration Participation And Test-Only Ownership

Declaration participation is separate from source visibility. `private`,
`internal`, `public`, `export`, and export projections still mean source/API
visibility. Test-only participation answers whether a declaration exists in a
production view or only in a test-module view.

The active declaration view is computed from the complete module graph:

- production participation excludes declarations marked `@test`, declarations
  marked `@testonly`, members owned by a top-level test-only type, and generated
  declarations whose source owner is test-only;
- test-module participation includes production declarations and test-only
  declarations;
- API headers still contribute imported source contracts, but they are not
  widened by the consuming module's test mode.

Do not remove test-only declarations from `Module.Definitions` as a mutation
strategy. Binding, ownership, diagnostics, metadata, API output, and C emission
must ask the shared declaration participation service for the active view.

`@test` is valid only on top-level functions with no visibility modifier. It
marks the function as test-only and as a test-discovery candidate. The built-in
runner signature is intentionally not a compiler-stopping validation rule.
Discovery classifies the signature later. A test is runnable by the built-in
runner when it has the shape `void name(thrown TYPE*)`, and `TYPE` has
instance fields named `message`, `sourcefile`, and `sourceline`. The string
fields may be `string` or `escaped string`; `sourceline` is `uint`.

`@testonly` is valid only on top-level declarations that are private or
`internal`. On a top-level class, struct, interface, enum, newtype, or callable
declaration it owns the complete declaration subtree. Generated declarations
must inherit test-only ownership through explicit generated-declaration
provenance rather than through symbol-name heuristics.

Production declarations may not depend on test-only declarations in any command
mode. This validation runs after binding has resolved enough references and call
targets to identify dependencies accurately. Test declarations and test-only
helpers may depend on production declarations and on other test-only
declarations.

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

The current implementation groups these stages under five analyzer pass names:
`DeclarationExpansion`, `DeclarationAnalysis`, `MethodBodyAnalysis`,
`NodeRewriteApplication`, and `LoweringRewrite`. The conceptual stages above
are more granular than those pass names, but the ordering contract is the same:
generated declarations must exist before declarations and bodies rely on them,
body analysis must own source legality, node-rewrite application must preserve
the decisions body analysis recorded, and lowering must not reopen source-level
validity.

Comments in source files should remain the closest documentation for exact pass
entry points and pass names. This document captures the cross-cutting contract.

## Declaration Collection

Declaration collection must make source names available without requiring body
analysis. It records:

- type declarations and nested member declarations;
- static class containers and their nested static members;
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
- static classes are source member containers, not types, and may not appear in
  type, pointer, generic-argument, construction, allocation, inheritance,
  interface-implementation, receiver, lifecycle, `classtype`, `sizeof`, or
  `vtableof` type positions;
- static class declarations accept attributes and doc comments but no ordinary
  visibility, generic, lifecycle, virtual, inheritance, or interface surface on
  the container itself;
- static class members must be explicitly static and may not be constructors,
  destructors, virtual/override/abstract/sealed members, instance fields, or
  declarations with an explicit `this` parameter;
- every `static` method, whether declared in a static class or ordinary type,
  is forbidden from declaring an explicit `this` parameter;
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
- static class member access through an unqualified or namespace-qualified
  container name;
- overload resolution and generic inference;
- target typing and default argument insertion;
- assignment, call, return, yield, throw, catch, and delete checks;
- property and indexer accessor binding;
- `@index`/`@range`, from-end, and range-argument checks;
- omitted trailing `out` result binding and deconstruction;
- intentional discard target recognition;
- conversion classification;
- lifetime fact propagation and storage checks;
- `constof` call-site equality and produced-result checks;
- lambda target typing and capture rules;
- async awaitability, resumer validation, and `@noawait` body validation;
- interface and virtual dispatch call sites.

For a prep-bearing invocation, body analysis owns the selected source callable
and substitutions, complete declared argument-to-slot mapping, explicitly
supplied slots, full or transformed mode, intrinsic result type, property
ineligibility, lifetime facts, and `(new)` legality. Overload selection and
argument mapping use the declared scalar/prep signature before omission of the
prep slot is considered. These facts are part of the analyzed call model, not
lowering guesses.

Body analysis should record facts needed by lowering. Lowering should not repeat
source overload resolution or decide whether a conversion is legal.

Default argument insertion remains the mechanism for assertion source capture.
Standard library helpers such as `assert(...)` and `fail(...)` are ordinary
public declarations that use `sourceof(argumentName)`, `caller(sourcefile)`,
and `caller(sourceline)` the same way any other callable default does. The
compiler does not generate source declarations for assertion helpers or for
their thrown type. Generated harness calls must not manufacture assertion
locations; assertion failures report the source captured at the user's call
site.

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
- property, indexer, and range access become ordinary calls/arguments;
- omitted trailing `out` results become generated caller storage;
- intentional discards become generated write-only locals;
- structured transfers run generated cleanup, while source `goto` keeps its
  source-level low-level behavior;
- async functions become callback-shaped state machines;
- iterators become protocol state machines;
- `sizeof`, `typenameof`, and `vtableof` use explicit capability values where
  required;
- `this`, `classtype`, and `constof` are lowered or erased where the ABI demands
  ordinary types.

A recorded transformed prep call lowers through its size/allocate/write
protocol, or through an analysis-approved measure-only optimization when its
elements are unobservable. A recorded full call stays one ordinary scalar call.
Lowering reuses captured receivers, arguments, capabilities, dispatch, error
handling, and allocation context; it must not rediscover prep omission from
argument count, target type, or `.length` use.

Lowering must preserve analysis decisions. It may create helper declarations,
rewrite expressions, and materialize temporaries, but it should not accept a
program that analysis rejected or reject a program merely because a source rule
was not rechecked.

Coverage instrumentation has an explicit pass boundary before destructive
lowering erases reliable Camp source sequence points. It inserts function-entry
and executable-line counters only for production-subject declarations in the
active coverage subject. The pass must not change evaluation order, duplicate
expression evaluation, change short-circuiting, add user-visible allocation or
thrown flow, or change lifetimes, cleanup, constness, `within`, or `thrown`
behavior.

Generated helpers, test functions, test-only declarations, and harness code are
not coverage denominator subjects unless a future feature documents a different
coverage view.

## Emission

Emission serializes the analyzed or lowered model into:

- C source and private/public C headers;
- Camp API headers;
- metadata JSON;
- test manifests, test results, coverage maps, coverage results, and LCOV where
  requested by test/coverage modes;
- Camp-like declaration and lowering dumps.

Each emission surface has a different audience. C emission is ABI-oriented.
Camp API headers are source-contract-oriented. Metadata is source-level and
tooling-oriented. Dumps are compiler-maintainer views.

Generated helpers should be exposed only when that surface needs them. For
example, C emission needs helper functions, but metadata should usually omit
implementation-only helpers.

## Test Harness And Coverage Runner

`campc test` lowers the test module, discovers tests from top-level `@test`
functions, emits a `camp.test-manifest` JSON artifact, generates a native
harness executable, runs selected tests unless `--list` was supplied, and writes
test results. The harness table is derived from the manifest. Skipped tests and
tests with invalid built-in runner signatures are recorded without invocation.
Valid tests are invoked with their declared thrown pointer type. When a
non-null thrown value is produced, the harness reads its `message`,
`sourcefile`, and `sourceline` fields and records the result as a failed test.

Private in-module tests remain private source declarations. C emission may
expose private generated names to the compiler-owned harness, but this does not
change Camp visibility, metadata visibility, API headers, or production native
exports.

For executable projects under in-module testing, the generated harness entry
point is the native entry point of the test artifact. The production `main`
remains ordinary source code that tests may call only when its signature and
visibility permit it.

`campc cover` uses the same harness and result pipeline, then merges runtime
counter data with one or more coverage map CSV files. In-module coverage
defaults to `self`. External coverage instruments selected shared-library
project-reference subjects in production participation mode and links the
harness against the instrumented shared library. If more than one shared
project reference could be the subject, the command line must name the subject.

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
