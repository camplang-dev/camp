# Experimental Package Infrastructure

Camp's package infrastructure is a development-preview compiler feature. It
exists so compiler authors can exercise package API generation, native artifact
caching, package references, and language-service behavior while the real
package model is still being designed.

The current implementation is not recommended for ordinary Camp projects. It
does not include a public package repository, package publishing command, remote
restore protocol, package manifest format, lock file, checksum model, or
dependency solver. The CLI commands and source layout are expected to change.

Today, packages are source folders plus compiler-produced API and native
artifacts. The implementation is intentionally local and file-system based:
package sources live under configured source roots, copied packages live under
package roots, and build outputs live in cache directories keyed by target,
profile, and dependency link kind.

## Package Specs

The experimental compiler path models a package dependency as a name, an
optional version, and an optional link kind:

```text
textlib
textlib@1.2.0
textlib@1.2.0:shared
textlib:static
textlib:api
```

Package specs may appear in the current prototype surface:

- `--use` command-line arguments;
- `#build --use` pragmas;
- `campc pkg add`;
- experimental restore and package install workflows.

The link kind describes how the package is consumed when the root build needs
native artifacts. The default dependency link kind is `shared`.

## Version Resolution

Versions use a simple semantic-version ordering with `major.minor.patch` and an
optional suffix. If a dependency omits the version, package lookup selects the
highest installed version by that ordering. Exact versions select the matching
version directory.

Live source packages discovered through `--use-source` may be unversioned. When
an unversioned live package is used, its resolved cache version is `live`.

## Source Root Layout

The current source-root layout is a usage demonstration for compiler
development. A package source root contains package directories. A versioned
source package uses this shape:

```text
packages/
  textlib/
    1.2.0/
      src/
        text.camp
        text_native.c
        text_native.h
```

A live unversioned source package uses:

```text
packages/
  textlib/
    src/
      text.camp
```

Only the `src` subtree is compiled. Camp source, C source, and C headers under
that subtree are considered cache inputs. Packages that provide native helpers
place them beside the Camp sources they support.

## Package Sources

`--use-source <name> <path>` adds a named source root to the effective request:

```camp
#build --use-source local-libs ../packages
#build --use textlib@1.2.0
```

The source name is used by the experimental package search and source-management
commands. The compiler build path uses the paths. A package source with no path
may appear while editing source lists, but it cannot satisfy installation or
live-source resolution until a path is provided.

## Copied Package Roots

Copied packages are searched in two roots:

```text
<repo>/cache/pkg
<working-directory>/cache/pkg
```

The repository root is the global package root for the active compiler checkout.
The working-directory root is local to the project being built. `campc restore`
copies missing packages into the local root. `campc pkg install --global` copies
into the global root.

Copied packages use:

```text
cache/pkg/
  textlib/
    1.2.0/
      src/
        text.camp
```

Build artifacts for copied packages are written under the matching package's
cache tree, not into the package source directory.

## Live Source Packages

When `--use-source` points at a source root, the compiler can consume packages
directly from that root without first installing them. Live packages are still
built into the working directory's package cache:

```text
cache/pkg/
  textlib/
    live/
      bin/
        clang-macos-x64_shared_DEBUG/
          textlib_api.camp
          textlib_api.h
          textlib_api.json
          libtextlib.dylib
```

Live package sources are treated as cache inputs. If a live package source file
changes, the cached API or native artifact is rebuilt for the next consuming
build.

## Dependency Link Kinds

Package dependencies support three link kinds:

| Kind | Meaning |
|---|---|
| `:api` | Build or reuse only the Camp API surface needed for analysis. |
| `:static` | Build or reuse a static native library plus API artifacts. |
| `:shared` | Build or reuse a shared native library plus API artifacts. |

When the root request does not build a native artifact, package preparation only
needs API headers. When the root request builds an executable or library,
`:static` and `:shared` request native libraries, while `:api` deliberately
does not.

Shared dependencies are also added to `SharedLibraryApiHeaders`, so generated C
headers can use the import/export surface appropriate to shared-library
consumption.

## Standard Library Package

The standard library is prepared as package `std` unless `--nostdlib` is set.
Its source package lives under `lib/std/src`. For native builds the compiler
builds `std` as a static package dependency and links it into the root artifact.
For analysis-only and C-only builds, the compiler prepares the API header as
needed.

Because `std` is bundled with the compiler checkout, its package source root is
the repository `lib` directory rather than an installed package source.

## Experimental Restore

`campc restore` reads effective global and local package pragmas from the
provided sources or build file. For each `--use` package that is not installed
locally or globally, it copies the package from the configured source roots into
the local package root:

```sh
campc restore @textapp.campbuild
```

Restore does not build API headers or native libraries. Those are built lazily
by a later compile when the selected target/profile/link kind is known. This
command is intended for compiler development and package-infrastructure testing,
not normal project setup.

## Experimental Package Commands

The `campc pkg` commands are development-preview commands. They operate only on
local files and copied package folders. They do not contact a package
repository.

`campc pkg search textlib` prints versions found in configured source roots.
With `--local <file>`, search reads source roots from that file in addition to
global pragmas. With `--source <name>`, it restricts search to one named source.

`campc pkg install textlib@1.2.0` copies the selected package source into the
local package root. `--global` selects the global root.

`campc pkg uninstall textlib@1.2.0` removes one installed version. Omitting the
version removes all installed versions for that package from the selected root.

`campc pkg add` and `campc pkg remove` edit `#build --use` pragmas at the top of
the named source file. `campc pkg add-source` and `campc pkg remove-source` edit
`#build --use-source` pragmas in either `lib/global.camp` or a selected local
source file.

## Project References Versus Experimental Packages

For normal multi-project development today, use a project reference when the
dependency is another project in the same source tree and should be built with
the consumer's target, profile, variants, and requested link kind. The package
path exists to test compiler behavior around package identity, API headers, and
cached native artifacts.

Project references accept only `:static` and `:shared` suffixes. Package
references also accept `:api`.

Both mechanisms ultimately contribute Camp API headers to analysis and native
libraries to linking. Their cache roots and freshness checks differ:

- packages are keyed by package name, version or `live`, target, link kind, and
  profile;
- project references are built into the referenced project's `bin` directory and
  checked against the referenced build file, sources, includes, target metadata,
  compiler binaries, global defaults, and native references.

## Transitive Native References

When a static project reference is consumed, its native references are added to
the consumer's link line because static linking must carry the referenced
library's dependencies forward. When a shared reference is consumed, only shared
runtime/link files that are themselves shared dependencies need to propagate.

Package builds follow the same general rule through the link files returned by
the package build. Shared runtime files are copied next to the final artifact
when the target exposes a shared-library extension or import-library mapping.

## Cache Validity

Package cache reuse is conservative. The compiler compares output timestamps
against source files, native helper sources, headers, target definitions, and
compiler inputs. Command-line defines disable package cache reuse for that
request, because defines can change the API or native output.

Delete the package's cached `bin` directory to force a rebuild for one target
and link kind. Delete the package version directory to force source
reinstallation or live cache refresh.
