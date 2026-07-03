# Camp LSP

`camp-lsp` is the first narrow Camp language server. It speaks LSP over stdio
and is built next to `campc` into the repository `bin/` directory.

## Running

Editor integrations should launch:

```sh
bin/camp-lsp
```

The server uses `workspaceFolders` or `rootUri` when the client supplies them,
but the opened document is the authoritative entry point for v1 project
selection.

## Project Loading

For an opened `.camp` file, the server searches upward for a `.campbuild` file.
If one is found, the server loads that project using the same read-only build
option parser used by compiler tooling:

- source patterns and `--include`
- source-file and `lib/global.camp` `#build` pragmas
- `--define`, target, profile, memory model, and metadata options
- installed package references and project reference discovery

The LSP server does not restore packages, build referenced projects, emit C, or
run a native compiler. Missing package/API files are surfaced as diagnostics
instead of repaired.

If no `.campbuild` file is found, the server treats the opened file as a
single-file project using the normal compiler defaults, including the standard
library package.

## Implemented V1 Features

- full-document text sync
- diagnostics after open, change, and save
- hover from source declarations and doc comments
- go-to definition for simple source-backed symbols
- document symbols for source declarations, including nested type members and
  enum values

Definition lookup intentionally returns source locations only. Generated symbols
and external declarations without source ranges do not produce a definition
location.

## Async Notes

The LSP view is source-level. Async declarations should be surfaced as the
programmer wrote them, including `async`, `once`, `@resumewith`, `@noawait`,
ordinary resumer parameters, and ordinary completion/catch syntax. Generated
async frames, resume helpers, completion helper functions, resumer invocation
thunks, postponed-call context types, and lambda capture-context implementation
details are compiler internals and should not appear as user declarations in
document symbols, hover, or definition results.

Future semantic features may highlight suspension points and async frame
captures, but those should be annotations on the source construct rather than
separate generated declarations. Async iterators and `await foreach` are
reserved/deferred; until implemented, LSP diagnostics should mirror compiler
diagnostics rather than suggesting generated async-iterator surfaces.

## Deferred

The v1 server does not implement semantic completion, rename, references,
formatting, semantic tokens, workspace symbols, package restore, native builds,
or incremental compilation. Document symbols are source-level and intentionally
conservative. Each document change rebuilds the affected semantic snapshot.
