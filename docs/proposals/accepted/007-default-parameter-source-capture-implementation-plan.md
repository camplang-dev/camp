# Default Source Capture Implementation Plan

## Status

Done.

## Goal

Implement the default-parameter source capture proposal:

```camp
caller(sourceline)
caller(sourcefile)
caller(propertyname)
caller(functionname)
caller(qualifiedname)
sourceof(argumentName)
```

The feature is valid only in default parameter expressions. `caller` is
contextual: it is recognized as an intrinsic only when used with function-call
syntax in a default parameter expression. Otherwise, `caller` remains an
ordinary name.

This plan uses three stages so the parser/binder surface, direct-call
substitution, and callable/interface integration can land with focused tests.
The test strategy favors a small number of dense golden fixtures over many new
unit tests. Add xUnit tests only where a golden test cannot naturally cover the
behavior, such as command-line flag parsing.

## ~~Stage 1: Syntax, Binding, Diagnostics, Metadata, And API Persistence~~

**Status: Done.** Implemented with focused semantic coverage for binding,
diagnostics, API serialization, metadata JSON shape, and syntax-highlighting
updates. Full test pass completed on macOS, Windows, and WSL/Linux before the
Stage 1 commit.

### Scope

Stage 1 makes the source forms legal in signatures, rejects invalid forms, and
preserves them across metadata and API output. It does not need full call-site
substitution yet.

### Implementation Tasks

1. [x] Add a source representation for source-capture default expressions:

   - `caller(selector)` with selector values `sourceline`, `sourcefile`,
     `propertyname`, `functionname`, and `qualifiedname`;
   - `sourceof(argumentName)` with exactly one parameter-name operand.

2. [x] Bind `caller(...)` contextually:

   - recognize it only in default parameter expressions;
   - do not reserve `caller`;
   - leave non-call uses of `caller` to ordinary lookup;
   - treat the selector as an intrinsic selector, not as an ordinary expression.

3. [x] Bind `sourceof(argumentName)` contextually:

   - allow it only in default parameter expressions;
   - require one identifier;
   - bind that identifier to a parameter in the same signature according to the
     existing default-parameter rules.

4. [x] Add diagnostics for invalid source forms:

   - source capture intrinsic outside a default parameter expression;
   - unknown `caller(...)` selector;
   - wrong `caller(...)` arity;
   - `sourceof(...)` with zero arguments, multiple arguments, a non-identifier,
     or an identifier that does not bind to a valid parameter.

5. [x] Preserve source-capture defaults in Camp API headers:

   ```camp
   string text = sourceof(condition)
   string file = caller(sourcefile)
   uint line = caller(sourceline)
   ```

6. [x] Extend metadata JSON parameter output:

   - keep the existing `defaultValue` source text;
   - add structured `defaultExpression` records for source-capture defaults:

   ```json
   {
     "kind": "caller",
     "selector": "sourcefile"
   }
   ```

   ```json
   {
     "kind": "sourceof",
     "argument": "condition"
   }
   ```

7. [x] Keep ordinary defaults unchanged. Do not require structured
   `defaultExpression` for non-source-capture defaults.

8. [x] Update syntax highlighting definitions so `caller` and `sourceof` are
   highlighted as compiler intrinsics when used in call form:

   - `extras/camp.yaml`;
   - `extras/Camp.sublime-syntax`;
   - `extras/vscode-camp/syntaxes/camp.tmLanguage.json`.

### Test Coverage

Use golden tests first.

1. Add one diagnostics golden fixture, for example
   `tests/Diagnostics/source_capture_intrinsics_invalid.camp`, covering:

   - `caller(sourcefile)` in a global initializer;
   - `caller(functionname)` in a function body;
   - unknown selector such as `caller(sourcepath)`;
   - wrong arity such as `caller()` and `caller(sourcefile, sourceline)`;
   - `sourceof()` with no argument;
   - `sourceof(left + right)`;
   - `sourceof(missingParameter)`;
   - a valid inline constant named `caller` used as an ordinary default value,
     proving contextual behavior.

2. Add one API golden fixture, for example
   `tests/Api/source_capture_defaults.camp`, covering preservation on:

   - ordinary public/exported function defaults;
   - method defaults;
   - interface method defaults where defaults are allowed;
   - callable newtype defaults where defaults are allowed.

3. Add one metadata golden fixture, for example
   `tests/Metadata/source_capture_defaults.camp`, covering:

   - `defaultValue` source text for every selector;
   - structured `defaultExpression` for `caller(...)`;
   - structured `defaultExpression` for `sourceof(argumentName)`;
   - callable newtype and interface surfaces in metadata.

4. Add at most one semantic unit test only if the contextual `caller` behavior
   cannot be asserted cleanly in the diagnostics/API fixture.

5. Add a lightweight syntax-highlighting sanity check only if the repo already
   has tests for the editor grammar files. Otherwise, inspect the grammar diff
   manually and rely on parser diagnostics plus LSP completion tests for
   behavioral validation.

### Documentation Updates

Do not update the language guide in Stage 1 unless parser/binder behavior lands
before call-site semantics. If documentation is updated in this stage, keep it
limited to internal proposal notes or comments near implementation code.

### Completion Criteria

Stage 1 is complete when:

- [x] valid source-capture defaults parse, bind, and survive declaration analysis;
- [x] invalid placements and invalid operands produce focused diagnostics with
  useful source ranges;
- [x] `caller` remains usable as an ordinary name outside intrinsic call syntax;
- [x] API output preserves source-capture defaults exactly;
- [x] metadata output contains both `defaultValue` and structured
  `defaultExpression` records for source-capture defaults;
- [x] syntax highlighting files recognize `caller` and `sourceof` as intrinsic call
  names without making `caller` a general reserved word;
- [x] targeted tests pass;
- [x] full platform tests pass:

  - macOS: 688 passed, 14 skipped;
  - Windows: 692 passed, 10 skipped;
  - WSL/Linux: 682 passed, 20 skipped.

```sh
CAMP_TEST_KIND=Diagnostics,Api,Metadata CAMP_TEST_CASE=source_capture \
  dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll \
  --TestCaseFilter:FullyQualifiedName~GoldenFileTests
```

## ~~Stage 2: Direct Call-Site Substitution And Source Text Capture~~

**Status: Done.** Implemented direct-call source capture substitution, source
text capture, sourcefile path mapping/flags, LSP diagnostic flow, focused docs,
and cross-platform tests. Coverage was implemented with focused semantic,
path-mapper, project-loader, and language-service tests rather than new runtime
golden fixtures.

### Scope

Stage 2 implements default insertion for direct calls to ordinary functions and
methods. It supplies file, line, function name, qualified name, property name
inside property accessors, and `sourceof(argumentName)` text.

### Implementation Tasks

1. [x] Add a call-site source capture context created after overload resolution and
   argument binding:

   - source file path;
   - source line;
   - caller function visible name;
   - caller qualified visible name;
   - caller property name, when the caller is a property accessor body;
   - source ranges for supplied arguments by bound parameter.

2. [x] Insert source-capture defaults before callable lowering:

   - substitute `caller(sourceline)` as a `uint` literal;
   - substitute `caller(sourcefile)` as a string literal;
   - substitute `caller(functionname)` as a string literal;
   - substitute `caller(qualifiedname)` as a string literal;
   - substitute `caller(propertyname)` as a string literal when called from a
     property accessor body;
   - treat `caller(propertyname)` as not supplied outside property accessor
     bodies.

3. [x] Implement visible caller names:

   - top-level functions use their visible function name;
   - instance/static/out-of-scope members use their visible member name;
   - overload suffixes are included;
   - constructors supply `create`;
   - destructors supply `destroy`;
   - overrides and generators use the visible source name;
   - native symbols and generated helper names are ignored.

4. [x] Implement qualified caller names:

   - format as `[Namespace::][Type.]functionName`;
   - include overload suffixes;
   - use `create` and `destroy` for constructors/destructors;
   - ignore generated helper names.

5. [x] Implement `sourceof(argumentName)`:

   - capture the caller-written source text for a supplied positional or named
     argument;
   - supply `""` when the referenced argument was omitted;
   - normalize source text by removing comments, collapsing whitespace outside
     literals, trimming edges, preserving literal spelling, and preserving
     whitespace where required to avoid token merging;
   - capture source spelling before property/index/range/out/expanded-form
     lowering.

6. [x] Implement `caller(sourcefile)` path policy:

   - relative by default;
   - absolute only when `--sourcefile-paths absolute` is specified;
   - relative when `--sourcefile-paths relative` is specified or omitted;
   - relative to the active `.campbuild` directory when there is a build file
     and no explicit root is supplied;
   - relative to the request working directory for loose source-file builds
     when no explicit root is supplied;
   - relative to the selected `--sourcefile-root` when one or more explicit
     roots are supplied.

7. [x] Add the sourcefile path flags:

   - `--sourcefile-paths relative|absolute`;
   - repeatable `--sourcefile-root <path>`;
   - parse both through command-line and `.campbuild` handling;
   - rebase relative `--sourcefile-root` values using the existing build-file
     path rebasing rules;
   - make `--sourcefile-root` meaningful only for relative mode;
   - ensure these flags affect source capture, not ordinary diagnostic path
     formatting unless that is intentionally shared.

8. [x] Implement multi-root matching for relative mode:

   - if roots are supplied, choose the longest root that contains the source
     file;
   - emit the path relative to the chosen root;
   - if no supplied root contains the source file, emit a diagnostic rather than
     silently falling back to an absolute path;
   - if two roots produce the same relative output for distinct source files in
     one compilation, emit a collision diagnostic;
   - named roots are out of scope for this implementation plan.

9. [x] Put the root/path decision behind a small deterministic service, for example
   `SourcefilePathMapper`, rather than scattering `Path.GetRelativePath` calls
   through call lowering:

   - inputs: source file path, build/request root, path mode, sourcefile roots,
     host/path comparer policy;
   - output: sourcefile string or a diagnostic reason;
   - behavior: normalize paths, select longest containing root, detect
     duplicate relative outputs, and report files outside all supplied roots.

   This service is what makes cross-root and cross-drive behavior testable
   without depending on the developer machine's actual mounted drives.

10. [x] Update LSP behavior for Stage 2 diagnostics:

   - source capture diagnostics should flow through the ordinary compiler
     diagnostic pipeline into LSP diagnostics;
   - ranges should highlight the intrinsic call or selector for invalid
     signatures;
   - missing-argument diagnostics from `caller(propertyname)` not supplied
     should highlight the call expression and name the parameter.

### Test Coverage

Use a small number of dense golden fixtures.

1. [x] Add focused semantic coverage for direct-call source capture, covering:

   - `caller(sourceline)` from a direct function call;
   - relative `caller(sourcefile)` default;
   - `caller(functionname)` from top-level, instance, static, out-of-scope,
     constructor, destructor, override, generator, and overloaded callers;
   - `caller(qualifiedname)` for un-namespaced, namespaced, and type-member
     callers;
   - `caller(propertyname)` from getter and setter bodies;
   - `sourceof(argumentName)` for positional arguments;
   - `sourceof(argumentName)` for named arguments;
   - `sourceof(argumentName)` when the referenced argument is omitted;
   - normalization for comments, multi-line expressions, strings, char
     literals, and token-boundary-sensitive text.

   The direct-call coverage was implemented as semantic lowering assertions so
   path values and source line values can be compared deterministically.

2. [x] Extend the Stage 1 diagnostics coverage to include the call-analysis case:

   - calling a function whose omitted parameter default is
     `caller(propertyname)` from outside a property body should report a missing
     required argument because the default is not supplied.

3. [x] Add focused path-mapper unit tests for root behavior. These should be pure
   string/path tests and should not require real files:

   - default build-file root maps `/repo/app/src/main.camp` to
     `src/main.camp`;
   - explicit root maps `/repo/shared/lib.camp` to `lib.camp`;
   - repeatable roots choose the longest containing root;
   - Windows-style paths on the same drive map relative paths correctly;
   - Windows-style paths on separate drives do not match the wrong root;
   - files outside all supplied roots produce a diagnostic result;
   - two distinct files from different roots that would emit the same relative
     path produce a collision result;
   - path comparison uses the selected host/target policy consistently.

   The Windows/separate-drive cases should use literal paths such as
   `C:\work\app\src\main.camp` and `D:\packages\json\src\json.camp` in the
   mapper test. They should not require the test host to actually have those
   drives.

4. [x] Add one command-line/driver unit test for option parsing and build-file
   rebasing:

   - `--sourcefile-paths relative`;
   - `--sourcefile-paths absolute`;
   - repeatable `--sourcefile-root`;
   - relative `--sourcefile-root` in a `.campbuild` rebased to the build-file
     directory.

5. [x] Add one diagnostics or driver case for relative mode with source files
   outside all supplied `--sourcefile-root` values.

6. [x] Add one diagnostics or driver case for duplicate relative sourcefile output
   from distinct files under different roots, if this collision is not easier
   to assert in a focused unit test.

7. [x] Avoid raw absolute-path golden expectations. When an integration test must
   inspect an absolute `caller(sourcefile)` value, use a focused xUnit test that
   compares paths semantically:

   - capture the emitted/runtime string;
   - normalize separators;
   - compare `Path.GetFullPath(actual)` to the expected local file path;
   - or replace the generated temporary root with a stable token such as
     `<TESTROOT>` before comparing text.

   Do not commit expected files containing machine-specific temp directories,
   user profile paths, or drive letters.

8. [x] Add or extend one focused LSP diagnostic test only if existing compiler
   diagnostic tests do not already prove that source-capture diagnostics are
   surfaced through the language service.

### Documentation Updates

Stage 2 is where user-visible docs should begin.

1. [x] Add a small language-guide note in
   `docs/language/06-functions-methods-and-callables.md` near default
   arguments:

   - mention that library authors can use source-capture intrinsics as default
     values;
   - show one assertion/logging example;
   - avoid a long reference table.

2. [x] Skip
   `docs/language/18-expressions-statements-and-operators-reference.md` for
   Stage 2 because there is no existing special-expression table that needs a
   separate entry.

3. [x] Add the Stage 2 normative direct-call semantics to
   `docs/semantics/14-core-expression-statement-and-access-semantics.md`.
   Cover:

   - source forms;
   - placement and binding;
   - direct-call substitution;
   - source text normalization;
   - path policy;
   - not supplied semantics;
   - diagnostics;
   - test surface.

4. [x] Add focused supporting notes to existing semantics docs:

   - `docs/compiler/06-metadata-json.md` for `defaultExpression`,
     `defaultValue`, and API-header persistence;
   - `docs/semantics/14-core-expression-statement-and-access-semantics.md` for
     source ranges/provenance, diagnostic placement, and
     `caller(propertyname)` behavior.

5. [x] Add `--sourcefile-paths` and `--sourcefile-root` to the relevant compiler
   command-line/build file docs.

6. [x] Skip `docs/compiler/08-language-server-and-editor-tooling.md` for Stage
   2 because source-capture diagnostics flow through ordinary compiler
   diagnostics and need no separate user-visible LSP behavior.

7. [x] Add a minimal LLM guide note only if Stage 2 exposes enough behavior for LLMs
   to use it safely:

   - one sentence that these intrinsics are default-parameter-only;
   - one assertion/logging example.

### Completion Criteria

Stage 2 is complete when:

- [x] direct calls substitute all `caller(...)` selectors correctly;
- [x] `caller(propertyname)` is supplied only from property accessor bodies;
- [x] omitted `caller(propertyname)` outside property bodies produces the intended
  missing-argument diagnostic;
- [x] `sourceof(argumentName)` captures caller-written source text for supplied
  positional and named arguments;
- [x] `sourceof(argumentName)` supplies `""` when the referenced argument is
  omitted;
- [x] source text normalization is deterministic and tested;
- [x] relative and absolute `caller(sourcefile)` behavior is implemented and tested;
- [x] repeatable `--sourcefile-root` behavior is implemented and tested;
- [x] cross-root relative path failures diagnose instead of silently emitting
  absolute paths;
- [x] duplicate relative sourcefile output from distinct source files diagnoses;
- [x] source-capture diagnostics appear through LSP with usable ranges, either by
  existing coverage or a focused LSP diagnostic test;
- [x] focused language/compiler/LLM docs are updated as described;
- [x] targeted tests pass;
- [x] full platform tests pass:

  - macOS: 698 passed, 14 skipped;
  - Windows: 702 passed, 10 skipped;
  - WSL/Linux: 692 passed, 20 skipped.

```sh
CAMP_TEST_KIND=Diagnostics,StdRun CAMP_TEST_CASE=source_capture \
  dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll \
  --TestCaseFilter:FullyQualifiedName~GoldenFileTests
```

If a command-line unit test was added:

```sh
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll \
  --TestCaseFilter:FullyQualifiedName~CompilerDriverOptionTests
```

## ~~Stage 3: Callable Surfaces, Thunks, Interfaces, API Consumption, And Final Docs~~

**Status: Done.** Existing callable/default lowering already carried the Stage
2 substitution through callable newtypes, bound method references, interface
calls, concrete calls, and API-header consumers. Stage 3 added focused semantic
coverage and completed the normative docs.

### Scope

Stage 3 completes integration with all callable surfaces that permit default
values and verifies downstream package/API behavior. It also finishes the
normative documentation.

### Implementation Tasks

1. [x] Ensure source-capture defaults work through callable references and default
   thunks:

   - generated thunks preserve source call-site provenance;
   - defaults are applied before callable lowering;
   - `within`, `thrown`, `out`, `sizeof`, `typenameof`, and `vtableof` ordering
     remains unchanged.

2. [x] Ensure interface call behavior follows existing default rules:

   - calls through an interface view use interface-surface defaults;
   - calls through a concrete implementation use concrete-surface defaults;
   - source capture does not copy, merge, or inherit defaults between related
     callable surfaces.

3. [x] Ensure callable newtype and ascription behavior follows existing default
   rules:

   - callable-newtype calls use the callable surface selected by the call;
   - ascribed implementation defaults are optional according to existing
     callable/default semantics;
   - generated adapters/thunks preserve the original source call site.

4. [x] Verify API-header consumption:

   - source-capture defaults emitted by a library API header remain deferred;
   - downstream calls capture the downstream source location and caller name,
     not the library declaration site;
   - metadata JSON from library builds preserves structured defaults.

5. [x] Complete docs:

   - finish the source-capture additions in
     `docs/semantics/07-callable-lowering-and-context-ownership.md`, including
     all callable/interface/ascription interactions;
   - finish supporting metadata/API details in
     `docs/semantics/11-metadata-api-surface-and-symbols.md`;
   - finish any focused diagnostics/provenance note in
     `docs/semantics/13-diagnostics-source-ranges-and-error-quality.md`;
   - finish any necessary property accessor cross-reference in
     `docs/semantics/14-core-expression-statement-and-access-semantics.md`;
   - update `docs/compiler/06-metadata-json.md` with `defaultExpression`;
   - update `docs/compiler/01-campc-command-line.md` and
     `docs/compiler/02-build-files-and-pragmas.md` for `--sourcefile-paths`
     and `--sourcefile-root`;
   - keep language-guide changes short and library-author focused;
   - keep LLM guide changes short and example-driven;
   - update syntax highlighting files if Stage 1 did not already complete all
     editor grammar changes;
   - update LSP/editor tooling docs only for source-capture behavior that is
     visible to editor users;
   - update the proposal status if the implementation is accepted/completed by
     the project process.

### Test Coverage

Use golden tests to cover broad behavior with few files.

1. [x] Add focused callable-surface semantic coverage covering:

   - function reference or method reference using a default thunk;
   - delegate/callable invocation with source capture defaults;
   - interface-view call using interface defaults;
   - concrete call using concrete defaults;
   - callable newtype call;
   - ascribed implementation call;
   - `caller(functionname)` and `caller(qualifiedname)` through these surfaces;
   - `sourceof(argumentName)` through a thunk.

2. [x] Add one API-consumption fixture if the existing golden runner supports the
   shape cleanly, or use a small driver/project-reference unit test otherwise:

   - build or load a library API exposing source-capture defaults;
   - compile a consumer that omits those parameters;
   - verify the consumer call site is captured.

3. [x] Extend the Stage 1 metadata fixture rather than adding another metadata
   fixture unless interface/callable-newtype structured defaults cannot be kept
   readable in the existing file.

4. [x] Do not add extra xUnit tests for behavior already covered by golden runtime,
   API, metadata, and diagnostics fixtures.

5. [x] Add or extend an LSP test only if callable/default-thunk source-capture
   diagnostics or hover/signature-help behavior needs direct editor coverage.

### Documentation Completion Criteria

Docs are complete when:

- [x] the language guide has a brief default-argument note and one practical
  example;
- [x] `docs/semantics/07-callable-lowering-and-context-ownership.md` contains the
  complete normative behavior for placement, binding, default insertion,
  lowering, not supplied semantics, source text normalization, callable
  surfaces, interface/ascription behavior, and test surface;
- [x] `docs/semantics/11-metadata-api-surface-and-symbols.md` contains the
  normative metadata/API header behavior;
- [x] `docs/semantics/13-diagnostics-source-ranges-and-error-quality.md` contains
  any needed source range/provenance diagnostic note;
- [x] `docs/semantics/14-core-expression-statement-and-access-semantics.md` remains
  the source of truth for property accessor recognition, with only a focused
  source-capture cross-reference if needed;
- [x] compiler docs describe the metadata JSON shape, `--sourcefile-paths`, and
  `--sourcefile-root`;
- [x] the LLM guide has only the compact reminder needed to produce correct API
  signatures;
- [x] syntax highlighting files recognize the new intrinsic call names;
- [x] LSP behavior is either covered by existing compiler-diagnostic plumbing or a
  focused LSP test;
- [x] no active doc asks ordinary Camp consumers to learn unnecessary compiler
  internals for this feature.

### Completion Criteria

Stage 3 is complete when:

- [x] callable references and generated default thunks preserve source call-site
  capture;
- [x] interface-view and concrete-view calls apply the correct callable surface
  defaults;
- [x] callable newtypes and ascribed implementations apply existing default rules
  without source-capture-specific exceptions;
- [x] downstream API-header consumers capture their own call sites;
- [x] metadata JSON and API headers preserve source-capture defaults consistently;
- [x] syntax highlighting and LSP/editor documentation are updated where applicable;
- [x] all planned docs are updated;
- [x] targeted tests pass;
- [x] full platform tests pass:

  - macOS: 701 passed, 14 skipped;
  - Windows: 705 passed, 10 skipped;
  - WSL/Linux: 695 passed, 20 skipped.

```sh
CAMP_TEST_KIND=StdRun,Api,Metadata CAMP_TEST_CASE=source_capture \
  dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll \
  --TestCaseFilter:FullyQualifiedName~GoldenFileTests
```

- [x] the final full non-skipped suite passes before committing implementation
  changes:

```sh
dotnet build src/camplang.sln
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll
```

## Minimal Test Fixture Summary

The intended additional test footprint is:

- one diagnostics golden fixture;
- one API golden fixture;
- one metadata golden fixture;
- one direct-call runtime golden fixture;
- one callable-surface runtime golden fixture;
- one API-consumption/project-reference test only if needed;
- one command-line option unit test only if `--sourcefile-paths` and repeatable
  `--sourcefile-root` cannot be covered by a golden fixture;
- one focused diagnostics or driver case for roots that do not contain a source
  file, plus one for duplicate relative output if not covered elsewhere.
- one focused LSP diagnostic/editor test only if existing language-service
  plumbing does not already cover the new diagnostics.

That is the minimum practical set because each fixture covers a different
observable surface: diagnostics, API persistence, metadata persistence, runtime
substitution, callable thunk/interface behavior, downstream consumption, and
driver option parsing.
