# `campc` Command Line

## Command Overview

`campc` is the Camp command-line compiler. It uses subcommands:

```sh
campc build [pattern.camp...] [options]
campc run [pattern.camp...] [options]
campc dump <kind> [pattern.camp...] [options]
campc restore [pattern.camp...]
campc pkg <command> [...]
campc help [command]
```

Source patterns may be file paths or globs. Build files can be supplied through
response-file syntax with `@file.campbuild`.

## `build`

`build` compiles Camp sources, emits C, and optionally builds a native artifact.
When no artifact is specified, the compiler infers an executable if it finds an
exported or public `main`; otherwise it builds a static library.

Common examples:

```sh
campc build src/main.camp --target gcc-linux-x64
campc build @app.campbuild
campc build src/lib.camp --artifact static --name textlib
```

## `run`

`run` builds an executable and runs it.

```sh
campc run samples/hello.camp
```

`run` requires an executable artifact. If the artifact option is omitted, it
defaults to an executable.

## `dump`

`dump` prints compiler intermediate output. Valid dump kinds are `tokens`,
`cst`, `ast`, `declarations`, `lowering`, and `metadata`.

```sh
campc dump lowering source.camp
campc dump declarations source.camp --xml
```

`--xml` applies only to declaration and lowering dumps.

## `restore`

`restore` installs missing packages used by source files and build pragmas into
the local package cache.

```sh
campc restore @app.campbuild
```

## `pkg`

`pkg` manages package sources and package dependencies. Subcommands include
`add-source`, `remove-source`, `search`, `install`, `uninstall`, `add`, and
`remove`.

```sh
campc pkg add-source local-libs ../packages --local app.campbuild
campc pkg search text
campc pkg install textlib@1.2.0
campc pkg add textlib@1.2.0 app.campbuild
```

## `help`

`help` prints command help.

```sh
campc help
campc help build
```

## Common Build Options

Common options include:

| Option | Meaning |
|---|---|
| `--include`, `-i` | Include Camp API headers or source patterns. |
| `--exclude` | Exclude source file patterns. |
| `--target`, `-t` | Select the target. |
| `--profile`, `-p` | Select `DEBUG` or `RELEASE`. |
| `--variant`, `-v` | Select target variants. |
| `--define`, `-d` | Define conditional compilation symbols. |
| `--emit` | Select emitter, currently `c99`. |
| `--nostdlib` | Omit the standard library package. |
| `--reference`, `-r` | Reference a native static library during linking. |
| `--use`, `-u` | Use an installed package. |
| `--use-source` | Define a package source name and local path. |
| `--project-reference` | Build and reference another Camp project. |
| `--metadata` | Emit metadata: `none`, `export`, `public`, or `all`. |
| `--explicit-within` | Require explicit `within` for source allocation. |
| `--implicit-within` | Allow default allocation without explicit `within`. |

Build-only options include `--framework`, `--artifact`, `--name`,
`--subsystem`, and `--out-dir`.

## Exit Codes And Diagnostics

`campc` returns `0` on success and `1` when startup, parsing, semantic,
metadata, emission, native build, package, or project-reference processing
fails. Diagnostics are printed to standard error unless a dump command emits
structured output.

## Response Files

Response files are expanded with `@path`. `.campbuild` files are ordinary
response files by convention. Build and run commands also recognize bare build
file arguments when a positional value resolves to a `.campbuild` file.
