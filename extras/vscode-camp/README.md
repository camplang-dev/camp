# Camp Language Support For VS Code

This extension provides Camp syntax highlighting, language-server integration,
simple build/run commands, and debug launch integration.

## Features

- Syntax highlighting for `.camp` and `.campbuild` files. Only `.camp` source
  files are sent to the language server.
- LSP diagnostics, hover, go to definition, references, document symbols,
  workspace symbols, signature help, and completion through `camp-lsp`.
- `Camp: Build Current Project`, `Camp: Run Current Project`, and `Camp: Debug
  Current Project` commands.
- Editor title buttons and status-bar buttons for build/run/debug while a Camp
  file is active.
- Camp breakpoints and generated launch configurations for the `camp-dap`
  debug adapter.
- `Camp: Restart Language Server`.

## Prerequisites

Build Camp first:

```sh
cd /path/to/camplang
dotnet build src/camplang.sln
```

The extension needs the path to `camp-lsp`. If you use the repository build
layout, that is usually:

- macOS/Linux: `/path/to/camplang/bin/camp-lsp`
- Windows: `C:\path\to\camplang\bin\camp-lsp.exe`

The build and run commands derive `campc` from the same directory as
`camp-lsp`. If `camp.server.path` is just `camp-lsp`, the commands use `campc`
from `PATH`.

Debug sessions use `camp-dap`. By default, the extension derives `camp-dap`
from the same directory as `camp.server.path`, just like it derives `campc`.
For example, if `camp.server.path` is `/path/to/camplang/bin/camp-lsp`, the
debug adapter path defaults to `/path/to/camplang/bin/camp-dap`.

You can override this with:

```json
{
  "camp.debugAdapter.path": "/path/to/camplang/bin/camp-dap"
}
```

The first real native backend is macOS LLDB. On macOS, use:

```json
{
  "camp.debug.nativeBackend": "lldb"
}
```

`"auto"` currently selects LLDB on macOS. Linux/GDB and Windows/CDB are planned
for later backend phases.

## Install From Source

From the Camp repository:

```sh
cd extras/vscode-camp
npm install
npm run package
code --install-extension vscode-camp-0.0.1.vsix --force
```

Then configure the server path in VS Code settings:

```json
{
  "camp.server.path": "/path/to/camplang/bin/camp-lsp"
}
```

On Windows:

```json
{
  "camp.server.path": "C:\\path\\to\\camplang\\bin\\camp-lsp.exe"
}
```

Reload VS Code after installing or changing the server path.

## Development Workflow

For normal Camp compiler/LSP iteration:

```sh
cd /path/to/camplang
dotnet build src/camplang.sln
```

Then run `Camp: Restart Language Server` from the Command Palette, or reload the
VS Code window.

Language-server performance tracing is off by default. To enable it, set:

```json
"camp.server.trace": true
```

When tracing is enabled, each language-server process writes a fresh performance
trace file when it starts. By default, VS Code trace files are written under the
extension global storage folder in `lsp-traces/`. Run `Developer: Open Extension
Logs Folder` or `Developer: Open User Data Folder` from the Command Palette,
then look for the Camp extension storage directory and its newest
`camp-lsp-*.jsonl` file. Send that file when reporting slow hover, completion,
signature help, or diagnostics.

To write trace files somewhere else, set:

```json
"camp.server.traceDirectory": "C:\\Code\\_temp\\camplang\\logs"
```

On Windows, a running language-server process can lock `camp-lsp.exe` or loaded
assemblies. If rebuild fails because files are in use, run
`Camp: Restart Language Server`, reload the VS Code window, or close VS Code and
build again.

## Run Without Installing The VSIX

You can run the extension directly from source in a VS Code Extension
Development Host. This is the fastest way to work on the extension itself
without packaging and installing a `.vsix` each time.

```sh
cd extras/vscode-camp
npm install
code .
```

In the VS Code window that opens:

1. Open the Run and Debug view.
   - macOS: `Cmd+Shift+D`
   - Windows/Linux: `Ctrl+Shift+D`
2. Select `Launch Extension`.
3. Click the green play button, or run `Run: Start Debugging`.

On some Mac keyboards, plain `F5` opens a system or editor menu instead of
starting VS Code debugging. Use the Run and Debug sidebar, the menu item, or
`fn+F5`.

VS Code opens a second window named Extension Development Host. That second
window is running the extension from this source folder. Open a Camp project in
that second window and configure:

```json
{
  "camp.server.path": "/path/to/camplang/bin/camp-lsp"
}
```

When extension source changes, stop the debug session and launch
`Launch Extension` again. When only `camp-lsp` changes, rebuild Camp and run
`Camp: Restart Language Server` in the Extension Development Host.

When only `camp-dap` changes, rebuild Camp. New debug sessions will start the
new adapter executable. Existing debug sessions keep using the adapter process
that was already started for that session.

## Build And Run Commands

`Camp: Build Current Project` and `Camp: Run Current Project` look at the active
Camp file and walk upward to find the nearest `.campbuild` file. If the active
file is itself a `.campbuild`, that file is used directly. If a build file is
found, the extension runs:

```sh
campc build /path/to/project.campbuild
campc run /path/to/project.campbuild
```

If no `.campbuild` file is found, the extension falls back to the active `.camp`
file:

```sh
campc build /path/to/file.camp
campc run /path/to/file.camp
```

Output is sent to a VS Code terminal named `Camp`.

## Debugging

Set breakpoints in `.camp` files as usual. Then run `Camp: Debug Current
Project`, click the `Camp Debug` status-bar button, or create/use a VS Code
launch configuration of type `camp`.

The command uses the active Camp file and walks upward to find the nearest
`.campbuild`, matching the build/run commands. If no `.campbuild` is found, it
debugs the active `.camp` file directly.

A typical launch configuration is:

```json
{
  "name": "Debug Camp",
  "type": "camp",
  "request": "launch",
  "project": "${workspaceFolder}/app.campbuild",
  "cwd": "${workspaceFolder}",
  "args": [],
  "stopOnEntry": false,
  "backend": "lldb"
}
```

For now, the debug adapter builds with Camp debug metadata and uses LLDB on
macOS. Breakpoints, basic stepping, stack frames, and simple scalar
locals/parameters are supported. Expression evaluation is intentionally narrow:
simple mapped local and parameter names work, while arbitrary Camp expressions
return a clear unsupported result.
