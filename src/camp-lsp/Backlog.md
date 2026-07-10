# Camp LSP Backlog

This list tracks language-server features that are not compiler bugs. Items are
ordered by recommended implementation priority, balancing user value against
implementation complexity and risk.

## LSP Performance Backlog

These items track responsiveness fixes that do not require full incremental
compilation. They are ordered by expected value/effort for real editor use.

## ~~LSPPERF-001 — Debounce Diagnostics After Typing~~

Complexity: Low-medium.

~~Do not re-analyze immediately on every `textDocument/didChange`. Wait for a
short quiet period after the last edit, likely 300-600 ms, before starting
diagnostic analysis. Coalesce rapid full-document changes into one analysis
request for the latest document version.~~

~~Timing: Do this first. It should reduce red-underline lag, server queue
pressure, and visible editor churn without requiring compiler architecture
changes.~~

## LSPPERF-002 — Do Not Block Interactive Queries On Fresh Analysis

Complexity: Low-medium.

Hover, completion, signature help, go-to-definition, document symbols, and
references should answer from the latest successful semantic snapshot whenever
possible. They should not synchronously trigger or wait for a full fresh compile
of dirty text. If the current edit is broken or analysis is in flight, return
best-effort stale results or an empty result quickly.

Timing: Do immediately after diagnostics debouncing. VS Code feels broken when
interactive features wait seconds for a fresh snapshot.

## LSPPERF-003 — Single-Flight Latest-Version Analysis

Complexity: Medium.

Ensure only one analysis runs per document/project snapshot lane. When a newer
document version arrives, cancel the old request if possible or ignore its
result when it completes. Never publish diagnostics or replace the query
snapshot with stale analysis results.

Timing: Do after interactive queries are decoupled from fresh analysis. This is
the core safety rule that prevents old work from clogging the server and
reintroducing obsolete diagnostics.

## LSPPERF-004 — Separate Diagnostic And Query Snapshot Policies

Complexity: Medium.

Diagnostics should run in the background and can tolerate latency. Interactive
queries should be opportunistic and fast. Keep explicit state for the latest
successful query snapshot, latest requested diagnostic version, latest completed
diagnostic version, and current in-flight analysis.

Timing: Do after single-flight analysis. This makes the LSP behavior easier to
reason about before adding broader project diagnostics or richer completion.

## LSPPERF-005 — Cache `.campbuild` Project Loading

Complexity: Medium.

Avoid re-reading and re-expanding the nearest `.campbuild`, `#build` pragmas,
glob patterns, package source roots, project references, and cached API header
lookups for every request. Cache resolved project inputs by build file and
invalidate when the build file, included build/pragmas source files, or relevant
package/reference inputs change.

Timing: Do after analysis scheduling is sane. Project loading is pure overhead
that VS Code may accidentally repeat on every hover/completion/change.

## LSPPERF-006 — Cache Parsed Unchanged Files

Complexity: Medium-high.

Even without full incremental binding, avoid tokenizing and parsing unopened
unchanged files repeatedly. Cache token/CST/AST surfaces by file path,
content hash, and relevant parse options. Reuse cached parses for project
sources, package sources, and generated API headers when only an open overlay
file changed.

Timing: Do after project-loading cache. This is a bigger compiler/tooling
boundary change, but still much smaller than full incremental semantic
analysis.

## LSPPERF-007 — Add Cheap Completion Fallbacks

Complexity: Medium.

When no fresh semantic snapshot is ready, return useful completion candidates
from the latest successful snapshot or a lightweight lexical/project index.
Prefer stale but responsive completions over no completion. Member completions
should remain conservative when receiver type information is unavailable.

Timing: Do after query paths reliably use cached snapshots. This improves the
typing experience while deeper analysis performance work continues.

## LSPPERF-008 — Throttle Diagnostic Publishing

Complexity: Low-medium.

Publish diagnostics only for the latest completed analysis version, coalesce
rapid changes, and avoid repeatedly publishing identical diagnostic sets. Clear
stale diagnostics predictably when a newer successful snapshot completes.

Timing: Do after single-flight analysis and diagnostic/query separation. This
reduces VS Code UI churn and prevents old errors from flickering or lingering
after the source has changed.

## ~~LSP-001 — Improve Member And Property Definition Mapping~~

Complexity: Medium.

Add stronger go-to-definition support for member access, property-style access,
getter/setter surfaces, interface members, inherited members, and aliases that
resolve to members. This should build on the existing symbol-at-position service
before higher-level editing features depend on it.

Timing: Do this while the current member-resolution semantics are fresh, and
before any LSP feature depends on member identity. This should follow compiler
stability for properties, generated `getInterfaceName()` methods, interface
conversion, inherited members, aliases, and callable/member references.

## ~~LSP-002 — Workspace Symbols~~

Complexity: Low-medium.

Implement `workspace/symbol` over the current project snapshot so users can
search declarations by name across the loaded project. Include functions, types,
methods, fields, enum values, aliases, and property names where available.

Timing: Do this once source-level declaration identity is stable enough for
metadata/API output and LSP symbol indexing to agree. It does not need completion,
incremental compilation, or final async/escaped-delegate semantics.

## ~~LSP-003 — Signature Help~~

Complexity: Medium.

Implement `textDocument/signatureHelp` for call expressions. Show callable
signatures, parameter names, default values, thrown/within/sizeof/vtableof
parameters where relevant, and the active parameter when the cursor is inside an
argument list.

Timing: Do this after call binding and argument mapping are stable for named
arguments, default arguments, overloads, expanded params values, `this`
extension calls, callable newtypes, and hidden parameters such as `sizeof`,
`vtableof`, `within`, and `thrown`.

## ~~LSP-004 — Basic Semantic Completion~~

Complexity: Medium-high.

Add conservative completion for:

- local variables and parameters;
- visible functions and types;
- members after `.`;
- enum values in enum-typed contexts;
- keywords only where syntactically useful.

Avoid snippet-heavy or speculative completions in the first pass.

Timing: Do this after symbol-at-position, member resolution, call binding, and
signature help all use the same compiler facts. Defer richer completion until
the compiler has stable behavior for lambdas, generics, `constof`, lifetimes,
interface conversions, and callable transports.

## ~~LSP-005 — Find References~~

Complexity: Medium-high.

Implement `textDocument/references` for source-backed symbols using the semantic
snapshot. Start with locals, parameters, functions, types, fields, methods, and
enum values. Exclude generated declarations and external declarations without
source locations in v1.

Timing: Do this after the compiler exposes stable source-level symbol identities
for declarations and uses. It should wait until generated symbols, flattened
symbols, API-imported declarations, aliases, properties, and interface accessors
are consistently mapped back to their source declarations.

## LSP-006 — Semantic Tokens

Complexity: Medium.

Implement semantic highlighting for declarations, variables, parameters, fields,
methods, type names, enum values, keywords, modifiers, doc-comment links, and
deprecated symbols when metadata is available.

Timing: Do this after the parser and binder expose reliable token-to-symbol and
token-to-role information. It can come before completion, but should wait until
doc comments, metadata attributes, `constof`, lifetime modifiers, callspecs,
typespecs, and fixed-array syntax are all represented consistently.

## LSP-007 — Diagnostics Across Full Project

Complexity: Medium.

Publish diagnostics for all files in the loaded `.campbuild` project, not only
the currently opened document. Track which open document maps to which project
snapshot and avoid publishing stale diagnostics after edits.

Timing: Do this after project loading and compiler diagnostics have stable file
identity/range mapping across source files, included files, generated API
headers, project references, and package inputs. It should also wait until stale
snapshot cancellation is reliable enough to avoid publishing obsolete project
errors.

## LSP-008 — Snapshot Cache And Cancellation Hardening

Complexity: Medium-high.

Cache project snapshots by project root and overlay set, cancel stale analyses
when a newer edit arrives, and debounce full-document changes. Keep the compiler
path read-only and avoid incremental compilation in this stage.

Timing: Do this when full-snapshot analysis becomes a practical blocker for
editing responsiveness. It should precede high-frequency features such as
semantic completion, project-wide diagnostics on every edit, and references over
larger projects.

## LSP-009 — Package And Project Reference Awareness

Complexity: Medium-high.

Improve read-only LSP handling for installed packages, project references, and
missing generated API headers. Surface actionable diagnostics when a project
needs `campc restore` or a referenced project needs to be built.

Timing: Do this after the compiler project model is settled for `.campbuild`,
`#build`, package sources, project references, generated `.camp` API headers,
and metadata artifacts. It is more important once package/reference workflows
are common enough that single-file fallback is insufficient.

## LSP-010 — Code Actions For Simple Diagnostics

Complexity: Medium.

Add narrowly scoped code actions for common diagnostics, such as adding a missing
`using`, replacing an unresolved symbol with a visible candidate, or inserting a
required explicit type argument when inference cannot determine one.

Timing: Do this after diagnostics have precise ranges and stable diagnostic
codes/categories. It should also wait until completion can suggest the same
symbols or syntax that a code action would insert.

## LSP-011 — Rename

Complexity: High.

Implement semantic rename for locals, parameters, private declarations, and then
public/export declarations once API-boundary implications are understood.
Renaming must avoid generated symbols, external declarations, and symbol aliases
unless explicitly supported.

Timing: Do this only after source-level symbol identity and find-references are
stable across locals, members, aliases, properties, imports, API headers, and
generated/flattened symbol surfaces. Rename should not ship before references
can prove the edit set.

## LSP-012 — Formatting

Complexity: High.

Add document/range formatting for Camp source. This requires either a formatter
over syntax trees or a token-preserving pretty-printer that respects comments,
doc comments, preprocessor directives, and preferred declaration style.

Timing: Defer until the compiler has a token-preserving syntax layer or a
dedicated formatter abstraction. Formatting should wait until comments, doc
comments, preprocessor directives, attributes, fixed arrays, lifetime modifiers,
`constof`, callspecs, and typespecs are stable in the grammar.

## LSP-013 — Incremental Compilation

Complexity: Very high.

Introduce true incremental parsing/binding/analysis for editor use. Preserve
stable symbol identities across edits where possible and invalidate only affected
modules or declarations.

Timing: Defer until the compiler architecture has stable declaration identity,
dependency tracking, source overlays, and invalidation boundaries. This becomes
worth doing after full-snapshot LSP features expose real latency bottlenecks.

## LSP-014 — Multi-Root Workspace Model

Complexity: High.

Support multiple independent `.campbuild` roots in one editor workspace, with
per-root options, package state, diagnostics, and symbol indexes.

Timing: Do this after the compiler project model is stable for project
references, packages, generated API headers, global/local `#build` defaults, and
metadata output. It is most valuable once users regularly open multiple related
Camp projects together.

## LSP-015 — Debug/Trace Mode For LSP

Complexity: Low.

Add optional logging for project selection, command-line defaults, loaded build
files, include/API headers, analysis duration, and published diagnostic counts.
Logs should be opt-in and safe to attach to bug reports.

Timing: Add this whenever editor integration bugs become hard to diagnose from
tests alone. It can be done at any point because it does not require additional
compiler semantics, but it should use the shared diagnostic/project-loading
types instead of inventing separate logging state.
