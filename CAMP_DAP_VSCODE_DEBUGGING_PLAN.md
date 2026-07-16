# Camp DAP And VS Code Debugging Plan

## Summary

Implement Camp debugging as a staged toolchain feature:

- compiler-emitted source/debug metadata;
- a new C# `camp-dap` executable that speaks Debug Adapter Protocol;
- native debugger backends, beginning with macOS `lldb`;
- VS Code integration in `extras/vscode-camp`;
- later Linux `gdb`, Windows `cdb.exe`, richer variable display, and async/iterator polish.

The end goal is the same as the original plan: Camp projects can be launched from VS Code, breakpoints bind in `.camp` files, stepping mostly follows Camp source instead of generated C scaffolding, and debugger state is mapped back to Camp source names and source-level values where practical.

This plan is organized for implementation. The early phases intentionally narrow the first usable milestone to compiler debug artifacts plus an LLDB-backed MVP, then expand to the remaining debugger backends and richer user experience.

## Default Choices

- Add `src/camp-dap/camp-dap.csproj`.
- Use `OmniSharp.Extensions.DebugAdapter` `0.19.9` for DAP protocol support, matching the existing LSP package family.
- If an early spike shows server-side ergonomics are unsuitable, switch promptly to `Microsoft.VisualStudio.Shared.VSCodeDebugProtocol`.
- Emit Camp debug metadata through `campc build --debug-info`.
- `camp-dap` builds programs with `--profile DEBUG --artifact exec --debug-info`.
- Native backend order:
  - Phase 3: macOS `lldb`.
  - Phase 7: Linux `gdb`.
  - Phase 8: Windows `cdb.exe` from Windows Debugging Tools.

## Phase 1 — Compiler Debug Artifact Foundation

### Goals

Add the compiler output needed by any debugger adapter before building DAP protocol machinery.

### Implementation

- Add `campc build --debug-info`.
- Ensure debug builds use native compiler flags suitable for source debugging:
  - no or low optimization;
  - native debug information enabled;
  - predictable generated C locations.
- Emit `#line` directives in generated C for source-backed Camp functions and statements.
- Emit a `*.campdebug.json` artifact beside the native build output.
- Record enough metadata for v1 source mapping:
  - Camp source file/range to generated C file/line/function;
  - Camp function names to generated C symbols;
  - Camp local/parameter names to generated C/native names where directly knowable;
  - compiler-owned/generated regions to hide or step over;
  - build target and native artifact path.
- Start with conservative mappings. It is better for v1 to omit uncertain mappings than to confidently show wrong state.

### Tests

- Golden C emission tests for `#line`.
- Unit tests for `.campdebug.json` shape and deterministic content.
- Command-line tests proving `campc build --debug-info` writes debug artifacts.
- Tests for generated/compiler-owned hidden regions.
- Tests for local/parameter name mapping for simple functions.

### Completion Criteria

- `campc build --debug-info` succeeds for a simple executable and produces generated C, native output, and `*.campdebug.json`.
- Generated C contains useful `#line` directives for simple source statements.
- The debug JSON maps at least simple functions, source ranges, and directly emitted locals/parameters.
- Existing non-debug builds are unchanged.
- Targeted tests and full local suite pass.
- Commit.

## Phase 2 — `camp-dap` Skeleton And Fake Backend — Complete

### Goals

Create the DAP executable and protocol surface without depending on a real native debugger yet.

### Implementation

- Add `src/camp-dap/camp-dap.csproj`.
- Include it in `src/camplang.sln`.
- Copy `camp-dap` to repo `bin/` after build like `camp-lsp`.
- Reference:
  - `Camp.Compiler`;
  - `OmniSharp.Extensions.DebugAdapter`.
- Implement DAP request plumbing:
  - `initialize`;
  - `launch`;
  - `setBreakpoints`;
  - `configurationDone`;
  - `continue`;
  - `pause`;
  - `next`;
  - `stepIn`;
  - `stepOut`;
  - `threads`;
  - `stackTrace`;
  - `scopes`;
  - `variables`;
  - `evaluate`;
  - `disconnect`.
- Add launch configuration parsing:
  - `project`;
  - `cwd`;
  - `args`;
  - `stopOnEntry`;
  - `backend`.
- Implement backend abstraction:
  - launch/build operation;
  - set breakpoints;
  - continue/pause/step;
  - stack frames;
  - scopes/variables;
  - evaluate;
  - disconnect/cleanup.
- Add a fake backend for deterministic protocol tests.
- Add build failure and missing-debugger diagnostic paths even if real backends are not active yet.

### Tests

- Protocol tests over stdio, modeled after existing LSP server tests.
- Fake-backend tests for:
  - launch;
  - set breakpoints;
  - stepping;
  - stack traces;
  - scopes/variables;
  - evaluate;
  - disconnect.
- Build failure diagnostics test.
- Missing-debugger diagnostics test through backend selection.

### Completion Criteria

- ~~`camp-dap` starts, initializes, accepts launch config, and serves fake-backend protocol requests.~~
- ~~Protocol tests do not require `lldb`, `gdb`, or `cdb`.~~
- ~~Existing compiler and LSP behavior is unchanged.~~
- ~~Targeted tests and full local suite pass.~~
- ~~Commit.~~

## Phase 3 — macOS LLDB MVP — Complete

### Goals

Deliver the first real debugger backend on the platform easiest to iterate locally.

### Implementation

- Add macOS `lldb` backend.
- Have `camp-dap` build the project with:
  - `--profile DEBUG`;
  - `--artifact exec`;
  - `--debug-info`.
- Launch the executable under `lldb`.
- Set source breakpoints using debug metadata and/or `#line`-enabled native debug info.
- Implement:
  - continue;
  - pause;
  - next;
  - step in;
  - step out;
  - threads;
  - stack trace;
  - disconnect.
- Map native frames back to Camp source where metadata is reliable.
- Hide or step over compiler-owned/generated regions where v1 metadata supports it.
- Keep variable display minimal in this phase; stack and stepping are the priority.

### Tests

- Platform-gated LLDB smoke tests on macOS when `lldb` is available.
- Launch a tiny Camp executable, stop on breakpoint, continue, step, and disconnect.
- Verify stack frames report Camp file/line for a simple function call.
- Verify build failure is reported as a DAP error/event instead of hanging.

### Completion Criteria

- ~~A simple Camp program can be debugged through `camp-dap` on macOS with LLDB.~~
- ~~Breakpoints in `.camp` files bind and stop in expected simple cases.~~
- ~~Stack traces show Camp source locations for simple functions.~~
- ~~Generated regions do not dominate ordinary stepping in simple cases.~~
- ~~Targeted tests and full local suite pass.~~
- ~~Commit.~~

## Phase 4 — Local Inspection And Evaluation V1 — Complete

### Goals

Make stopped debug sessions useful by showing Camp-named locals and parameters.

### Implementation

- Use `*.campdebug.json` to map native locals/parameters back to Camp source names.
- Implement DAP scopes:
  - parameters;
  - locals;
  - optional raw/native scope if useful for debugging the debugger.
- Implement DAP variables for simple scalar values and pointers.
- Display expanded Camp forms initially as structured fields:
  - arrays/spans;
  - delegates;
  - optionals.
- Add simple evaluation support:
  - local names;
  - parameter names;
  - simple native-compatible expressions when safely forwarded to the backend.
- Do not compile arbitrary Camp expressions in v1.
- Prefer honest omission over wrong values for generics, hidden params, and complex lowered state.

### Tests

- Fake-backend tests for scopes, variables, and evaluate.
- LLDB-gated smoke test for simple locals and parameters.
- Tests for expanded Camp forms appearing as structured fields where metadata is available.
- Tests for unsupported evaluation returning a clear message.

### Completion Criteria

- ~~Stopped sessions show simple parameters and locals using Camp names.~~
- ~~Simple local/parameter evaluation works.~~
- ~~Expanded forms do not appear as random hidden native slots when metadata is available.~~
- ~~Unsupported evaluation fails gracefully.~~
- ~~Targeted tests and full local suite pass.~~
- ~~Commit.~~

## Phase 5 — VS Code Debug Integration — Complete

### Goals

Expose Camp debugging through the existing VS Code extension in the normal VS Code debug UX.

### Implementation

- Update `extras/vscode-camp` to contribute:
  - breakpoints for `camp`;
  - debugger type `camp`;
  - command `Camp: Debug Current Project`;
  - status bar button `Camp Debug`;
  - settings:
    - `camp.debugAdapter.path`;
    - `camp.debug.nativeBackend`: `auto`, `lldb`, `gdb`, `cdb`.
- Derive `camp-dap` from the configured `camp.server.path` directory by default, like `campc` is currently derived.
- Add a VS Code debug configuration provider so F5 can create a default Camp launch config.
- `Camp Debug` behavior:
  - save active Camp document;
  - find nearest `.campbuild` using existing build/run discovery behavior;
  - fall back to the active `.camp` file if no build file exists;
  - start VS Code debugging with type `camp`.
- Support launch config:

```json
{
  "name": "Debug Camp",
  "type": "camp",
  "request": "launch",
  "project": "${workspaceFolder}/app.campbuild",
  "cwd": "${workspaceFolder}",
  "args": [],
  "stopOnEntry": false,
  "backend": "auto"
}
```

### Tests

- `npm run check`.
- Unit-testable helpers for:
  - `camp-dap` path derivation;
  - project discovery;
  - launch config construction.
- Manual Extension Development Host smoke checklist:
  - install/run extension;
  - set breakpoint;
  - click `Camp Debug`;
  - hit breakpoint;
  - step;
  - inspect simple variables.

### Completion Criteria

- ~~VS Code recognizes Camp breakpoints.~~
- ~~`Camp Debug` launches a debug session for a `.campbuild` project and a loose `.camp` file.~~
- ~~F5 can use a generated/default Camp launch configuration.~~
- ~~Extension README includes setup instructions.~~
- ~~`npm run check`, targeted tests, and full local suite pass.~~
- ~~Commit.~~

## Phase 6 — Documentation And First Usable Debugging Guide — Complete

### Goals

Document the macOS/LLDB MVP before adding more backends, so users and future agents have a stable baseline.

### Implementation

- Add `docs/compiler/10-debug-adapter-and-vscode-debugging.md`.
- Update `docs/compiler/index.md`.
- Add a short DAP cross-reference to `docs/compiler/08-language-server-and-editor-tooling.md`.
- Update `extras/vscode-camp/README.md` with:
  - build instructions: `dotnet build src/camplang.sln`;
  - `camp.server.path`;
  - `camp.debugAdapter.path`;
  - `camp.debug.nativeBackend`;
  - platform debugger prerequisites;
  - Extension Development Host workflow;
  - known v1 limitations.
- Document v1 limits clearly:
  - macOS LLDB is the first supported real backend;
  - expression evaluation is limited;
  - async/iterator call stacks are not yet polished;
  - some lowered/generated regions may still appear.

### Tests

- Link/path sanity checks where existing docs tooling supports it.
- Manual smoke checklist remains current.

### Completion Criteria

- ~~Compiler docs and VS Code README explain how to install, configure, and use Camp debugging.~~
- ~~Known limitations are explicit.~~
- ~~Full local suite passes.~~
- ~~Commit.~~

## Phase 7 — Linux GDB Backend — Complete

### Goals

Add the second real backend after the Camp debug map and LLDB implementation have proven the model.

### Implementation

- Add Linux `gdb` backend.
- Reuse the backend abstraction from Phase 2.
- Support the same core operations as LLDB:
  - launch;
  - breakpoints;
  - continue/pause;
  - next/step in/step out;
  - threads;
  - stack trace;
  - scopes/variables/evaluate where practical.
- Respect Linux GCC target debug flags.
- Keep behavior aligned with LLDB where DAP semantics are shared.

### Tests

- Platform-gated GDB smoke tests on Linux/WSL when `gdb` is available.
- Launch/breakpoint/step/stack test.
- Simple variables/evaluate test.
- Existing fake-backend protocol tests remain backend-independent.

### Completion Criteria

- ~~A simple Camp program can be debugged through `camp-dap` on Linux with GDB.~~
- ~~Breakpoints and stack traces map to Camp source in simple cases.~~
- ~~Local/parameter inspection works at least as well as the LLDB v1 equivalent.~~
- ~~Full local suite and Linux lane pass.~~
- ~~Commit.~~

## Phase 8 — Windows CDB Backend — Complete

### Goals

Add the Windows/MSVC backend using Windows Debugging Tools.

### Implementation

- Add Windows `cdb.exe` backend.
- Support MSVC debug builds.
- Ensure generated C/PDB/debug info works with Camp `#line` and debug metadata.
- Implement the same core DAP operations:
  - launch;
  - breakpoints;
  - continue/pause;
  - next/step in/step out;
  - threads;
  - stack trace;
  - scopes/variables/evaluate where practical;
  - disconnect.
- Add clear diagnostics when Windows Debugging Tools are missing or the MSVC debug environment is not configured.
- MinGW/GDB-on-Windows is not the primary v1 path.

### Tests

- Platform-gated CDB smoke tests on Windows when `cdb.exe` is available.
- Launch/breakpoint/step/stack test.
- Simple variables/evaluate test.
- Missing-debugger and missing-MSVC-environment diagnostics tests.

### Completion Criteria

- ~~A simple Camp program can be debugged through `camp-dap` on Windows/MSVC with CDB when Windows Debugging Tools are installed.~~
- ~~Breakpoints and stack traces map to Camp source in simple cases when `cdb.exe` is available.~~
- ~~Clear diagnostics are reported for missing Windows debugger prerequisites.~~
- ~~Full local suite and Windows lane pass.~~
- ~~Commit.~~

Note: the Windows validation host used for this phase does not currently have `cdb.exe` installed, so the real CDB smoke test is platform/tool-gated there. The missing-debugger diagnostic path is covered on that host.

## Phase 9 — Debug UX Hardening And Pretty Printing

### Goals

Improve the everyday debugging experience after all core backends exist.

### Implementation

- Improve hidden/generated region handling.
- Improve stepping through:
  - constructors/destructors;
  - cleanup/finally delete;
  - generated interface fixups;
  - delegate/lambda thunks;
  - iterator state machines where possible.
- Add richer pretty-printing for:
  - arrays/spans;
  - strings;
  - delegates;
  - optionals;
  - fixed arrays;
  - newtypes;
  - interface pointers/vtables where useful.
- Add clearer display names for compiler-generated locals when they cannot be hidden.
- Add backend capability flags so unsupported features fail consistently.

### Tests

- Fake-backend tests for pretty-printer shape.
- Backend-gated smoke tests for representative values.
- Step-over tests for hidden/generated regions where stable enough.

### Completion Criteria

- Common Camp values display in a source-friendly shape.
- Stepping avoids most generated scaffolding in ordinary code.
- Unsupported backend features produce clear messages.
- Full local suite plus active platform lanes pass.
- Commit.

## Phase 10 — Async, Iterators, And Advanced Runtime Views

### Goals

Polish the debugging model for the language features whose lowering is intentionally more complex.

### Implementation

- Improve async debugging:
  - logical source locations;
  - async frame locals;
  - completion/resumption state;
  - later logical async call stack where feasible.
- Improve iterator debugging:
  - current state;
  - yielded value;
  - source locations inside generated state machines;
  - reasonable stepping behavior around `yield`.
- Improve lambda/delegate views:
  - captured context fields;
  - delegate target names;
  - scoped/escaped context display where metadata can describe it.

### Tests

- Focused async/iterator/lambda debug-map tests.
- Fake-backend variable/stack tests for advanced views.
- Backend-gated smoke tests only where stable.

### Completion Criteria

- Async and iterator debugging is source-oriented enough for normal diagnosis.
- Complex lowered state is explained or hidden instead of appearing as unexplained native clutter.
- Full local suite plus relevant platform lanes pass.
- Commit.

## Cross-Cutting Test Strategy

- Compiler tests:
  - `#line` C emission goldens;
  - `.campdebug.json` unit/golden tests;
  - command-line artifact tests;
  - generated/compiler-owned region tests;
  - local/parameter mapping tests.
- `camp-dap` tests:
  - stdio protocol tests;
  - fake-backend tests for all DAP requests;
  - build failure diagnostics;
  - missing debugger diagnostics;
  - backend-gated smoke tests.
- VS Code extension tests/checks:
  - `npm run check`;
  - helper tests where practical;
  - manual Extension Development Host smoke checklist.
- Before commits:
  - targeted tests for the touched phase;
  - full local suite;
  - platform lanes only for phases affecting that platform/backend.

## Assumptions

- DAP transport is stdio.
- `camp-dap` is a separate executable, not a `campc` subcommand.
- `camp-dap` may invoke `campc build`, but it should not silently restore or mutate package state beyond normal build outputs.
- v1 evaluation supports locals and simple expressions, not arbitrary Camp expression compilation.
- v1 variable display is allowed to be conservative.
- `#line` may carry more of the source-location burden than the debug JSON, but the debug JSON remains the authority for Camp-specific names, hidden/generated regions, and expanded/lowered value interpretation.
- Async debugging may show logical source locations before it has a fully polished async call-stack view.
