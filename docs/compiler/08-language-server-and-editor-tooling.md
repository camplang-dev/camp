# Language Server And Editor Tooling

`camp-lsp` provides Language Server Protocol support for Camp editors. It runs
over stdio and analyzes source through the same compiler project-loader and
language-service surfaces used by tests.

Debugging support is provided by the separate `camp-dap` process. See
[Debug Adapter And VS Code Debugging](10-debug-adapter-and-vscode-debugging.md)
for debug adapter setup, VS Code launch configuration, and current backend
limits.

The language server is a tooling layer over the compiler. It does not define
language semantics, and it should not invent project behavior that `campc` would
not understand.

## Installing Editor Support

Camp release distributions include optional editor setup scripts under
`extras/editors`. Run the script for the editor you use, and rerun it after
updating Camp:

```sh
extras/editors/vscode/install.sh
extras/editors/sublime/install.sh
extras/editors/micro/install.sh
extras/editors/vim/install.sh
extras/editors/fresh/install.sh
```

On Windows, use the matching `install.ps1` script from PowerShell. The VS Code
installer installs the bundled extension, which includes syntax highlighting,
language-server support, and debugging. Sublime Text, micro, Vim, and Fresh install
syntax highlighting and configure language-server support when the editor has a
supported setup. Each installer has `--help` or `-Help` for its own options.

## Server Process

The server entry point is `src/camp-lsp`. Editors start it as a stdio LSP
server. Project-specific setup instructions belong in that project README, while
this supplement documents the compiler-facing behavior.

The server maintains open-document overlays and sends analysis requests through
`CampLanguageService`. Open text can override the file-system version for the
currently edited document, which lets diagnostics and completion update before a
file is saved.

## Project Discovery

For a source file, the server looks for the nearest `.campbuild` by walking from
the file's directory toward the filesystem root. In each directory:

1. A build file named after the directory wins.
2. If exactly one `.campbuild` file exists, it is used.
3. If multiple build files exist and none is preferred, the search continues
   upward.

If no build file is found, the server analyzes the loose file with default
compiler settings and the standard library unless the request says otherwise.

## Project Loading

`CampProjectLoader` is read-only. It expands response files and source
patterns, reads global and local `#build` pragmas, resolves package and project
reference information, and prepares a `CompilerRequest` for analysis.

Unlike `campc build`, the project loader does not compile project references or
packages. For project references it tries to find an existing API header in the
referenced project's expected `bin/<artifact-directory>` location. If no API
header is available and the command kind is language service, it recursively
loads referenced source files for analysis.

This design keeps editor analysis responsive and avoids surprising writes from
the editor process.

## Source Overlays

Language-service analysis can include source overlays:

- the path identifies the source file;
- the text is the editor buffer;
- the version is used to discard stale diagnostics and responses.

Overlay analysis should preserve source ranges from the overlay text. LSP
diagnostics, hover, completion, definition, references, and symbols must map to
the editor's current buffer when one is supplied.

## Diagnostics

The server publishes parse and semantic diagnostics using compiler ranges and
severity. It avoids republishing identical diagnostics and ensures rapid
document changes publish only the latest diagnostics for a document version.

Diagnostics include source files, included API headers, package API headers, and
project-reference source fallbacks as needed for analysis. Editor clients should
display only diagnostics relevant to the document the server publishes.

## Hover

Hover uses symbol lookup, declaration metadata, and doc-comment attributes. It
can show summaries for functions, parameters, types, fields, enum values,
properties, and other source-backed symbols.

Hover should prefer source-level Camp spelling over lowered ABI spelling. For
example, a property hover should name the property surface, not the getter's
lowered implementation details.

For prep-bearing callables, declaration hover and method references show the
declared scalar return and `prep` array parameter. Hover over an invocation may
show the mutable prepared array when the prep slot is omitted, or the scalar
return when it is explicitly supplied. The language server does not invent a
synthetic array-returning overload.

## Go To Definition And References

Definition and reference results are source-backed. They rely on syntax ranges
and symbol identity established by semantic analysis. Generated declarations
should map to the user-written source construct that caused them when possible,
and implementation-only generated helpers should not appear as primary editor
navigation targets.

Project-reference API headers and source fallback both affect definition
results. If an API header exists, navigation may lead to that API header. If the
language service loaded source fallback, navigation may lead to the referenced
project source.

## Completion

Completion has two layers:

- lexical/syntactic fallback before a good semantic snapshot is available;
- semantic completion from the latest good analysis snapshot.

Semantic completion covers scope names, members, properties, components of
expanded forms such as array `.length` and `.elements`, enum values, methods,
variables, parameters, fields, aliases, and useful keywords.

Prep-bearing methods remain method-completion candidates but are excluded from
property completion. `prep` is suggested or highlighted as a modifier only in
parameter-declaration context; elsewhere it is an ordinary identifier or type
name. Editor grammars and semantic tokens should follow the same distinction.

For shadow classes, completion should show source fields, methods, implemented
interfaces, and hook methods that are visible in source. It should not show
generated shadow data structs, generated interface slots, stored shadow-instance
fields, shadow vtables, or helper thunks as ordinary members.

While a user is typing broken code, the service may use the last good snapshot
for member completion and signature help. This keeps the editor helpful without
making partially parsed code canonical.

## Signature Help

Signature help uses analyzed callable and call-expression information. It should
reflect default arguments, source parameter names, doc-comment parameter
summaries, and callable-newtype surfaces where those determine the signature the
caller sees.

Signature help should not expose hidden async completion parameters, generated
context parameters, or expanded ABI components unless the user is explicitly
editing an ABI-visible component name.

Signature help for prep callables retains the declared scalar/prep parameter
list and can indicate that omitting the prep parameter produces the prepared
array. Diagnostics for prep-shaped property access and invalid `(new)` prepared
allocation come from the compiler with their source ranges and are surfaced
unchanged by the language server.

## Document And Workspace Symbols

Document symbols are derived from source-backed declarations in the current
document. Workspace symbols are derived from the current analysis request and
its loaded source surfaces.

Generated helpers should not crowd the main symbol list. Source declarations
remain the primary editor-facing surface.

## Test Discovery And Coverage Tooling

The language service exposes the same manifest-equivalent test discovery records
that `campc test` and `campc cover` use:

- ID;
- name;
- qualified name;
- source file;
- source line;
- summary;
- skipped state and reason;
- built-in runner signature state.

The LSP server adds CodeLens commands beside top-level `@test` functions when
the current document belongs to an analyzable project:

| Command | Action |
|---|---|
| `camp.test.run` | Run `campc test <project> --filter <manifest-id>`. |
| `camp.test.debug` | Launch `camp-dap` for the generated test harness with the same exact manifest filter. |
| `camp.test.cover` | Run `campc cover <project> --filter <manifest-id> --coverage-format json`. |

Manifest IDs do not contain the wildcard characters `*`, `?`, or `^`, so the
filter sent by editor tooling is an exact no-wildcard test selection.

Compiler diagnostics for invalid source placement of `@test`, `@testonly`, and
`@skip` remain normal analysis diagnostics. Invalid built-in runner signatures
are surfaced as non-blocking test-runner diagnostics so valid tests in the same
project can still run.

Tools can import `camp.test-results` JSON and convert assertion failures or
invalid-test results into editor diagnostics at the captured source file and
source line. Tools can import coverage results JSON plus the matching coverage
map CSV to decorate executable source lines as covered or uncovered. Lines that
do not appear in the coverage map receive no coverage decoration.

All paths used for test discovery, result diagnostics, navigation, and coverage
decorations use the same `caller(sourcefile)` sourcefile path policy configured
by `--sourcefile-paths` and `--sourcefile-root`.

## Standard Library And Packages

Loose files and ordinary project files include the standard library by default.
`#build --nostdlib` in the selected project build file or source prelude disables
that behavior for the request.

Package and project-reference API headers are included when available. When an
installed package is missing, the language server should report diagnostics from
the loader/compiler rather than attempting package installation.

## LSP Range Mapping

Compiler ranges use `CampTextRange` and are converted to LSP ranges at the
server boundary. The conversion is direct: line and character values are passed
through to the LSP range model. Any change to compiler range construction can
therefore affect LSP tests.

Fallback diagnostics without a source range use a small range at the start of
the document. Prefer precise ranges in compiler diagnostics so the editor can
highlight the failing construct.

## Backlog Ownership

Feature backlog details belong in `src/camp-lsp/Backlog.md` or project-local
tracking, not in the language reference. Typical backlog areas include semantic
tokens, code actions, rename, formatting, multi-root workspaces, trace logging,
incremental analysis, and richer package/project diagnostics.

When an LSP feature requires a language-service or compiler semantic guarantee,
document that guarantee in the semantic supplement rather than only in editor
docs.
