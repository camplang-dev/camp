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
--include api/*.camp
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
- response files may include other response files with `@other-file`.

Relative source patterns and path-valued options inside a response file are
rebased to the directory containing that response file. This includes source
patterns, `--include`, `--exclude`, `--out-dir`, `--sourcefile-root`,
`--use-source` paths, and project-reference paths. Native references are
rebased only when they look like paths; bare linker names remain bare names.

Project-reference link-kind suffixes are preserved while the path part is
rebased:

```text
--project-reference ../mathlib:static
```

## Source Patterns

Source patterns expand to `.camp` files. Plain file paths must exist. Glob
patterns are expanded recursively from the nearest stable search root. Matches
are sorted, de-duplicated case-insensitively, and stored relative to the command
working directory in the final compiler request.

`--exclude` filters root source patterns. It does not remove API headers already
added by `--include`, package preparation, or project-reference API discovery.

```text
src/**/*.camp
--exclude src/generated/*.camp
```

## Include Patterns

`--include` adds files to analysis without making them root source files for
emission. This is how Camp API headers and analysis-only helper declarations are
made visible.

Included files may contain their own prelude `#build` directives. The loader
iteratively reads pragmas from source files and include files until no newly
included file adds more build pragmas. This matters for API headers that need to
contribute native references or package uses:

```camp
#build --reference platform-io

export extern void platformOpen();
```

The included file is analyzed, but it is not emitted as a root `.c` source for
the current project.

## `#build` Pragmas

A source file may contain `#build` directives in its file prelude:

```camp
#build --nostdlib
#build --artifact none
#build --include api/*.camp
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

Repeated list-valued options accumulate. Examples include `--include`,
`--exclude`, `--define`, `--reference`, `--framework`, `--use`,
`--use-source`, and `--project-reference`.

Single-valued options resolve by precedence. Examples include `--target`,
`--profile`, `--emit`, `--metadata`, `--artifact`, `--name`, `--subsystem`,
`--out-dir`, and the within allocation policy selected by `--explicit-within`
or `--implicit-within`. Two conflicting values at the same precedence are a
diagnostic.

Variant selections are also precedence-sensitive. A higher-precedence variant
list replaces a lower-precedence list; values at the same precedence accumulate.

## Conditional Symbols

`--define` and `#build --define` add Camp preprocessor symbols. The compiler
also defines `TRUE`, the selected profile (`DEBUG` or `RELEASE`), and target
symbols from the selected target and variants.

Target-owned symbols cannot be supplied manually through `--define`. Selecting
the matching target or target variant is the supported way to obtain them. This
prevents source from pretending to be built for a target that was not selected.

Camp inline constants are separate from preprocessor symbols. Inline constants
do not participate in Camp `#if` evaluation, and generated C macros do not feed
back into Camp preprocessing.

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
such as required package uses or API include files. Prefer `.campbuild` files
for project selection, artifact kind, output name, target, and local developer
build choices.
