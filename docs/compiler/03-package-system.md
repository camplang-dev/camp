# Package Management (Experimental)

Camp's package infrastructure is a development-preview compiler feature. It
exists so compiler authors can exercise package API generation, native artifact
caching, package references, restore behavior, publishing, and language-service
integration while the package model is still settling.

Packages are source-only payloads. A package source publishes deterministic zip
archives plus a `versions.ini` catalog. Projects restore selected archives into
their local package cache and record exact selections in `packages.ini`.
Ordinary build, run, test, cover, dump, and language-server operations consume
only installed package cache entries. They do not contact package sources and do
not use `--use-source` as a live source lookup path.

## Dependency Specs

Package dependencies use a package name, an optional version request, and an
optional link kind:

```text
textlib
textlib@1
textlib@1.2
textlib@1.2.0
textlib/1.2.0
textlib@1.2.0:shared
textlib:static
textlib:api
```

`@` requests a compatible catalog version. `pkg@1` means any `1.x.y` version,
`pkg@1.2` means any `1.2.y` version, and `pkg@1.2.0` means that exact selected
version. `/` names an exact installed version.

The default dependency link kind is `:shared`.

## Package Sources

`--use-source <name> <path-or-url>` declares a named package source for restore,
install, and publish workflows:

```camp
#build --use-source local-libs ../packages
#build --use textlib@1.2
```

The source path can be a local filesystem path or an HTTP/HTTPS URL. Source
names are local configuration. They are not written to `packages.ini`.

A source root contains package directories:

```text
packages/
  textlib/
    versions.ini
    textlib_1.2.0.zip
```

`versions.ini` describes package identity, published versions, source archives,
hashes, compiler provenance, and direct dependencies:

```ini
[package]
name=textlib
identity=textlib

[1.2.0]
compiler=campc/0.9.1-preview.1
use=otherlib@1:static
sha256=<archive-sha256>
src=textlib_1.2.0.zip
```

Archive paths are relative to the package directory unless they are already
absolute paths or URLs.

## Restore And Lock Files

`campc restore` reads effective `--use-source` and `--use` declarations from the
provided source/build file plus global configuration. It resolves direct and
transitive dependencies, verifies archive SHA-256 values, extracts selected
source archives into the project cache, and writes `packages.ini`:

```sh
campc restore textapp.campbuild
campc restore src/*.camp
```

The lock file records portable package facts only:

```ini
[textlib]
identity=textlib
version=1.2.0
sha256=<archive-sha256>
```

Restore reuses locked versions unless `--upgrade` is supplied. `--upgrade`
updates all direct dependencies. `--upgrade textlib` updates one direct
dependency, and `--upgrade textlib@1.3` updates it within the supplied version
request.

## Installed Package Cache

Restored and manually installed packages use:

```text
<project>/cache/pkg/<package>/<version>/
<compiler-home>/cache/pkg/<package>/<version>/
```

Project cache entries win over compiler/global cache entries. A restored package
contains the extracted source archive:

```text
cache/pkg/
  textlib/
    1.2.0/
      src/
        text.camp
      textlib.campbuild
```

Package API/native artifacts are built lazily under the installed package
version:

```text
cache/pkg/textlib/1.2.0/bin/<artifact-directory>/
  textlib_api.camp
  textlib_api.h
  textlib_api.json
  libtextlib.a
```

## Dependency Link Kinds

Package dependencies support three link kinds:

| Kind | Meaning |
|---|---|
| `:api` | Build or reuse only the Camp API surface needed for analysis. |
| `:static` | Build or reuse a static native library plus API artifacts. |
| `:shared` | Build or reuse a shared native library plus API artifacts. |

When the root request does not build a native artifact, package preparation only
needs API headers. When the root request builds an executable or library,
`:static` and `:shared` request native libraries, while `:api` deliberately does
not.

## Package Commands

`campc pkg` commands are development-preview commands.

| Command | Meaning |
|---|---|
| `pkg add-global-source <name> <path-or-url>` | Add or replace a named global package source. |
| `pkg remove-global-source <name>` | Remove a named global package source. |
| `pkg list-global-sources` | List configured global package sources. |
| `pkg publish <version|+major|+minor|+patch> [build-file] [--pub-dir dir] [--name name]` | Create a deterministic source archive and update `versions.ini`. |
| `pkg install <package[@version|/version]> [--local file] [--global]` | Install a package archive into a package cache without editing `packages.ini`. |
| `pkg uninstall <package[/version]> [--global]` | Remove one cached version or all cached versions of a package. |

`pkg add`, `pkg remove`, `pkg add-source`, `pkg remove-source`, and `pkg search`
are intentionally removed. Edit build files manually for `--use` and local
`--use-source` declarations.

## Publishing

`pkg publish` selects a build file, collects package source files, creates a
deterministic source zip, computes its SHA-256, and writes or updates
`versions.ini`. The default output directory is:

```text
<project-root>/pub/<package-name>/
```

`--pub-dir` names the publication root directory. Package files are written
under `<pub-dir>/<package-name>/`.

Published archives exclude generated outputs, package caches, build caches,
lock files, and binary artifacts.

## Standard Library Package

The standard library is still compiler-owned. Unless `--nostdlib` is set, the
compiler prepares package `std` from `lib/std/src` and caches its artifacts
under the compiler library cache. User package restore does not route `std`
through `packages.ini`.

## Project References Versus Packages

Use a project reference when the dependency is another project in the same
source tree and should be built with the consumer's target, profile, variants,
and requested link kind. Use package references when the dependency should be
resolved through restore and consumed from the package cache.

Project references accept only `:static` and `:shared` suffixes. Package
references also accept `:api`.

## Cache Validity

Package cache reuse is conservative. The compiler compares output timestamps
against source files, native helper sources, headers, target definitions, and
compiler inputs. Command-line configuration that can change output disables
cache reuse for that request.

Delete a package version's `bin` directory to force artifact rebuild for one
target/profile/link kind. Delete the package version directory to force
reinstallation by `campc restore` or `campc pkg install`.
