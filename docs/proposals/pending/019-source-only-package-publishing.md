# Source-Only Package Publishing

Status: pending  
Proposal date: 2026-08-26  
Last updated date: 2026-08-26

## Summary

Replace Camp's current experimental local package infrastructure with a
source-only package publishing and restore model.

The proposed model keeps the package system deliberately small:

- packages are published as source archives;
- each package source exposes a `versions.ini` catalog beside the archives;
- projects use `--use` to declare package dependencies;
- projects use `--use-source` to declare named package sources;
- `campc restore` resolves package dependencies, installs missing source
  archives, and writes `packages.ini`;
- ordinary build, run, test, dump, language-server, and package-reference
  analysis consume already-installed packages only;
- `campc pkg publish` creates source archives and updates `versions.ini`;
- `campc pkg install` manually installs packages into a cache without changing
  the project lock file;
- global package sources are managed with explicit `campc pkg` commands.

This proposal does not introduce binary Camp package artifacts, package
registry servers, package search, package signing, or authenticated feeds.

## Motivation

The current package path exists to exercise compiler behavior around package API
generation, native artifact caching, language-service loading, and package
references. It is useful for compiler development, but it is not yet a public
package model.

Current behavior is too source-live for ordinary package consumption:

- `--use-source` can make build resolve live package source directly;
- live package source can take precedence over installed cache entries;
- build can refresh package API/native outputs from source roots;
- `campc restore` copies local folder packages but does not write a lock file;
- `campc pkg search` scans local folders only;
- `campc pkg add`, `remove`, `add-source`, and `remove-source` edit source or
  build pragmas as convenience commands;
- there is no remote source protocol, source archive catalog, checksum
  verification, package publishing command, or committed package lock file.

That shape blurs two different operations:

```text
restore changes package state
build consumes package state
```

The new model makes that separation explicit. This gives users repeatable
package restores, keeps ordinary build deterministic, and avoids designing a
global registry before Camp needs one.

## Goals

- Make source archives the only package payload for the first public package
  publishing model.
- Keep `campc restore` as the package restore command.
- Keep `--use` as the way projects declare package dependencies.
- Keep `--use-source <name> <path-or-url>` as the way projects declare named
  package sources.
- Keep package source names local to build/global configuration, not committed
  lock facts.
- Add a committed `packages.ini` lock file containing exact selected package
  versions.
- Add a published `versions.ini` package catalog containing available package
  versions and source archive hashes.
- Make ordinary build, run, test, dump, LSP, and project/package reference
  analysis use installed package caches only.
- Make package cache lookup prefer the project cache before the compiler/global
  cache.
- Preserve the existing package reference type suffixes `:api`, `:static`, and
  `:shared`.
- Preserve the current default package reference type of `:shared`.
- Add `campc pkg publish`.
- Make `campc pkg publish` default to `<project-root>/pub/<package-name>` when
  `--out` is omitted.
- Keep global source management ergonomic through `campc pkg` commands.
- Remove package commands that only edit local project dependencies or search
  non-indexed sources.

## Non-Goals

- A central package registry server.
- Package search.
- Binary native artifacts as package payloads.
- Package signing, trust policy, advisory metadata, or yanking.
- Git clone or sparse checkout restore.
- Private or authenticated package feeds.
- Complex semantic-version range syntax.
- Keeping live package source resolution in ordinary build.
- Generating a sidecar package manifest inside extracted package cache
  directories.

## Command-Line Surface

The v1 package command surface is:

| Command | Meaning |
| --- | --- |
| `campc restore <build-file>` | Resolve package dependencies, install missing packages, and write `packages.ini`. |
| `campc restore <build-file> --upgrade` | Upgrade all direct dependencies. |
| `campc restore <build-file> --upgrade <package[@version]>` | Upgrade one direct dependency, optionally constrained by a version expression. |
| `campc pkg install <package[@version\|/version]> [--global]` | Install a package into cache without changing `packages.ini`. |
| `campc pkg uninstall <package[/version]> [--global]` | Remove one package version or all cached versions of a package. |
| `campc pkg publish <version\|+major\|+minor\|+patch> [<build-file>] [--name <package>] [--out <folder>]` | Publish a source archive and update `versions.ini`. |
| `campc pkg add-global-source <name> <path-or-url>` | Add or replace a named global package source. |
| `campc pkg remove-global-source <name>` | Remove a named global package source. |
| `campc pkg list-global-sources` | Print configured global package sources. |

Remove these current package commands:

| Current Command | Replacement |
| --- | --- |
| `campc pkg add` | Edit the build file and add `--use`. |
| `campc pkg remove` | Edit the build file and remove `--use`. |
| `campc pkg add-source --local` | Edit the build file and add `--use-source`. |
| `campc pkg remove-source --local` | Edit the build file and remove `--use-source`. |
| `campc pkg search` | Omit until there is a real package index or registry. |

`campc restore` does not get a `--source` filter. Restore should use the package
sources already declared by the project and by global configuration.

## Project Declarations

Projects continue to declare package dependencies with `--use`:

```text
src/*.camp
--use ext-ansiterm@1.2
--use nnverium-easydb@1.1:static
--use-source local ../package-feed
```

The `--use-source` option remains named:

```text
--use-source local ../package-feed
```

The name is useful for diagnostics, global source management, and future
source-specific behavior. Source names remain local configuration. They are not
written to `packages.ini`.

Build-file editing stays manual. Adding a dependency is just adding a line:

```text
--use ext-ansiterm@1.2
```

Adding a project-local package source is just adding a line:

```text
--use-source local ../package-feed
```

Global source commands exist because users should not need to know where Camp's
global configuration file lives:

```sh
campc pkg add-global-source camp https://packages.camplang.org
campc pkg add-global-source mine /Users/andrew/packages
campc pkg list-global-sources
campc pkg remove-global-source mine
```

The global source commands should read and write the same global configuration
mechanism that currently contributes global build pragmas. The physical storage
file is an implementation detail, but the configured source entries should have
the same effective shape as `--use-source <name> <path-or-url>`.

## Package Sources

A package source is a local filesystem directory or HTTP/HTTPS URL containing
one directory per package.

```text
<source-root>/
  ext-ansiterm/
    versions.ini
    ext-ansiterm_1.2.1.zip
  nnverium-easydb/
    versions.ini
    nnverium-easydb_1.1.0.zip
```

Given:

```text
--use-source camp https://packages.camplang.org
```

restore reads:

```text
https://packages.camplang.org/ext-ansiterm/versions.ini
```

The same path rule applies to local filesystem roots:

```text
--use-source local ../package-feed
```

```text
../package-feed/ext-ansiterm/versions.ini
```

Package sources do not need a global source index for v1. Without such an
index, `campc pkg search` cannot work consistently across local and HTTP
sources, so it is removed.

## Version Catalog: `versions.ini`

Each published package directory contains a `versions.ini` file.

```ini
[package]
name=nnverium-easydb
identity=b3570b68026a47d79548bf949ba58f7e

[1.1.0]
compiler=campc/0.9.0-preview.1
use=ext-ansiterm@1.2 jjtst-winlib@2.0.1
sha256=b205dcf35d654a888db77db0971c41af6cd0246f35dc41f5b7121b21dd57f231
src=nnverium-easydb_1.1.0.zip
```

Required keys:

| Location | Key | Meaning |
| --- | --- | --- |
| `[package]` | `name` | Package name. |
| `[package]` | `identity` | Stable package identity. |
| version section | `sha256` | SHA-256 of the source archive. |
| version section | `src` | Archive file name or relative path. |

Optional keys:

| Location | Key | Meaning |
| --- | --- | --- |
| version section | `compiler` | Informational tool provenance for the published version. |
| version section | `use` | Package dependencies, using the same package syntax as `--use`. |

There is no schema key in v1. If the format later needs an explicit version, a
metadata key or section can be added then.

The archive hash is a restore-repeatability check. It catches wrong files,
stale mirrors, interrupted downloads, and accidental archive replacement. It is
not a complete package trust or security model.

## Lock File: `packages.ini`

`campc restore` writes `packages.ini` at the project root. Projects that want
repeatable package builds should commit it.

```ini
[nnverium-easydb]
identity=b3570b68026a47d79548bf949ba58f7e
version=1.2.1
sha256=f8e1f38e50284090aa70901b940ba7064958054ccc584901935eb9e049dd949e

[ext-ansiterm]
identity=d16a4b9d10c9469d970ae405bb2648fe
version=1.2.1
sha256=b205dcf35d654a888db77db0971c41af6cd0246f35dc41f5b7121b21dd57f231
```

`packages.ini` contains only portable package facts:

| Field | Meaning |
| --- | --- |
| section name | Package name. |
| `identity` | Stable package identity. |
| `version` | Exact selected version. |
| `sha256` | Expected source archive hash. |

The lock file does not record source names. Source names are local machine or
project configuration, while `packages.ini` is committed to the repo.

The lock file also does not record direct/transitive status for v1. Restore can
derive direct dependencies from the build file.

## Version Selection Syntax

Use `@` for requested version expressions and `/` for exact selected versions.
Package versions use three numeric components: major, minor, and patch.

| Syntax | Meaning |
| --- | --- |
| `pkg` | Latest compatible installed/resolved version. |
| `pkg@1` | Latest `1.x.x`. |
| `pkg@1.2` | Latest `1.2.x`. |
| `pkg@1.2.3` | Exact request. |
| `pkg/1.2.3` | Exact selected version, mainly lock/cache/internal syntax. |

`campc restore --upgrade <package[@version]>` uses the same expression syntax.

```sh
campc restore my-app.campbuild --upgrade ext-ansiterm
campc restore my-app.campbuild --upgrade ext-ansiterm@2
```

The first command upgrades `ext-ansiterm` to the latest compatible version. The
second upgrades it to the latest compatible version in major version `2`.

## Resolution Rules

The v1 resolver should be simple and predictable.

- Direct dependencies come from the build file's `--use` entries.
- Transitive dependencies come from selected package versions'
  `versions.ini` `use=` entries.
- If `packages.ini` exists, locked versions are reused unless the package is
  selected for upgrade or becomes incompatible with an upgraded dependency.
- `campc restore --upgrade` reselects direct dependencies and may reselect
  transitive dependencies as needed.
- `campc restore --upgrade pkg` reselects that direct dependency to the latest
  compatible version.
- `campc restore --upgrade pkg@version` reselects that direct dependency using
  the supplied version expression.
- If a package is not locked, select the highest version that satisfies the
  relevant version expression.
- If two constraints cannot be satisfied by one package version, report a
  conflict diagnostic.
- The v1 resolver does not need complex backtracking. A clear conflict is
  better than a surprising resolver search.

## Reference Types

Package references keep the existing reference type suffixes:

```text
--use ext-ansiterm@1.2:api
--use ext-ansiterm@1.2:static
--use ext-ansiterm@1.2:shared
```

The default remains `:shared`.

Because v1 packages contain source only, the reference type controls how the
compiler prepares the installed source package for the current build. It does
not select a prebuilt package artifact.

## Restore Behavior

`campc restore <build-file>` should:

1. load global package source declarations;
2. load project-local `--use-source` declarations from the build file;
3. load direct `--use` package dependencies from the build file;
4. read existing `packages.ini`, if present;
5. read `versions.ini` from configured package sources as needed;
6. resolve exact package versions for direct and transitive dependencies;
7. prefer already installed packages when they satisfy the dependency graph;
8. download or copy missing selected source archives;
9. verify each selected archive's SHA-256 before extraction;
10. extract selected packages into the project package cache;
11. write `packages.ini` with exact selected versions.

If no lock file exists and installed packages already satisfy the dependency
graph, restore should use them rather than downloading newer versions. Users ask
for movement with `--upgrade`.

If a locked package is missing locally, restore should look through configured
sources for the locked package identity, version, and archive hash, then install
that exact archive.

If a locked package cannot be found:

```text
Package 'ext-ansiterm/1.2.1' is locked but not installed and could not be
found in configured package sources. Add a package source or update the lock
with 'campc restore --upgrade ext-ansiterm'.
```

If two package sources offer the same package name with different identities,
restore should reject the ambiguity unless the lock file already identifies the
intended package identity and one source matches it.

## Build Behavior

Ordinary build, run, test, cover, dump, and LSP operations should not restore
packages and should not contact package sources.

Build should:

1. aggregate `--use` dependencies;
2. read `packages.ini`, if present;
3. select exact locked package versions when available;
4. allow command-line `--use` version overrides only when the selected package
   is already installed and compatible;
5. search installed package roots in this order:
   - `<project-root>/cache/pkg`;
   - `<compiler-root>/cache/pkg`;
6. prepare API/static/shared outputs lazily from installed source packages as
   needed;
7. report missing packages with a restore-oriented diagnostic.

If a package is not installed:

```text
Package 'ext-ansiterm/1.2.1' is locked but not installed.
Run 'campc restore'.
```

Under this proposal, build diagnostics should not mention searched live package
source roots. `--use-source` declares package sources for restore/install, not
live source roots for ordinary build.

The bundled standard library remains compiler-owned. It can continue to use the
compiler's bundled package source root and cache behavior as an internal
implementation detail.

## Installed Package Cache

Restored packages are extracted under:

```text
<project-root>/cache/pkg/<package-name>/<version>/
```

Manual global installs use:

```text
<compiler-root>/cache/pkg/<package-name>/<version>/
```

The extracted package should contain the source tree and build file needed to
prepare package API/static/shared outputs:

```text
cache/pkg/
  nnverium-easydb/
    1.2.1/
      nnverium-easydb.campbuild
      src/
        ...
      README.md
      LICENSE
```

Do not generate a `package.ini` sidecar in the extracted package directory. The
catalog and lock already contain package identity, version, hash, and source
archive information.

Treat `cache/pkg` as compiler-managed convenience storage, not protected state.
If a user edits files in the package cache, those files are simply local build
input. If the cache gets into a bad state, the user can delete the affected
package directory and run `campc restore`.

## Publishing Behavior

`campc pkg publish` creates or updates a package source directory. When `--out`
is omitted, it writes to:

```text
<project-root>/pub/<package-name>
```

Publish operates on one build file. If a build file argument is omitted, the
compiler should use the single `.campbuild` file in the current directory. If
there are no `.campbuild` files or multiple `.campbuild` files, publish should
report a diagnostic requiring an explicit build file.

The project root for publish is the selected build file's directory.

Example:

```sh
campc pkg publish +minor
```

Output:

```text
<project-root>/
  pub/
    nnverium-easydb/
      versions.ini
      nnverium-easydb_1.2.0.zip
```

Explicit output is still available:

```sh
campc pkg publish 1.2.1 --out ../package-feed/nnverium-easydb
```

`--out` names the package directory, not the parent feed directory.

The publish version may be exact:

```sh
campc pkg publish 1.2.1
```

or an increment keyword:

```sh
campc pkg publish +major
campc pkg publish +minor
campc pkg publish +patch
```

The increment keyword is applied to the latest version known from the
destination `versions.ini` or project metadata:

| Increment | Result |
| --- | --- |
| `+major` | Increment major; reset minor and patch to `0`. |
| `+minor` | Increment minor; reset patch to `0`. |
| `+patch` | Increment patch. |

Publishing should:

1. select the package build file;
2. determine the package name from project metadata or `--name`;
3. determine the version from an exact version or increment;
4. require an explicit version and `--name` if those values cannot be inferred;
5. collect the package source files used by the build;
6. include the package build file;
7. include source-level native support files that are part of the package, such
   as `.c` and `.h` files, but not compiled binary artifacts;
8. exclude package caches, build outputs, generated API/native package outputs,
   and lock files;
9. create a deterministic zip archive;
10. compute the archive SHA-256;
11. create or update `versions.ini`;
12. fail if the target version already exists.

Deterministic zip creation should use sorted relative paths, normalized `/`
path separators, stable timestamps, and no absolute source paths.

The `compiler=` catalog line records informational tool provenance. It is not a
compatibility requirement:

```ini
compiler=campc/0.9.0-preview.1
```

## Package Installation

`campc pkg install` manually installs one package into the project cache by
default:

```sh
campc pkg install ext-ansiterm@1.2
campc pkg install ext-ansiterm/1.2.1
```

`--global` installs into the compiler/global package cache:

```sh
campc pkg install ext-ansiterm/1.2.1 --global
```

Install should resolve through configured package sources and `versions.ini`.
It should verify the archive hash before extraction. It should not write
`packages.ini`.

`campc pkg uninstall` removes cached packages:

```sh
campc pkg uninstall ext-ansiterm/1.2.1
campc pkg uninstall ext-ansiterm --global
```

Uninstall should not edit `packages.ini`.

## Current Compiler Impact

The main affected code is in `campc/Program.cs`,
`Camp.Compiler/CompilerDriver.cs`, project loading, command-line option parsing,
and package-related tests.

Current package command handling should be changed:

- remove `pkg add`;
- remove `pkg remove`;
- remove `pkg search`;
- replace `pkg add-source` and `pkg remove-source` with
  `pkg add-global-source`, `pkg remove-global-source`, and
  `pkg list-global-sources`;
- add `pkg publish`;
- update `pkg install` to resolve archive catalogs instead of copying source
  directories directly;
- update `pkg uninstall` version parsing to support exact `/version` spelling.

Current restore handling should be changed:

- keep top-level `campc restore`;
- add `--upgrade` with optional `package[@version]`;
- read and write `packages.ini`;
- resolve transitive dependencies from `versions.ini` `use=` entries;
- install packages into the project cache;
- support local filesystem package sources;
- support HTTP/HTTPS package sources;
- verify source archive hashes before extraction.

Current build handling should be changed:

- stop resolving package dependencies from live `--use-source` source roots;
- stop treating `--use-source` path validation as a build-time failure when no
  restore/install operation is being performed;
- read `packages.ini` during package selection;
- prefer project package cache before compiler/global package cache;
- preserve `:shared` as the default dependency link kind;
- keep lazy API/static/shared output preparation from installed source packages.

Current diagnostics should be changed:

- missing package diagnostics should recommend `campc restore`;
- build diagnostics should not list searched package source roots;
- restore/install diagnostics should list the named package sources consulted;
- invalid `versions.ini` and `packages.ini` files should report filename,
  section, key, and package/version when practical;
- identity mismatches should name both identities and the source/lock involved.

## Documentation Updates

If accepted and implemented, update the active documentation. Do not rewrite old
accepted or rejected proposals as living references.

Required documentation updates:

- `docs/compiler/01-campc-command-line.md`: command surface, `restore`,
  `pkg install`, `pkg uninstall`, `pkg publish`, and global source commands.
- `docs/compiler/02-build-files-and-pragmas.md`: `--use` and `--use-source`
  semantics, especially that `--use-source` is not live build input.
- `docs/compiler/03-package-system.md`: replace the current development-preview
  package model with the source-only package model.
- `docs/compiler/05-artifacts-cache-and-output-layout.md`: project/global
  package cache lookup order and package artifact layout.
- `docs/compiler/08-language-server-and-editor-tooling.md`: LSP consumes
  installed package cache and reports restore diagnostics; it does not restore.
- `docs/compiler/09-standard-library-build-integration.md`: clarify which
  package behavior is standard-library internal and which applies to user
  packages.
- `docs/camp-llm-coding-guide.md`: explain when to add `--use`, when to run
  `campc restore`, and that package sources are local configuration.

## Test Coverage

Update current tests that assume live package source lookup. Important existing
tests include:

- `Use_source_resolves_live_unversioned_package_sources`;
- `Build_reports_missing_use_source_path_with_resolved_path`;
- `Build_reports_missing_live_package_with_searched_roots`;
- `Live_package_dependency_builds_shared_by_default_and_static_separately_on_macos`;
- `Live_static_package_dependency_lowers_iterators_before_api_emission`;
- `Live_api_package_dependency_emits_headers_without_native_library`;
- `Lsp_uses_package_sources_before_stale_package_api_headers`;
- `Package_source_can_be_added_and_searched_locally`;
- `Restore_installs_packages_into_cache_pkg`.

Add or update command-line tests for:

- removed `pkg add`, `pkg remove`, and `pkg search` commands;
- new `pkg add-global-source`, `remove-global-source`, and
  `list-global-sources`;
- local build-file `--use-source` parsing without build-time live source use;
- `campc restore` reading local and global named sources;
- `campc restore --upgrade`;
- `campc restore --upgrade package`;
- `campc restore --upgrade package@version`;
- `packages.ini` creation;
- minimal `packages.ini` parsing;
- locked package build selection;
- command-line build override of a locked package only when installed;
- project cache winning over compiler/global cache;
- missing package diagnostics that recommend `campc restore`;
- `versions.ini` parsing;
- invalid `versions.ini` diagnostics;
- package identity mismatch diagnostics;
- source archive hash mismatch diagnostics;
- deterministic extraction layout;
- `pkg install` into project cache;
- `pkg install --global`;
- `pkg uninstall` for exact versions and whole packages;
- `pkg publish` with exact version;
- `pkg publish` with `+major`, `+minor`, and `+patch`;
- `pkg publish` selecting the only `.campbuild` file in the current directory;
- `pkg publish` requiring an explicit build file when zero or multiple build
  files are present;
- `pkg publish` defaulting to `<project-root>/pub/<package-name>`;
- `pkg publish --out`;
- publish failure for duplicate version;
- publish failure when package name/version cannot be inferred and required
  arguments are missing.

Add integration tests for:

- building an app from a restored source-only package;
- default `:shared` package consumption;
- explicit `:static` package consumption;
- `:api` package consumption without native package library output;
- transitive package restore through `versions.ini use=`;
- LSP diagnostics when a locked package is missing;
- LSP analysis when a locked package is installed.

Add HTTP restore tests if the test infrastructure has a lightweight local HTTP
server helper. If not, keep HTTP coverage narrow and mostly parser/URI based
until such a helper exists.

## Regression Risks

Package lookup behavior will intentionally change. Any tests or workflows that
use `--use-source` as live build input must move to restore/install first.

The highest-risk areas are:

- standard-library package preparation, because it uses related package cache
  machinery but should remain compiler-owned;
- LSP project loading, because it currently has tests expecting package source
  freshness over stale cache entries;
- API/static/shared package artifact cache invalidation;
- native shared package copying beside final executable outputs;
- sourcefile path mapping for files under package cache roots;
- project-reference behavior that shares package-reference code paths;
- command-line parsing and response-file rebasing for `--use-source`;
- package version parsing, because package versions are now explicitly
  three-component major/minor/patch values and existing parser call sites should
  be audited for older assumptions;
- cross-platform path/URL handling for local and HTTP sources;
- deterministic zip creation and extraction;
- clear diagnostics for lock/catalog/cache mismatches.

## Acceptance Criteria

The feature is complete when:

- `campc restore` resolves source-only packages from named local and HTTP/HTTPS
  sources;
- `campc restore` writes minimal committed `packages.ini`;
- ordinary build/test/run/dump/LSP never contact package sources;
- ordinary build selects locked installed packages when a lock exists;
- project cache is searched before compiler/global cache;
- default package reference type remains `:shared`;
- `campc pkg publish` writes source archives and `versions.ini`;
- `campc pkg publish` defaults to `<project-root>/pub/<package-name>`;
- `campc pkg install` and `uninstall` operate on package caches without editing
  `packages.ini`;
- global source commands work;
- removed commands are no longer documented or accepted;
- package docs and LLM guidance describe the new model;
- targeted package tests and relevant LSP/project-loader tests pass;
- platform-specific package behavior is covered where native shared/static
  outputs differ.
