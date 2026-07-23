# Plan: Replace `--include` With API-Only `--api`

Status: accepted

## Goal

Remove the old `--include` / `-i` feature entirely and replace it with `--api`.

`--api` should mean: load the selected `.camp` files as imported Camp API/header
files for analysis, not as root implementation sources. These files may describe
source/API contracts, contribute prelude `#build` pragmas, and be consumed by
project/package/std references, but they must not contain implementation-bearing
code that would require implementation C/object emission.

There is no compatibility window: `--include` and `-i` should disappear from the
compiler, build-file parser, tests, and docs.

## Current Model

- CLI option: `src/campc/Program.cs`
  - `AddBuildOptions` registers `--include`, `-i`.
  - `CommandLineOptionParser` recognizes `--include` / `-i`.
  - `ResponseFileExpander` treats both as path-valued options.
  - Parsed include patterns flow into `CompilerRequest.IncludeFiles`.

- Project/build-file parser: `src/Camp.Compiler/CampProjectLoader.cs`
  - Parses `--include` / `-i` from `.campbuild` files and `#build` pragmas.
  - Uses `IncludePatterns` internally, expands those patterns, and stores results
    in `CompilerRequest.IncludeFiles`.
  - Language-service project references add discovered API headers to
    `IncludeFiles`.

- Compiler request/loading: `src/Camp.Compiler/CompilerDriver.cs`
  - `CompilerRequest.IncludeFiles` are combined with package API headers.
  - Include files are loaded as `SourceFile { IsApiHeader = true }`.
  - API headers cannot be standard input.

- API-header behavior:
  - `Compilation.BuildSharedModule` marks definitions from these files as
    `IsApiHeader`.
  - `CompilerDriver.BuildApiOutputModule` skips API-header files when generating
    the current project API.
  - `MetadataJsonSerializer` skips API-header-owned definitions.
  - `CCodeEmitter` skips implementation source emission for API-header files, but
    may emit C header declarations for exported/public API shapes.

## Proposed Public Semantics

`--api <pattern...>`:

- accepts file paths or glob patterns, using the same path/glob expansion rules
  currently used by `--include`;
- marks matched files as API headers/imported source contracts;
- reads prelude `#build` pragmas from those files during the same iterative
  pragma-discovery loop used today;
- does not make those files root source files for C source emission, project API
  output, or project metadata ownership;
- rejects API files containing implementation-bearing declarations.

No short alias is proposed. In particular, do not keep `-i`, because its name is
tied to the removed feature.

## API-Only Validation

Add an explicit validation pass for files loaded via `--api`, generated package
API headers, and project-reference API headers. The diagnostic should be framed
around API files, not includes. Suggested wording:

```text
API file '<path>' contains implementation code; files passed with --api may only contain API declarations.
```

For declaration-specific diagnostics, prefer the declaration source range:

```text
Function 'foo' in API file '<path>' has a body; API files may only declare API surfaces.
Variable 'state' in API file '<path>' requires storage; mark it extern, inline, or move it to a source file.
```

Recommended source-level rules:

- Disallow any function, method, constructor, destructor, iterator, async body, or
  lambda-owning declaration with a body, unless the declaration is explicitly
  `extern`.
- Disallow non-`extern`, non-`inline` global variables.
- Disallow non-`extern`, non-`inline` static fields.
- Disallow concrete non-`extern` classes that would synthesize lifecycle helpers
  or implementation storage for an artifact-visible API surface. Generated Camp
  API headers already serialize visible classes as `extern`, so this should not
  reject compiler-generated artifact APIs.
- Allow pure API shapes: `extern` declarations, interfaces, aliases, enums,
  params/newtype/type declarations without implementation bodies, struct layouts,
  inline constants, and `#build` pragmas.

Implementation note: the cleanest first cut is probably a post-bind,
pre-expansion validation pass, because `SourceFile.IsApiHeader`, source ranges,
function bodies, variable/storage declarations, and `extern`/`inline` flags are
all available there. If we later need to catch generated implementation storage
more precisely, add a second post-expansion check over generated declarations
whose owner is an API header, using `GeneratedInfo`/provenance to point back to
the source declaration.

Avoid defining the rule as "no C header text"; API files are header-like and may
legitimately cause C header declarations/layouts. The forbidden thing is
implementation C/object emission.

## Compiler Source Changes

### Rename request/state terminology

Preferred mechanical rename:

- `CompilerRequest.IncludeFiles` -> `CompilerRequest.ApiFiles`
- local `includeFiles` variables that represent API headers -> `apiFiles`
- `IncludePatterns` -> `ApiPatterns`
- parser result lists `IncludePatterns` -> `ApiPatterns`

This is not strictly required for functionality, but leaving `IncludeFiles` in
the request would preserve the old mental model and make future code drift more
likely.

Likely touch points:

- `src/Camp.Compiler/CompilerDriver.cs`
  - request property;
  - stdin validation messages;
  - package API header merge;
  - `TryLoadCompilation(..., apiFilenames, ...)`;
  - `SourceFile { IsApiHeader = true }` loading;
  - shared-library API header matching.

- `src/campc/Program.cs`
  - replace `--include`, `-i` option registration with `--api`;
  - update option description;
  - parse `--api`;
  - remove `--include` and `-i` from option-value-count/path-value tables;
  - update pragma/application loops to use API naming;
  - project-reference API headers should append to the API file list.

- `src/Camp.Compiler/CampProjectLoader.cs`
  - parse `--api`;
  - remove `--include` and `-i`;
  - update response-file expander path-valued options;
  - rename internal include pattern fields;
  - language-service project-reference API headers should append to API files.

- `src/Camp.Compiler/CampLanguageService.cs`
  - `GetAnalysisIncludeFiles` should become something like
    `GetAnalysisApiAndPackageFiles` or `GetAnalysisImports`;
  - request API files should still be loaded as `isApiHeader: true`;
  - package source fallback behavior should remain unchanged.

- `src/camp-lsp/Program.cs`
  - update request cloning, cache file watching, and trace source counts after
    `CompilerRequest.IncludeFiles` is renamed.

### Add API-only validator

Potential location:

- `src/Camp.Compiler/ApiHeaderValidator.cs`, called by
  `CompilationPipeline.BuildAst` after bind/doc-comment diagnostics and before
  `BuildSharedModule`; or
- a small `CompilationPipeline.ValidateApiHeaders(compilation)` helper invoked
  by `BuildAst`.

Returning diagnostics as `BindDiagnostic`s on the owning `SourceFile` would fit
existing CLI/LSP reporting, since parse and bind diagnostics are printed before
analysis diagnostics.

## Tests

Update existing tests:

- `src/Camp.Compiler.TestRunner/ProjectLoaderTests.cs`
  - rename `Project_loader_reads_campbuild_and_build_pragmas` input from
    `#build --include api/*.camp` to `#build --api api/*.camp`;
  - update assertions if internal request property is renamed;
  - rename `Project_loader_expands_includes_and_excludes` to mention source/API
    patterns only if it actually tests both.

- `src/Camp.Compiler.TestRunner/CommandLineTests.cs`
  - rename `Include_pragmas_discovered_from_source_pragmas_contribute_build_pragmas`;
  - change `#build --include {{api}}` to `#build --api {{api}}`;
  - keep the assertion that the API file's pragmas are discovered but no C source
    file is emitted for it.

- LSP tests and helpers:
  - update direct `IncludeFiles` property usage, especially project-reference API
    header setup in `LanguageServiceTests`.

Add new tests:

- CLI/build-file parser accepts `--api` in command line, `.campbuild`, and
  prelude `#build` pragmas.
- `--include` is rejected as an unknown option.
- `-i` is rejected as an unknown option.
- API file with an ordinary function body is rejected.
- API file with a non-extern, non-inline global variable is rejected.
- API file with a non-extern, non-inline static field is rejected.
- API file containing extern declarations, interfaces, aliases, enums, struct
  layout, and inline constants is accepted.
- Generated project/package API headers continue to be consumed automatically as
  API headers.
- Shared dependency API headers remain marked as shared-library imports so public
  declarations are not treated as available from shared dependencies.

Suggested diagnostic golden cases can live under `tests/Diagnostics`, with one
focused `.camp` root file and one `--api` file selected through a command-line
test or a `.campbuild` test depending on current golden-runner capabilities.
If golden tests cannot pass auxiliary CLI API files directly, use command-line
unit tests in `CommandLineTests`.

## Documentation Updates

Remove `--include` and `-i` everywhere. Replace the conceptual section with
`--api`.

Known docs to update:

- `docs/compiler/01-campc-command-line.md`
  - test/run text that says tests use `--include`/`--exclude`;
  - build option table row for `--include`, `-i`.

- `docs/compiler/02-build-files-and-pragmas.md`
  - `.campbuild` example;
  - response-file rebasing list;
  - `--exclude` explanation;
  - "Include Patterns" section should become "API Patterns";
  - remove "analysis-only helper declarations" as a supported use unless they are
    API-only declarations.

- `docs/compiler/07-dumps-diagnostics-and-introspection.md`
  - analysis options list.

- `docs/semantics/01-binding-analysis-and-lowering-pipeline.md`
  - test/cover source selection wording.

- `docs/proposals/accepted/008-first-class-testing-and-coverage.md`
  - accepted proposal still mentions the old flag; update or add a note that the
    finalized spelling is `--api`.

Search command for final cleanup:

```sh
rg -n -- '--include|-i|IncludeFiles|IncludePatterns|include files|Include Patterns' src docs tests
```

Use judgment for ordinary English "include"; do not rename unrelated C target
include lists, `#include` emission, or generic "include" prose where it is not
the compiler option/model.

## Migration/Risk Notes

- Removing `--include` means existing local scripts will fail fast. That is
  acceptable because the feature has not shipped.
- Project/package/std reference behavior should not change externally. Those
  paths already consume generated API headers automatically; the internal path
  should simply flow through the renamed API-file list.
- The stricter API-only validator may reveal existing tests or docs that used
  include files as "analysis-only source" with bodies. Those should be moved to
  root source patterns or rewritten as API declarations.
- Be careful with static versus shared dependency semantics:
  - static dependencies expose `public` API;
  - shared dependencies expose only `export` API;
  - `SharedLibraryApiHeaders` should remain distinct from the general API file
    list so C header visibility keeps working.

## Suggested Implementation Order

1. Add `--api` parsing in CLI/project loader while still wired to the old
   internal fields temporarily.
2. Rename internal request/state fields from include terminology to API
   terminology.
3. Remove `--include` and `-i` parser cases and response-file option entries.
4. Add the API-only validation pass and diagnostics.
5. Update tests for the new spelling.
6. Add negative tests for implementation-bearing API files.
7. Update docs and run the final cleanup `rg`.
8. Run targeted tests:

```sh
dotnet build src/camplang.sln
dotnet test src/Camp.Compiler.TestRunner/Camp.Compiler.TestRunner.csproj --filter ProjectLoaderTests
dotnet test src/Camp.Compiler.TestRunner/Camp.Compiler.TestRunner.csproj --filter CommandLineTests
dotnet test src/Camp.Compiler.TestRunner/Camp.Compiler.TestRunner.csproj --filter LanguageServiceTests
```

Then run the broader golden suite if the validator changes compiler diagnostics
or emitted API behavior.
