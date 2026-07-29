# Camp for Fresh

This package adds Camp syntax highlighting and language-server support to
Fresh.

It defines two Fresh languages:

- `camp` for `.camp` files, with syntax highlighting and `camp-lsp`.
- `campbuild` for `.campbuild` files, with syntax highlighting only.

`camp-lsp` must be available on `PATH`. The Camp installer adds the compiler
binary directory to `PATH`; for source-tree development, build Camp and add
`dev/bin` to `PATH` before starting Fresh.

## Local Development

Validate the package:

```sh
./validate.sh
```

Install it in Fresh from a local checkout:

1. Open Fresh.
2. Open the command palette with `Ctrl+P`, then type `>`.
3. Run `Package: Install from URL`.
4. Enter the full path to this directory.

Fresh also supports installing packages directly from a Git URL.
