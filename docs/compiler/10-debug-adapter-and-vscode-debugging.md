# Debug Adapter And VS Code Debugging

Camp debugging is split across compiler artifacts, a Debug Adapter Protocol
process, and editor integration.

The compiler emits native C and native debug information as usual, then adds
Camp-specific mapping data when a build requests debug artifacts. The debug
adapter uses those artifacts to translate ordinary debugger concepts such as
breakpoints, stack frames, scopes, and variables back into source-level Camp
terms.

## Build Requirements

Build the toolchain first:

```sh
dotnet build src/camplang.sln
```

The repository build copies the main tools into `bin/`:

- `campc`
- `camp-lsp`
- `camp-dap`

`camp-dap` is a separate executable. Editors launch it over stdio as a Debug
Adapter Protocol server.

## Debug Artifacts

Debug builds should use:

```sh
campc build app.campbuild --profile DEBUG --artifact exec --debug-info
```

`--debug-info` asks the compiler to:

- emit generated C with useful `#line` directives;
- produce native debug information through the target's debug profile;
- write a `*.campdebug.json` file beside the generated native artifact.

The `*.campdebug.json` file is a compiler tooling artifact. It maps source
functions, generated C locations, native symbols, and directly known local or
parameter names. It is intentionally conservative: if a lowered value cannot be
shown accurately, the debugger should omit or simplify it rather than invent a
source-level view.

## Debug Adapter

`camp-dap` speaks Debug Adapter Protocol over stdio. It accepts launch
configurations with this shape:

```json
{
  "name": "Debug Camp",
  "type": "camp",
  "request": "launch",
  "project": "/path/to/app.campbuild",
  "cwd": "/path/to/project",
  "args": [],
  "stopOnEntry": false,
  "backend": "lldb"
}
```

`project` may be either a `.campbuild` file or a loose `.camp` source file.
`cwd` is the working directory used for the build and debug launch. `args` are
program arguments. `stopOnEntry` requests a stop at the native entry point.

The `backend` value selects the native debugger backend:

- `auto`: selects the first supported backend for the current platform.
- `lldb`: macOS LLDB backend.
- `gdb`: reserved for the Linux backend phase.
- `cdb`: reserved for the Windows backend phase.

At the current stage, `auto` selects `lldb` on macOS. Linux/GDB and Windows/CDB
are planned but not active yet.

## macOS LLDB MVP

The first real backend is macOS LLDB. It supports:

- building the Camp project with `--profile DEBUG --artifact exec --debug-info`;
- binding breakpoints in `.camp` files for simple source-backed locations;
- launching under LLDB;
- continuing and stepping in basic cases;
- stack traces mapped back to Camp file and line where native debug information
  and `#line` mappings are reliable;
- simple parameter and local display using Camp source names from
  `*.campdebug.json`;
- simple evaluation by mapped local or parameter name.

The LLDB backend is intentionally narrow. It favors honest omission over wrong
values. Unsupported expression evaluation returns an unsupported result instead
of trying to compile arbitrary Camp expressions.

## VS Code Integration

The VS Code extension in `extras/vscode-camp` contributes:

- Camp breakpoints;
- debugger type `camp`;
- command `Camp: Debug Current Project`;
- status-bar button `Camp Debug`;
- settings:
  - `camp.server.path`;
  - `camp.debugAdapter.path`;
  - `camp.debug.nativeBackend`.

The extension derives `camp-dap` from `camp.server.path` by default. For
example, if `camp.server.path` is `/repo/bin/camp-lsp`, then the default debug
adapter is `/repo/bin/camp-dap`. Set `camp.debugAdapter.path` only when the
adapter is somewhere else.

`Camp: Debug Current Project` saves the active Camp document, searches upward
for the nearest `.campbuild`, and starts a VS Code debug session. If no
`.campbuild` exists, the command debugs the active `.camp` file directly.

## Known V1 Limits

- macOS LLDB is the first supported real backend.
- Linux/GDB and Windows/CDB are later phases.
- Expression evaluation is limited to simple mapped locals and parameters.
- Async and iterator call stacks are not polished yet.
- Some lowered/generated regions may still appear while stepping.
- Expanded forms are displayed only when debug metadata makes their components
  clear enough to avoid misleading output.

## Testing Guidance

Use targeted DAP tests while changing adapter behavior:

```sh
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll --Tests:Dap_fake_backend_serves_basic_debug_requests,Dap_lldb_backend_launches_and_stops_on_camp_breakpoint_when_available
```

Use extension checks while changing VS Code integration:

```sh
cd extras/vscode-camp
npm run check
npm test
```

Before committing a phase, run the full local compiler test suite:

```sh
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll
```
