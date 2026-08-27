# Build Files And Pragmas

Camp build configuration is deliberately small: `.campbuild` files are response
files, and source files may contribute prelude directives. The compiler combines
global pragmas, local pragmas, and command-line options into one effective build
request.

## `.campbuild` Files

A `.campbuild` file contains command-line fragments:

```text
# app.campbuild
--target gcc-linux-x64
--profile DEBUG
--artifact exec
--name textapp
src/*.camp
--api api/*.camp
```

Use a build file with `@file`:

```sh
campc build @app.campbuild
```

For `build`, `run`, `test`, and `cover`, a bare positional `.campbuild` file is
expanded the same way:

```sh
campc build app.campbuild
campc run app.campbuild -- --trace
campc test app.campbuild
campc cover app.campbuild
```

`campc init` creates ordinary `.campbuild` response files. There is no separate
project-file format hidden behind the starter templates; the generated build
file can be edited the same way as any hand-written response file.

When a build, run, test, or cover command uses a build file and `--out-dir` is
not supplied, the default final artifact root is `bin` next to that build file.

The same build-file directory is the default root for relative
`caller(sourcefile)` values unless the build supplies one or more explicit
`--sourcefile-root` values.

## Response File Tokenization

Response files use the same option language as command-line arguments:

- whitespace separates arguments;
- double quotes preserve whitespace inside one argument;
- backslash escapes `"` and `\` inside quoted text;
- a `#` at the start of a response-file token begins a comment through the end
  of the line;
- response files may include other response files with `@other-file`;
- response files may optionally include another response file with
  `@?other-file`. If the optional file is missing, it is ignored. If it exists,
  it is parsed normally, and any errors inside it are still reported.

Relative source patterns and path-valued options inside a response file are
rebased to the directory containing that response file. This includes source
patterns, `--api`, `--exclude`, `--out-dir`, `--pub-dir`, `--sourcefile-root`,
`--use-source` paths, and project-reference paths. `--use-source` is used by
restore/install/publish workflows; ordinary builds consume the installed package
cache instead of the source path. Native references are rebased only when they
look like paths; bare linker names remain bare names.

Project-reference link-kind suffixes are preserved while the path part is
rebased:

```text
--project-reference ../mathlib:static
```

Optional response-file includes are useful for local machine configuration that
should not usually be committed:

```text
# app.campbuild
@../shared.campbuild
@?../local.campbuild

--name app
--use ext-json
src/*.camp
```

The required shared file can be checked in, while `local.campbuild` can be
ignored by source control and used for local package sources or similar
developer-specific paths.

## Source Patterns

Source patterns expand to `.camp` files. Plain file paths must exist. Glob
patterns are expanded recursively from the nearest stable search root. Matches
are sorted, de-duplicated case-insensitively, and stored relative to the command
working directory in the final compiler request.

`--exclude` filters root source patterns. It does not remove API headers already
added by `--api`, package preparation, or project-reference API discovery.

```text
src/**/*.camp
--exclude src/generated/*.camp
```

## API Files

`--api` adds files to analysis without making them root implementation sources
for emission. This is how Camp API headers are made visible to a build.

API files may contain their own prelude `#build` directives. The loader
iteratively reads pragmas from source files and API files until no newly loaded
API file adds more build pragmas. This matters for API headers that need to
contribute native references or package uses:

```camp
#build --reference platform-io

export extern void platformOpen();
```

An API file is analyzed, but it is not emitted as a root `.c` source for the
current project. API files may declare API surfaces, but they may not contain
implementation-bearing declarations such as function bodies, concrete class
implementation shapes, global storage variables, or static fields that require
storage.

## `#build` Pragmas

A source file may contain `#build` directives in its file prelude:

```camp
#build --nostdlib
#build --artifact none
#build --api api/*.camp
#build --use textlib@1.2.0

export int meaning()
{
	return 42;
}
```

The prelude continues through blank lines and ordinary comments. It ends at the
first non-comment source token. A `#build` directive after that point is a
diagnostic. `#within` directives are also prelude directives, but they are
handled by the compiler request rather than by build-option collection.

The pragma body uses the same option parser as a response file, except
positional source arguments are not valid in `#build`.

## Global, Local, And Command-Line Precedence

Effective build options are applied in this order:

1. Global pragmas from `lib/global.camp`.
2. Local pragmas from root source and included files.
3. Command-line arguments or `.campbuild` response-file arguments.

Repeated list-valued options accumulate. Examples include `--api`,
`--exclude`, `--declare`, `--configure`, `--require`, `--reference`,
`--framework`, `--use`, `--use-source`, and `--project-reference`.

Single-valued options resolve by precedence. Examples include `--target`,
`--profile`, `--emit`, `--metadata`, `--artifact`, `--name`, `--subsystem`,
`--out-dir`, and the within allocation policy selected by `--explicit-within`
or `--implicit-within`. Two conflicting values at the same precedence are a
diagnostic.

Variant selections are also precedence-sensitive. A higher-precedence variant
list replaces a lower-precedence list; values at the same precedence accumulate.

## Configuration Flags

`--declare` and `#build --declare` introduce module-owned configuration flags.
`--declare NAME` gives the flag a false ambient value; `--declare NAME=true`
gives it a true ambient value.

`--configure` and `#build --configure` select a value for a declared flag.
`--configure NAME` is equivalent to `--configure NAME=true`; use
`--configure NAME=false` to select false.

`--require` and `#build --require` publish a module-level requirement that must
be satisfied by consumers. Source declarations use `requires (...)`, and method
bodies query flags with `configured(...)`.

Target-owned flags are declared or configured by target files and variants. A
user cannot configure them from the command line; selecting the target or target
variant is the supported way to obtain those facts.

Legacy source conditionals (`#if`, `#elif`, `#else`, `#endif`) and source-local
symbol mutation (`#define`, `#undef`) are hard errors in Camp source. Use
`requires (...)` on declarations and ordinary `if (configured(...))` in
function bodies.

## `#within` Directives

`#within explicit` and `#within implicit` are source-file prelude directives:

```camp
#within explicit

export void allocate(within allocator)
{
	auto bytes = new byte[16];
	delete bytes;
}
```

They override the default allocation policy for that source file. Command-line
`--explicit-within` and `--implicit-within` select the request default, while
`#within` selects the per-file policy. Static and shared library builds default
to explicit allocation; executable and C-only builds default to implicit
allocation unless the request or source file says otherwise.

## Project References In Build Files

`--project-reference` accepts a file, a path without the `.campbuild` suffix, or
a directory. A directory reference resolves to a build file named after the
directory when present; otherwise it resolves only if exactly one `.campbuild`
file exists in that directory.

```text
--project-reference ../mathlib:static
--project-reference ../plugins/rendering:shared
```

The CLI build path builds project references as needed. It rewrites the child
build request so the referenced project uses the consumer target, profile,
variants, library kind, and cache output directory. The language-service loader
uses the same resolution rules, but it does not build references; it uses an
existing API header when one is current enough for analysis, or falls back to
source analysis.

## Recommended Build File Shape

Keep project-owned build files explicit and boring:

```text
--target clang-macos-x64
--profile DEBUG
--artifact exec
--name textapp
--metadata export
--use-source local ../packages
--use textlib@1.2.0:shared
src/*.camp
```

Prefer source `#build` pragmas for facts that truly belong to a source surface,
such as required package uses or API files. Prefer `.campbuild` files for
project selection, artifact kind, output name, target, local package source
configuration, and local developer build choices.
