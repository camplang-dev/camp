# Camp Language Support For VS Code

This extension provides Camp syntax highlighting, language-server integration,
and simple build/run commands.

## Features

- Syntax highlighting for `.camp` and `.campbuild` files. Only `.camp` source
  files are sent to the language server.
- LSP diagnostics, hover, go to definition, references, document symbols,
  workspace symbols, signature help, and completion through `camp-lsp`.
- `Camp: Build Current Project` and `Camp: Run Current Project` commands.
- Editor title buttons and status-bar buttons for build/run while a Camp file is
  active.
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

On Windows, a running language-server process can lock `camp-lsp.exe` or loaded
assemblies. If rebuild fails because files are in use, run
`Camp: Restart Language Server`, reload the VS Code window, or close VS Code and
build again.

For extension development:

```sh
cd extras/vscode-camp
npm install
code .
```

Press `F5` to launch an Extension Development Host.

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
