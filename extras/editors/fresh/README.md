# Camp for Fresh

This package adds Camp syntax highlighting and language-server support to
Fresh.

It defines two Fresh languages:

- `camp` for `.camp` files, with syntax highlighting and `camp-lsp`.
- `campbuild` for `.campbuild` files, with syntax highlighting only.

`camp-lsp` must be available on `PATH`. The Camp installer adds the compiler
binary directory to `PATH`; for source-tree development, build Camp and add
`dev/bin` to `PATH` before starting Fresh.

## Install

From a Unix-like shell:

```sh
extras/editors/fresh/install.sh
```

From PowerShell:

```powershell
& "extras\editors\fresh\install.ps1"
```

Fresh also supports installing packages directly from a Git URL.
