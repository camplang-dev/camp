# Build Files And Pragmas

## `.campbuild` Files

A `.campbuild` file stores compiler arguments. It is supplied with `@file` or
as a bare build-file argument to `build` and `run`.

```text
src/*.camp
--target gcc-linux-x64
--artifact exec
--name sample
```

When a build file is used for `build` or `run`, the default output directory is
the `bin` directory next to the build file unless `--out-dir` overrides it.

## Bare Build File Expansion

For build and run commands, a positional argument that resolves to a
`.campbuild` file is expanded as if it were written with `@`.

```sh
campc build app.campbuild
```

## Response File Tokenization

Response files use command-line fragments. Quoting preserves spaces inside a
single argument. Project-reference paths are rebased when response files are
expanded from another directory.

## `#build` Pragmas

Source files can include build pragmas:

```camp
#build --target gcc-linux-x64
#build --use textlib@1.2.0
```

The pragma body uses the same build-option language as command-line arguments.
Pragmas are read from `lib/global.camp`, source files, and included API/header
files where the loader needs effective build options.

## Local And Global Pragmas

Global pragmas come from the compiler repository's `lib/global.camp`. Local
pragmas come from source and build files in the current project. Command-line
arguments have the highest precedence.

## Precedence Rules

The effective build option bag is built from global pragmas, local pragmas, and
command-line options. Single-value options such as target, profile, artifact,
and project name are resolved by precedence. Repeating conflicting values at
the same precedence is an error for options where the compiler requires one
effective value.

## Source And Include Patterns

Source patterns identify source files. Include patterns identify Camp API
headers or source patterns that should be available to analysis without being
part of the root build inputs.

`--exclude` removes source files matched by source patterns.

## Conditional Build Symbols

`--define` and `#define` add conditional compilation symbols. `#undef` removes
symbols in source preprocessing. Target files also supply target-owned defines
such as platform, compiler, architecture, or variant flags.
