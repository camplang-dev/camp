# Artifacts, Cache, And Output Layout

Camp output layout is target-aware and cache-friendly. Final outputs live under
an artifact directory named from target, non-default variants, link kind when
relevant, and profile. Native build intermediates live under that artifact
directory's `build` subdirectory.

## Output Roots

`--out-dir` selects the output root:

```sh
campc build src/main.camp --artifact exec --out-dir bin
```

If `--out-dir` is omitted and a build/run command uses a `.campbuild` file, the
default output root is `bin` beside the build file. If neither is true, the C
emitter chooses its default output root from the root source files.

The final artifact directory is normally:

```text
<out-root>/<artifact-directory-name>/
```

A direct output directory is selected by passing `.` or a path ending in `/.`:

```sh
campc build @mathlib.campbuild --out-dir bin/clang-macos-x64_static_DEBUG/.
```

In that case the compiler writes directly into the named directory instead of
adding another artifact-directory component.

## Artifact Directory Names

Artifact directory names are built by `BuildArtifactLayout`:

```text
<target-or-target-with-non-default-variants>[_static|_shared|_api]_<PROFILE>
```

Examples:

```text
clang-macos-x64_DEBUG
gcc-linux-x64_static_RELEASE
msvc-windows-x64_ansi_shared_DEBUG
msvc-win16-x86_large_static_DEBUG
```

Executable and C-only root builds do not include a link-kind part. Static and
shared library builds include `_static` or `_shared`. Package `:api`
dependencies use `_api`.

Default variants are omitted from the variant directory name. Non-default
variants are appended in target variant-group order.

## Generated C Files

The C emitter writes generated source and headers under the artifact directory.
The exact set depends on source declarations and artifact kind, but ordinary
builds may produce:

```text
textapp.c
textapp.h
textapp_private.h
```

The public header is for exported C-facing declarations. The private header is
for generated implementation dependencies. Library builds also emit a Camp API
header and a C API header:

```text
textlib_api.camp
textlib_api.h
textlib_api.json
```

The Camp API header is the source-level declaration surface consumed by other
Camp compilations. The C API header is for native consumers.

## Build Intermediates

Native object files are written under:

```text
<artifact-directory>/build/
```

Object extensions come from the target. Examples:

```text
bin/gcc-linux-x64_DEBUG/build/main.o
bin/msvc-windows-x64_DEBUG/build/main.obj
```

The native driver invokes the target's `compile` template once per generated C
source. It then invokes `exec`, `winexe`, `static`, or `shared` as appropriate.

## Executable Artifacts

Executable names use the target's executable prefix and extension:

```text
textapp
textapp.exe
```

Executable builds include an entry wrapper when needed so Camp `main` can keep
its source-level shape while the C output has the target's expected process
entry. `--subsystem windows` selects the `winexe` template.

## Static Libraries

Static library names use the target's static prefix and extension:

```text
libmathlib.a
mathlib.lib
```

Static archives are link files for downstream builds. Before writing a static
archive, the native driver deletes the existing archive at the output path so
stale object members cannot remain from a prior build.

## Shared Libraries

Shared library names use the target's shared prefix and extension:

```text
libgraphics.dylib
libgraphics.so
graphics.dll
```

Some targets also produce import libraries:

```text
graphics.lib
```

For shared builds, the runtime file is the shared library. The link file is the
import library when the target creates one; otherwise it is the shared library.
Downstream executable builds copy rooted shared runtime files next to the final
executable when the referenced file is not already there.

## Metadata Artifacts

Metadata JSON is named:

```text
<project>_api.json
```

Static and shared library builds default to `export` metadata. Other builds
default to no metadata unless `--metadata` requests a view. Metadata is written
beside the other final artifacts and is not part of the native build command.

See [Metadata JSON](06-metadata-json.md).

## Test And Coverage Artifacts

`campc test` and `campc cover` build generated harness executables in the
normal output root and artifact directory for the selected target/profile/output
settings. The harness source, generated C, native objects, runtime coverage
sources, event files, and counter files live under that directory's `build`
subdirectory.

The test manifest is emitted before the harness runs:

```text
<artifact-directory>/<project>.camp-test-manifest.json
```

When JSON test results are enabled, results are written beside the manifest by
default:

```text
<artifact-directory>/<project>.camp-test-results.json
```

`--test-output-dir` replaces the default directory for both the manifest and
test results.

`campc cover` also writes one coverage map per instrumented coverage subject:

```text
<artifact-directory>/<project>.camp-coverage-map.csv
```

Coverage maps are canonical CSV. Runtime counter files are generated scratch
intermediates under the build directory and are merged with the maps after the
harness exits.

Coverage results are written beside the coverage maps by default:

```text
<artifact-directory>/<project>.camp-coverage-results.json
<artifact-directory>/lcov.info
```

`--coverage-output-dir` replaces the default directory for both coverage maps
and coverage results.
`--coverage-format` controls whether JSON, LCOV, or both files are written.

## Package Artifact Cache

Bundled package artifacts use:

```text
<repo>/cache/lib/<package>/bin/<artifact-directory>/
```

Installed and live package artifacts use the package cache root:

```text
<repo>/cache/pkg/<package>/<version>/bin/<artifact-directory>/
<working-directory>/cache/pkg/<package>/<version-or-live>/bin/<artifact-directory>/
```

Package source directories are never used as build output directories. Live
source packages still write artifacts under the working-directory package cache.

## Project Reference Output

Project references are built into the referenced project's `bin` directory:

```text
mathlib/
  bin/
    clang-macos-x64_static_DEBUG/
      mathlib_api.camp
      mathlib_api.h
      mathlib_api.json
      libmathlib.a
      build/
        math.c
        math.o
```

When a consumer references `../mathlib:static`, the CLI forces the referenced
project to use the consumer target, profile, variants, static artifact kind, and
that output directory. `:shared` does the same with the shared artifact kind.

If the required API and library outputs are current, the reference build is
skipped and the current files are used. If not, `campc build` compiles the
referenced project before compiling the consumer.

## Project Reference Freshness Inputs

Project-reference cache checks consider:

- the referenced `.campbuild` file;
- `lib/global.camp`, when present;
- referenced root sources;
- API files;
- shared-library API headers used by the reference;
- rooted native references that exist as files or directories;
- the selected target INI file;
- the compiler assembly and process executable.

The oldest required output must be newer than all these inputs. Missing inputs
or outputs force a rebuild.

## Native Reference Resolution

`--reference` values are passed through these rules during native linking:

- rooted paths and path-like relative values become full paths relative to the
  build working directory;
- existing local files are used directly;
- values with extensions are passed through;
- bare names become `name.lib` for `.lib` targets and `-lname` otherwise.

This lets build files use either explicit library paths or ordinary linker
names:

```text
--reference third_party/libimage.a
--reference pthread
```

## Runtime File Copying

After a native build, rooted shared-library references are copied into the final
artifact directory when needed. If a reference is an import library and the
target defines a shared runtime extension, the compiler looks for a sibling
runtime library by changing the extension.

This copying is deliberately limited to rooted files the compiler can inspect.
Bare linker names and system libraries are left to the platform loader.

## Cleaning

There is no separate clean command. Delete the relevant output or cache
directory:

```sh
rm -rf bin/gcc-linux-x64_DEBUG
rm -rf cache/pkg/textlib/live/bin/gcc-linux-x64_shared_DEBUG
rm -rf ../mathlib/bin/gcc-linux-x64_static_DEBUG
```

Use the narrowest deletion that matches the stale artifact. Package sources and
installed package source trees should not be deleted as a substitute for
clearing generated output.
