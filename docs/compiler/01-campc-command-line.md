# `campc` Command Line

`campc` is the command-line entry point for compiling Camp source, producing C
output, building native artifacts, inspecting compiler stages, restoring
packages, and editing simple package pragmas. It is a subcommand-oriented tool:

```sh
campc build [pattern.camp...] [options]
campc run [pattern.camp...] [options] -- [program-args...]
campc test [pattern.camp...] [options]
campc cover [pattern.camp...] [options]
campc dump <kind> [pattern.camp...] [options]
campc restore [pattern.camp...]
campc pkg <command> [...]
campc help [command]
```

The command-line compiler and the language server share the same project option
model, but they do not have the same side effects. `campc build` may build
project references and packages. The language-server project loader is read-only
except for reading already generated API headers and source files.

## Command Model

Every command is interpreted from the current working directory. Source patterns
may be file paths or globs. `@response-file` arguments are expanded before the
command is parsed, and build/run also treat a bare `.campbuild` positional
argument as a response file. See [Build Files And Pragmas](02-build-files-and-pragmas.md).

`campc` exits with `0` on success and `1` on command-line, package, parse,
semantic, metadata, emission, project-reference, or native-build failure. Normal
generated-file status lines go to standard output. Diagnostics and native build
errors go to standard error.

`campc` finds its bundled files from the Camp installation root. In a normal
installation, the executable lives in `<camp-root>/bin` and the compiler reads
`<camp-root>/lib`, `<camp-root>/targets`, and `<camp-root>/cache`. If the tool
cannot infer that layout, set `CAMP_HOME` to the Camp installation root.

## `build`

`build` compiles Camp source, emits C, and optionally invokes the selected
target's native build templates. It is the normal command for producing final
artifacts:

```sh
campc build src/*.camp --target gcc-linux-x64 --artifact exec --name textapp
campc build @textapp.campbuild
campc build src/math.camp --artifact static --name mathlib
campc build src/protocol.camp --artifact none --metadata export
```

When `--artifact` is omitted, `build` infers an executable if any root source
contains a public or exported `main` declaration; otherwise it builds a static
library. The inference is intentionally shallow and file-text based, so a
project that needs a specific result should state `--artifact`.

`--artifact none` emits C and related generated headers without invoking a
native compiler. Static and shared library builds also emit Camp API headers and
metadata by default. Executable builds require exactly one public or exported
`main` after analysis; `main` must return `int` or `void` and must have no
ordinary parameters or the supported command-line parameter shape.

## `run`

`run` builds an executable and then executes it from the working directory:

```sh
campc run samples/hello.camp
campc run @textapp.campbuild -- --verbose input.txt
```

Program arguments are separated from build arguments with `--`. `run` defaults
to `--artifact exec` when no artifact is specified, and it rejects non-executable
artifact kinds before doing build work.

## `test`

`test` builds a native test harness executable, runs the selected tests, and
writes test result artifacts:

```sh
campc test @app.campbuild
campc test @app.campbuild --filter MathTests::addReturnsSum
campc test @app.campbuild --test-result-format json
campc test @app.campbuild --list
```

The test module is compiled with declaration participation mode `test` and the
preprocessor symbol `TEST_MODULE`. Top-level `@test` functions and `@testonly`
helpers participate in this build. Production declarations are still checked so
they cannot depend on test-only declarations.

An in-module test run applies `test` directly to the project under test. Tests
may live beside production source, selected by the same source pattern and
`--include`/`--exclude` model as other builds. If the production project is an
executable, the generated harness entry point replaces the production process
entry in the test artifact.

An external test run applies `test` to a separate test project that references a
production project as a shared library. The production dependency is built or
imported in ordinary production mode, and the external tests can use only the
API exported by that shared library.

`--list` prints selected manifest IDs and stops after discovery. No harness is
built or run in list mode. Without `--list`, the command exits with `0` only
when every selected test passed or was skipped. Failed, invalid, error, compile,
native-build, and infrastructure results are command failures after any
available result artifacts are written.

## `cover`

`cover` runs the same test pipeline with Camp source coverage enabled:

```sh
campc cover @app.campbuild
campc cover @app.campbuild --coverage-format json,lcov
campc cover @tests.campbuild --coverage-subject mathlib
```

Coverage is measured against Camp source sequence points, not generated C. The
initial metrics are function-entry coverage and executable-line coverage.

For in-module coverage, the default coverage subject is `self`: the production
declarations in the current project are compiled with production semantics plus
coverage counters. `@test` declarations, `@testonly` declarations, generated
helpers, and the harness are excluded from the coverage denominator.

For external coverage, the test project runs in test mode and a selected shared
project-reference dependency is rebuilt as an instrumented production shared
library. If there is exactly one shared project reference and no explicit
coverage subject, that dependency is selected. If there are multiple shared
project references, use `--coverage-subject <name>`. Use
`--coverage-subject self` only when the test module's own production
declarations are the intended subject.

## `dump`

`dump` prints compiler intermediate output. The dump kind is required:

| Kind | Output |
|---|---|
| `tokens` | Lexical tokens for the first root source file. |
| `declarations` | Analyzed declaration surface for the first root source file. |
| `lowering` | Lowered Camp-like output after semantic rewrites. |
| `metadata` | Metadata JSON for the selected metadata view. |

```sh
campc dump tokens src/lexer_case.camp --nostdlib
campc dump declarations src/library.camp --nostdlib
campc dump lowering src/library.camp --nostdlib
campc dump metadata src/library.camp --metadata public
```

Build-only options such as `--artifact`, `--framework`, `--name`,
`--subsystem`, and `--out-dir` are not valid on `dump`. `dump metadata` defaults
to the `export` metadata view if `--metadata` is not supplied.

## `restore`

`restore` reads effective package source and package use pragmas, then installs
missing packages into the local package root:

```sh
campc restore @textapp.campbuild
campc restore src/*.camp
```

Restore uses the same `#build --use` and `#build --use-source` information as a
build, but it does not compile source. It installs into the working directory's
local package cache unless the package is already available from either the
local or global package root.

## `pkg`

`pkg` manages simple package source and dependency pragmas plus package
installation. The supported subcommands are:

| Command | Meaning |
|---|---|
| `pkg add-source <name> <folder> --local <file>` | Add or replace a local package source pragma. |
| `pkg add-source <name> <folder> --global` | Add or replace a global package source pragma. |
| `pkg remove-source <name> --local <file>` | Remove a local package source pragma. |
| `pkg remove-source <name> --global` | Remove a global package source pragma. |
| `pkg search <pkg> [--source <name>] [--local <file>]` | Search configured source roots for package versions. |
| `pkg install <pkg@version> [--global]` | Copy a package from a configured source into a package root. |
| `pkg uninstall <pkg[@version]> [--global]` | Remove an installed package or package version. |
| `pkg add <pkg@version> <file.camp>` | Add a `#build --use` pragma to a source file. |
| `pkg remove <pkg> <file.camp>` | Remove matching `#build --use` pragmas from a source file. |

Examples:

```sh
campc pkg add-source local-libs ../packages --local src/main.camp
campc pkg search text
campc pkg install textlib@1.2.0
campc pkg add textlib@1.2.0 src/main.camp
```

The package manager edits source files by inserting or removing prelude
`#build` lines. It does not rewrite `.campbuild` files through a structured
project model.

## `help`

`help` prints command help:

```sh
campc help
campc help build
campc --help
```

`init` is reserved for project scaffolding and is not part of the documented v1
workflow.

## Build Options

These options are valid for `build`, `run`, `test`, `cover`, and `dump` unless
noted:

| Option | Meaning |
|---|---|
| `--include`, `-i` | Include Camp API headers or additional source patterns for analysis. |
| `--exclude` | Exclude source files matched by source patterns. |
| `--target`, `-t` | Select a target from `targets/**/*.ini`; default is `clang-macos-x64`. |
| `--profile`, `-p` | Select `DEBUG` or `RELEASE`; default is `DEBUG`. |
| `--variant` | Select one or more target variants. |
| `--verbose`, `-v` | Print generated artifact status lines. |
| `--define`, `-d` | Add Camp preprocessor symbols. |
| `--emit` | Select the emitter; the documented emitter is `c99`. |
| `--nostdlib` | Omit automatic standard library package preparation. |
| `--reference`, `-r` | Add native libraries or linker references. Multiple values may follow one switch. |
| `--use`, `-u` | Use an installed or live package, as `pkg`, `pkg@version`, or `pkg@version:kind`. |
| `--use-source` | Add a named package source root for live package lookup. |
| `--project-reference` | Build and reference another Camp project. |
| `--metadata` | Select metadata emission: `none`, `export`, `public`, or `all`. |
| `--sourcefile-paths` | Select `caller(sourcefile)` path style: `relative` or `absolute`; default is `relative`. |
| `--sourcefile-root` | Add a root for relative `caller(sourcefile)` values. May be repeated. |
| `--explicit-within` | Require source `new` and pointer-form `delete` to use an explicit `within` context unless overridden by `#within`. |
| `--implicit-within` | Allow source `new` and pointer-form `delete` to fall back to the default allocator unless overridden by `#within`. |

`--sourcefile-paths` and `--sourcefile-root` affect only source-capture default
arguments such as `caller(sourcefile)`. Relative sourcefile paths are rooted at
the active `.campbuild` directory for build-file requests, or at the command
working directory for loose source-file requests. If one or more
`--sourcefile-root` values are supplied, those roots replace the default root
and the longest matching root is used.

Native build and output-layout options:

| Option | Meaning |
|---|---|
| `--artifact` | Select `exec`, `static`, `shared`, `only-static`, `only-shared`, or `none`. |
| `--name` | Set the project/artifact base name. |
| `--out-dir` | Set the final artifact output directory. |
| `--subsystem` | Select a native subsystem; the documented value is `windows` for executable builds. |
| `--framework`, `-f` | Link native frameworks on targets that support framework linking. Multiple values may follow one switch. |

`only-static` and `only-shared` build the current project as the named library
kind and also declare that dependency consumers may only request that link kind.
They are useful in project-reference and package graphs where an implementation
is deliberately not available as both static and shared.

`--name`, `--out-dir`, `--target`, `--profile`, `--variant`, `--reference`, and
`--framework` also affect `test` and `cover` harness builds. `test` and `cover`
always build a generated harness executable; do not use `--artifact` to select
the harness kind.

## Test Options

These options are accepted by `test` and `cover` only:

| Option | Meaning |
|---|---|
| `--list` | List selected test manifest IDs and stop after discovery. |
| `--filter` | Select tests by exact name or wildcard pattern. May be repeated. |

These options are accepted by `build`, `run`, `test`, and `cover`:

| Option | Meaning |
|---|---|
| `--test-output-dir` | Directory for the test manifest and `*.camp-test-results.json`; default is `<artifact-directory>`. Ignored by `build` and `run`. |
| `--test-result-format` | `text`, `json`, or `text,json`; default is `text,json`. Ignored by `build` and `run`. |

`--filter` matches a test's manifest ID, qualified name, or simple name.
Multiple filters are ORed together. Matching is ordinal, case-sensitive, and
culture-invariant. A pattern with no wildcard characters must match the whole
ID, qualified name, or simple name exactly.

Wildcard characters:

| Character | Meaning |
|---|---|
| `*` | Zero or more characters. |
| `?` | Exactly one character. |
| `^` | Exactly one ASCII uppercase character, `A` through `Z`. |

Examples:

```sh
campc test @app.campbuild --filter MathTests::addReturnsSum
campc test @app.campbuild --filter '*Writer*'
campc test @app.campbuild --filter 'parse^alue'
```

`MathTests::addReturnsSum` is exact. `*Writer*` performs a contains-style match
because the wildcard is explicit. `parse^alue` matches `parseValue` but not
`parse_value` or `parsevalue`.

## Coverage Options

These options are accepted by `cover` only:

| Option | Meaning |
|---|---|
| `--coverage-format` | `json`, `lcov`, or `json,lcov`; default is `json`. |
| `--coverage-output-dir` | Directory for coverage map and result artifacts; default is `<artifact-directory>`. |
| `--coverage-subject` | Coverage subject: `self` or a shared project-reference name. May be repeated. |

Coverage map CSV files are emitted in the coverage output directory as
`<project>.camp-coverage-map.csv`. Runtime counter files are scratch build
intermediates under `build/`. Coverage results are written as
`<project>.camp-coverage-results.json` when JSON is requested and `lcov.info`
when LCOV is requested.

## Output And Status Lines

`--verbose` writes generated-file status lines such as:

```text
generated: textapp.c
generated: textapp.h
generated: textapp_private.h
generated: textapp
```

Without `--verbose`, these generated-file status lines are omitted.

`campc test` also writes a test manifest under the artifact directory:

```text
<project>.camp-test-manifest.json
```

When JSON test results are enabled, it writes:

```text
<project>.camp-test-results.json
```

`campc cover` additionally writes coverage maps and coverage results under the
coverage output directory.

Text test output ends with a line labelled `test summary:`. `campc cover`
also prints a readable `coverage summary:` line after the test summary.

The `GeneratedFiles` list returned by the compiler driver contains absolute
paths, but the command-line status text prints only file names. Tools that need
paths should use the driver API or compute them from the output layout described
in [Artifacts, Cache, And Output Layout](05-artifacts-cache-and-output-layout.md).

## Diagnostics

Diagnostics use a compiler-style source location when a source range is known:

```text
src/main.camp(12,5): error: new requires an explicit within context.
```

Diagnostics without a precise range use the first loaded source file and a
fallback location. Native build diagnostics include the failed command and any
captured stdout or stderr from the native tool.

## Standard Input

The compiler driver accepts `-` as a source file for standard input when it is
the only root source and no API headers are supplied. This is mostly useful for
tooling experiments. Library and project builds should use real files so output
names, diagnostics, package cache checks, and API artifacts are stable.
