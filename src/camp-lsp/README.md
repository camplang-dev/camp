# Camp LSP Editor Setup

`camp-lsp` is the Camp language server. It speaks LSP over stdio and is built
next to `campc` into the repository `bin/` directory.

Build or update it with:

```sh
cd /path/to/camplang
dotnet build src/camplang.sln
```

The executable is:

- macOS/Linux: `/path/to/camplang/bin/camp-lsp`
- Windows: `C:\path\to\camplang\bin\camp-lsp.exe`

## Features

The current server provides:

- diagnostics
- hover text
- hover documentation from Camp doc comments
- go-to definition for simple source-backed symbols
- document symbols for outlines/navigation

The current server does not provide completion, rename, references, formatting,
semantic tokens, workspace symbols, package restore, native builds, or
incremental compilation.

## Sublime Text

Sublime Text uses the `LSP` package as its generic LSP client.

Install the package:

1. Open the Command Palette.
2. Run `Package Control: Install Package`.
3. Install `LSP`.

Install or update Camp syntax highlighting:

1. Copy `extras/Camp.sublime-syntax` into Sublime's user package folder.
2. On macOS, that folder is usually:

   ```text
   ~/Library/Application Support/Sublime Text/Packages/User/
   ```

3. On Windows, that folder is usually:

   ```text
   %APPDATA%\Sublime Text\Packages\User\
   ```

Configure the LSP client. Open the Command Palette and run
`Preferences: LSP Settings`, then add or merge this into the user settings.

macOS example:

```json
{
  "clients": {
    "camp-lsp": {
      "enabled": true,
      "command": [
        "/Users/andrew/Projects/camplang/bin/camp-lsp"
      ],
      "selector": "source.camp"
    }
  }
}
```

Windows example:

```json
{
  "clients": {
    "camp-lsp": {
      "enabled": true,
      "command": [
        "C:\\Code\\camplang\\bin\\camp-lsp.exe"
      ],
      "selector": "source.camp"
    }
  }
}
```

Use the actual path to your local checkout.

To update the server:

1. Stop or restart the server from Sublime with `LSP: Restart Servers`, or quit
   Sublime.
2. Rebuild Camp with `dotnet build src/camplang.sln`.
3. Reopen a `.camp` file or run `LSP: Restart Servers`.

On macOS, a running process normally does not prevent rebuilding the executable.
On Windows, the running process may keep the executable or loaded assemblies
locked. If rebuild fails because files are in use, stop the server or close
Sublime, then rebuild.

## Micro

Micro uses the community `lsp` plugin.

Install the plugin:

```sh
micro -plugin install lsp
```

Install or update Camp syntax highlighting:

```sh
mkdir -p ~/.config/micro/syntax
cp extras/camp.yaml ~/.config/micro/syntax/camp.yaml
```

Configure the language server in:

```text
~/.config/micro/settings.json
```

Add or merge:

```json
{
  "lsp.server": "camp=/Users/andrew/Projects/camplang/bin/camp-lsp"
}
```

If `lsp.server` already contains other servers, keep them in the same
comma-separated string:

```json
{
  "lsp.server": "go=gopls,rust=rust-analyzer,camp=/Users/andrew/Projects/camplang/bin/camp-lsp"
}
```

Micro's LSP plugin can also be configured with `MICRO_LSP`, which overrides the
setting:

```sh
export MICRO_LSP='camp=/Users/andrew/Projects/camplang/bin/camp-lsp'
```

Open a `.camp` file and confirm Micro detects the filetype as `camp`. If it
does not, install `extras/camp.yaml` as shown above, then reopen the file.

For loose `.camp` files without a `.campbuild`, the server uses normal compiler
defaults, including the standard library package. A nearby `.campbuild` can
still disable this with `--nostdlib`.

To update the server:

1. Exit Micro, or kill the running language server:

   ```sh
   pkill -f camp-lsp
   ```

2. Rebuild Camp:

   ```sh
   dotnet build src/camplang.sln
   ```

3. Reopen Micro.

On macOS and Linux, rebuilding usually works even if the old server process is
still running, but restarting the process ensures the editor uses the newly
built server. On Windows, stop the server before rebuilding if the executable is
locked.
