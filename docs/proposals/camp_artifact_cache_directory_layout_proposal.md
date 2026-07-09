# Camp Artifact And Cache Directory Layout Proposal

Status: draft
Audience: compiler and package-system implementation agents

## Summary

Rework Camp build output and dependency cache layout so generated artifacts are predictable, easy to clean, and separated by target, selected variants, library kind, and profile. The main rule is simple:

- user-visible build products go under `bin/`;
- generated build intermediates go under a `build/` subdirectory beside those products;
- dependency source and dependency build caches go under `cache/`;
- deleting `cache/` should be enough to force dependency restore/rebuild;
- deleting `bin/` should be enough to remove project-owned outputs.

The `--build-dir` option is removed. Build intermediates are always placed beneath the resolved output artifact directory. The default `--out-dir` is `bin`, relative to the `.campbuild` file, or relative to the first source file when no `.campbuild` is used.

This proposal also defines where compiler-versioned libraries such as `std`, globally installed packages, local workspace package dependencies, live source dependencies, project-reference outputs, and shared dependency copies should live.

## Goals

- Make generated output locations obvious from the project being built.
- Make `cache/` fully generated and safe to delete.
- Avoid writing live dependency build artifacts into external source repositories.
- Avoid package/reference artifact collisions across target, variant, library kind, and profile.
- Make project-reference output behavior match direct project builds.
- Preserve a simple user-facing default: `campc build app.campbuild` produces outputs under `bin/`.
- Support static and shared dependency builds as separate cached artifacts.
- Make dependency link kind explicit on the dependency edge, with `shared` as the default.

## Non-Goals

- This does not redesign package version resolution.
- This does not add lock files.
- This does not specify remote network package transport.
- This does not require compiler-versioned libraries other than `std` to exist yet.
- This does not define per-dependency source compatibility metadata beyond the open questions below.

## Terminology

- **Workspace**: a user project root. For a `.campbuild` file, the workspace-relative output root is based on that file unless configured otherwise.
- **Module**: a buildable Camp project or package unit with a `.campbuild` file or equivalent source list.
- **Compiler-versioned dependency**: a dependency shipped with and versioned by the Camp compiler, such as `std`.
- **Remote package dependency**: a package installed from a package source into a generated `cache/pkg` source cache.
- **Live package dependency**: a package referenced from an external source folder without copying its source into the current workspace cache.
- **Project reference**: a direct reference from one workspace module to another workspace module.
- **Artifact directory**: the directory containing final build outputs for one target/variant/libtype/profile combination.
- **Build directory**: the `build/` subdirectory inside an artifact directory.
- **Library type**: `static` or `shared` for library artifacts. Executable artifacts omit library type from the directory name.

## Artifact Directory Name

Artifact directories use this shape:

```text
<target>[_<variant>][_static|_shared]_<profile>
```

Examples:

```text
msvc-windows-x86_ansi_static_DEBUG
msvc-windows-x86_unicode_shared_RELEASE
clang-macos-x64_DEBUG
gcc-linux-x64_static_RELEASE
```

The variant portion uses the target variant directory-name rules. Variants are concatenated in target-declared order. Default variants should continue to be omitted from the name unless the existing variant model is changed separately.

Executable builds omit `_static` and `_shared`. Library builds include exactly one of `_static` or `_shared`.

## Project Output Layout

The default `--out-dir` is `bin`.

When a `.campbuild` file is used, the default `bin` path is relative to the directory containing that `.campbuild` file.

When no `.campbuild` file is used, the default `bin` path is relative to the directory containing the first source file in the build list.

By default, `--out-dir` acts as a prefix. The compiler creates an artifact subdirectory inside it:

```text
<out-dir>/<artifact-directory>/
    build/
        *.c
        *.h
        *.obj or *.o
        other generated/intermediate files
    final artifact files
    generated API/header/metadata files
```

If the user explicitly writes `--out-dir bin/.`, output goes directly into `bin` and the build directory is `bin/build`:

```text
bin/
    build/
        *.c
        *.h
        *.obj or *.o
    app.exe
```

This direct-output spelling is an explicit escape hatch for users who do not want a target/profile subdirectory.

In a multi-module workspace, the compiler does not infer module-specific output folders from a workspace layout. Each module has its own `--out-dir`. The recommended shape is for each module `.campbuild` to use an output directory such as `../bin/<module-name>`. A module can explicitly choose `--out-dir ../bin/.`, but that bypasses the artifact subdirectory prefix for that module and may collide with other modules unless the user is deliberately managing the layout.

## Compiler Repository Layout

The compiler repository should have this conceptual shape:

```text
[repo]/
    bin/
        campc
        campc-lsp
    lib/
        std/
            src/
                *.camp
    cache/
        lib/
            std/
                bin/
                    <artifact-directory>/
                        build/
                            *.c
                            *.h
                            *.obj or *.o
                        std.dll or libstd.a or std.lib
                        std.h
                        std_api.camp
                        std_api.json
        pkg/
            ext-win32/
                <version>/
                    src/
                        *.camp
                    ext-win32.campbuild
                    bin/
                        <artifact-directory>/
                            build/
                                *.c
                                *.h
                                *.obj or *.o
                            ext-win32.dll or ext-win32.lib or libext-win32.a
                            ext-win32_api.h
                            ext-win32_api.camp
                            ext-win32_api.json
    targets/
        *.ini
```

`cache/lib` is for compiler-versioned dependencies only. User workspaces do not use `cache/lib` in v1. For consistency with package caches, compiler-versioned dependency build outputs use a `bin/` subfolder under the dependency root.

`cache/pkg` under the compiler root is for globally installed non-compiler-versioned packages. The old compiler-root `pkg/` directory is removed; installed package source and generated package artifacts live under `cache/pkg`.

## Remote Package Source Layout

A package source is source-only:

```text
[remote-package-source]/
    ext-win32/
        <version-or-latest>/
            ext-win32.campbuild
            src/
                *.camp
```

Installing a package from this source copies the selected version into a generated `cache/pkg` location. The copied package source and all built artifacts are cache data.

## Local Or Live Package Source Layout

A live package source is also source-only:

```text
[local-package-source]/
    win32-forms/
        <version-or-latest>/
            win32-forms.campbuild
            src/
                *.camp
```

When a workspace consumes this as a live dependency, the compiler should not write build products into the external source folder. Instead, it writes the live dependency artifact cache into the consuming workspace:

```text
[workspace]/
    cache/
        pkg/
            win32-forms/
                <version-or-live>/
                    bin/
                        <artifact-directory>/
                            build/
                                *.c
                                *.h
                                *.obj or *.o
                            win32-forms.dll or win32-forms.lib
                            win32-forms_api.h
                            win32-forms_api.camp
                            win32-forms_api.json
```

For live dependencies, the workspace cache does not create or use a `src/` copy. It points at the live source location for source input, while storing artifacts locally.

## Multi-Module Workspace Example

Assume:

- `forms-sample` depends on `win32-forms-extras` as a shared dependency;
- `win32-forms-extras` depends on `win32-forms` as a static dependency;
- `win32-forms` depends on `ext-win32`;
- `ext-win32` is globally installed and declarations-only;
- `win32-forms` is a live dependency.

The workspace layout is:

```text
multi-module-workspace/
    bin/
        win32-forms-extras/
            <target>_<variant>_shared_<profile>/
                build/
                    *.c
                    *.h
                    *.obj or *.o
                win32-forms-extras.dll
                win32-forms-extras_api.h
        forms-sample/
            <target>_<variant>_<profile>/
                build/
                    *.c
                    *.h
                    *.obj or *.o
                forms-sample.exe
                win32-forms-extras.dll
    cache/
        pkg/
            win32-forms/
                <version-or-live>/
                    bin/
                        <target>_<variant>_static_<profile>/
                            build/
                                *.c
                                *.h
                                *.obj or *.o
                            win32-forms.lib
                            win32-forms_api.h
                            win32-forms_api.camp
                            win32-forms_api.json
    src/
        win32-forms-extras/
            win32-forms-extras.campbuild
            src/
                *.camp
        forms-sample/
            forms-sample.campbuild
            src/
                *.camp
```

`ext-win32` is globally installed, so it is not copied into this workspace cache. The compiler reads it from the compiler-root/global package cache.

`win32-forms` is statically linked into `win32-forms-extras.dll`, so it does not appear beside `forms-sample.exe`.

`win32-forms-extras.dll` is a shared dependency of `forms-sample`, so it is copied beside `forms-sample.exe`.

## Single-Module Workspace Example

Assume:

- `single-module-workspace` depends on `win32-forms` as a shared dependency;
- `win32-forms` depends on globally installed `ext-win32`.

```text
single-module-workspace/
    bin/
        <target>_<variant>_<profile>/
            build/
                *.c
                *.h
                *.obj or *.o
            single-module-workspace.exe
            win32-forms.dll
    cache/
        pkg/
            win32-forms/
                <version-or-live>/
                    bin/
                        <target>_<variant>_shared_<profile>/
                            build/
                                *.c
                                *.h
                                *.obj or *.o
                            win32-forms.dll
                            win32-forms_api.h
                            win32-forms_api.camp
                            win32-forms_api.json
    src/
        *.camp
    single-module-workspace.campbuild
```

## Dependency Artifact Selection

The consuming project selects all build-affecting options for its dependencies:

- target;
- variants;
- profile;
- static vs shared dependency kind;
- relevant target-owned defines;
- relevant target capabilities.

A dependency may have defaults in its own `.campbuild`, but those defaults apply only when that dependency is built directly as the root project. When the dependency is built as a package or project reference, the consuming root project determines the target, variants, and profile.

This matches the existing variant rule: project references and packages must not use their own target/variant defaults when consumed by another project.

Arbitrary `--define` values are current-project only. They do not flow into package or project-reference builds, and they do not participate in dependency cache keys. Target-owned defines still flow from selected target variants and are represented by the artifact directory name through target/variant selection. If a future feature needs dependency-affecting defines, it should be explicit and cache-keyed separately.

## Static And Shared Dependencies

The dependency edge must be able to specify whether a dependency is consumed as static or shared.

`shared` is the default dependency link kind.

Package references encode dependency link kind after the package identity:

```text
--use win32-forms@live:static
--use win32-forms@1.2.3:shared
--use win32-forms:static
--use win32-forms
```

`--use win32-forms:static` means “use the latest matching package version and consume it as a static dependency.” `--use win32-forms` means “use the latest matching package version and consume it as a shared dependency.”

The same `pkg@version:kind` spelling should be accepted anywhere package references are accepted, including `#build --use ...`, `.campbuild` arguments, `campc pkg add`, restore, and build-option parsing.

Conceptually:

- static dependency:
  - build or reuse `<dependency>_<target>_<variant>_static_<profile>`;
  - pass the resulting static library to the linker for the dependent artifact;
  - do not copy the static dependency beside the final executable/shared object.
- shared dependency:
  - build or reuse `<dependency>_<target>_<variant>_shared_<profile>`;
  - link against the import/shared library as needed;
  - copy the runtime shared artifact beside the final executable or shared dependent artifact when appropriate.

Shared dependency copying is transitive for final runnable artifacts. A final executable directory should contain every runtime shared dependency needed to run it. Static dependencies are absorbed into the artifact that links them and are not copied.

Dependencies can declare allowed artifact consumption by using a root artifact mode:

```text
--artifact only-static
--artifact only-shared
```

`only-static` behaves like `static` when the package/project is built directly, but when consumed as a dependency or project reference, the compiler rejects attempts to consume it as `shared`.

`only-shared` behaves like `shared` when the package/project is built directly, but when consumed as a dependency or project reference, the compiler rejects attempts to consume it as `static`.

Plain `--artifact static` and `--artifact shared` continue to mean “build this project as static/shared when it is the root build,” without prohibiting the other link kind when the project is built as a dependency.

Declarations-only packages follow the same cache layout as regular packages, including separate `_static` and `_shared` artifact directories. They do not produce a static or shared object artifact, but they may still produce generated API/header/metadata files in the corresponding artifact directory.

## Current Shared-Library Support Assessment

The compiler already has partial shared-library support:

- `NativeBuildKind.Shared` exists.
- Target files define shared build templates for MSVC, clang/macOS, and gcc/Linux.
- Targets can define `dll_export_prefix`, and the C emitter applies it to exported declarations when the current project is being built as a shared library.
- `shared_cflags` exists for hidden-symbol defaults on clang/gcc-style targets.
- The MSVC test suite contains a smoke test proving that a single project can be built as a `.dll` and that exported declarations receive `__declspec(dllexport)`.

This does not yet prove that shared dependencies work correctly. The current project-reference/package path is effectively static-library shaped:

- package builds in `CompilerDriver` currently build dependencies as `NativeBuildKind.Static`;
- project-reference tests are named and asserted around static libraries;
- transitive dependency flow tests verify static libraries flowing to the final link;
- there is no end-to-end test where one Camp project builds a shared library and a second Camp project consumes it through the generated Camp API and C API;
- there is no consumer-side C header mode that emits import decoration for shared dependencies;
- there is no runtime-copy test proving that shared dependency binaries are placed beside the final executable.

Therefore this proposal must treat shared-dependency support as unfinished, even though root shared-library artifact generation exists.

## Shared Library ABI Headers

Shared dependencies require producer and consumer header modes.

When building a shared library itself, exported C declarations use the target's export spelling:

- MSVC: `__declspec(dllexport)`;
- clang/gcc targets with hidden visibility: `__attribute__((visibility("default")))`;
- targets without explicit export decoration: empty spelling.

When consuming a shared library, exported C declarations in the dependency's public C header must use the target's import spelling:

- MSVC: `__declspec(dllimport)`;
- ELF/Mach-O gcc/clang targets: usually empty unless a target chooses otherwise.

When consuming a static library, declarations should not use shared import decoration.

The target model should add import-decoration settings next to the existing export-decoration settings. Suggested initial keys:

```ini
[cemit]
dll_export_prefix=__declspec(dllexport)
dll_import_prefix=__declspec(dllimport)
```

Suffix support may be added if a future target needs it, but v1 can start with prefix-only if all supported targets fit that shape.

The implementation may choose either:

- a macro-driven public C header that selects export/import/static mode through preprocessor defines; or
- separate producer/consumer/static header emission modes.

The important semantic requirement is that the header used while building the shared library exports symbols, while the header used by consumers imports them where the target requires that.

The Camp `.camp` API surface is source-level and should not expose C import/export decoration. It remains the same for static and shared consumers except where the dependency edge or artifact restriction changes whether the dependency can be consumed.

## Shared Dependency Linking And Runtime Files

Shared dependencies need both link-time and runtime artifacts.

On MSVC:

- building a shared library produces a runtime `.dll` and an import `.lib`;
- consumers link against the import `.lib`;
- final executable outputs must copy the runtime `.dll` beside the executable;
- shared-library consumers may also need runtime DLLs copied beside their own artifact if they are intended to be loaded directly.

On macOS:

- building a shared library produces a `.dylib`;
- consumers link against that `.dylib` or an appropriate linker reference;
- runtime lookup must be made predictable. The first implementation may copy the `.dylib` beside the executable and use the existing target link behavior, but if install names/rpaths are needed, the target template should own that policy.

On Linux:

- building a shared library produces a `.so`;
- consumers link against that `.so` or an appropriate linker reference;
- runtime lookup must be made predictable. The first implementation may copy the `.so` beside the executable and use target link/rpath behavior where configured.

The dependency graph must distinguish:

- link artifact: the file passed to the native linker;
- runtime artifact: the file copied beside a final executable or shared artifact when needed;
- generated API artifacts: Camp API, C API header, and metadata.

Static dependencies only contribute link artifacts. Shared dependencies contribute link artifacts and runtime artifacts.

## `cache/` Semantics

All of these are generated cache:

- downloaded/copied package source in `cache/pkg`;
- package build outputs in `cache/pkg/**/bin`;
- compiler-versioned library outputs in compiler-root `cache/lib`;
- dependency generated C/header/object files;
- package API/header/metadata outputs.

`cache/` is always safe to delete. A future `campc restore` should be able to recreate source cache for installed packages, and a future build should be able to recreate artifact cache.

The compiler must not place user-authored source files in `cache/` except when installing/copying package sources from a package source.

## CLI Changes

### Remove `--build-dir`

`--build-dir` is removed.

Build intermediates always go in:

```text
<resolved artifact directory>/build/
```

For explicit direct output:

```text
--out-dir bin/.
```

the build directory is:

```text
bin/build/
```

### `--out-dir`

`--out-dir` remains.

Default:

```text
--out-dir bin
```

Resolution:

- relative to `.campbuild` file directory when building from a `.campbuild`;
- relative to the first source file directory when building direct source files.

Behavior:

- ordinary `--out-dir bin` creates `bin/<artifact-directory>/`;
- explicit `--out-dir bin/.` outputs directly into `bin/`.

### Dependency Link Kind

Dependency link kind is encoded as `:static` or `:shared` after the package identity:

```text
--use win32-forms@live:static
--use win32-forms@1.2.3:shared
--use win32-forms:static
```

If no kind is specified, the default is `shared`.

The parser must distinguish a version suffix from a link-kind suffix:

```text
name
name@version
name:kind
name@version:kind
```

Project references need an equivalent dependency-edge link-kind mechanism. The exact project-reference CLI spelling should match the existing project-reference option style while adding `:static` and `:shared` if possible.

### Dependency Artifact Restrictions

`--artifact only-static` and `--artifact only-shared` are added.

Allowed root artifact values become:

```text
exec
static
shared
only-static
only-shared
none
```

`only-static` produces a static library. `only-shared` produces a shared library. The “only” part is visible to dependency consumers and causes a diagnostic if a project/package is linked using the wrong dependency link kind.

Suggested diagnostics:

```text
Package 'win32-forms' only supports static dependency linking; use ':static' or change the package artifact policy.
Package 'win32-forms' only supports shared dependency linking; use ':shared' or change the package artifact policy.
```

## Compiler Touchpoints

Likely implementation areas:

- `src/Camp.Compiler/CampProjectLoader.cs`
  - resolve root output directory;
  - remove `BuildDir` option parsing;
  - compute artifact directory names;
  - compute package/project-reference cache locations.
- `src/Camp.Compiler/CompilerRequest.cs`
  - remove or deprecate `BuildDir`;
  - add dependency link-kind metadata if not already represented.
- `src/Camp.Compiler/CompilerDriver.cs`
  - route project outputs and generated files to the new layout;
  - ensure shared dependency copy behavior.
- `src/Camp.Compiler/CCodeEmitter.cs`
  - keep existing producer export decoration for shared-library builds;
  - add consumer import/static header modes for dependency C headers;
  - ensure public C headers do not expose producer-only `dllexport` decoration to consumers.
- `src/Camp.Compiler/NativeBuildDriver.cs`
  - use the computed build directory inside the artifact directory.
- `src/Camp.Compiler/TargetCatalog.cs`
  - reuse target variant directory naming;
  - ensure artifact directory generation uses target variant selection consistently.
- target `.ini` files
  - add consumer import-decoration keys where needed, especially MSVC `dll_import_prefix=__declspec(dllimport)`;
  - ensure shared-library templates produce both link and runtime artifacts in a discoverable way on MSVC;
  - document or encode rpath/install-name behavior for gcc/clang shared dependencies if needed.
- `src/camp-lsp`
  - update build-option parsing if the LSP reads `.campbuild` or `#build` options;
  - surface diagnostics for removed `--build-dir`, malformed `pkg@version:kind`, and disallowed dependency link kinds;
  - ensure document/workspace indexing resolves source/cache/generated paths using the new layout;
  - ensure generated `cache/` and `bin/` trees are not treated as project source roots unless explicitly opened.
- Command-line tests in `src/Camp.Compiler.TestRunner/CommandLineTests.cs`
  - update existing output-path expectations;
  - add package/project-reference cache layout tests.
- LSP tests in `src/Camp.Compiler.TestRunner/LanguageServiceTests.cs` or the relevant LSP test file
  - add smoke coverage for `.campbuild`/build-option parsing and diagnostics if the current LSP harness permits it.
- Golden test harnesses
  - update any tests that explicitly pass `--build-dir`.

## Suggested Implementation Stages

### ~~Stage 1: Artifact Directory Computation~~

- ~~Add a central artifact directory name helper.~~
- ~~Include target, selected variants, library type for library builds, and profile.~~
- ~~Omit library type for executable builds.~~
- ~~Preserve existing default-variant elision.~~
- ~~Add unit tests for naming.~~

### ~~Stage 2: Remove `--build-dir`~~

- ~~Remove CLI parsing and request plumbing for `--build-dir`.~~
- ~~Compute build directory as `<artifact-dir>/build`.~~
- ~~Implement `--out-dir path/.` direct-output behavior.~~
- ~~Update command-line tests.~~

### ~~Stage 3: Root Project Output Layout~~

- ~~Apply the new output layout to direct source builds and `.campbuild` builds.~~
- ~~Ensure relative `--out-dir` resolves from the correct base.~~
- ~~Update command-line, native build, and smoke tests.~~

### ~~Stage 4: Package And Project Reference Cache Layout~~

- ~~Move project-reference and package dependency artifacts into the appropriate `cache/pkg` or project `bin` locations.~~
- ~~Keep live source build artifacts in the consuming workspace cache.~~
- ~~Avoid writing generated files into external live package source folders.~~
- ~~Ensure globally installed packages are read from compiler-root `cache/pkg`.~~

### ~~Stage 5: Static/Shared Dependency Link Kind~~

- ~~Add dependency-edge link-kind representation using `pkg@version:static`, `pkg@version:shared`, `pkg:static`, and `pkg:shared`.~~
- ~~Default dependency link kind to `shared`.~~
- ~~Add `--artifact only-static` and `--artifact only-shared`.~~
- ~~Build/cache static and shared dependency artifacts independently.~~
- ~~Copy shared runtime dependencies beside consuming artifacts.~~
- ~~Do not copy static dependencies.~~
- ~~Add multi-module tests with static dependency inside shared dependency inside executable.~~

### ~~Stage 6: Shared Dependency ABI And Runtime Handling~~

- ~~Add target import-decoration support for shared-library consumers.~~
- ~~Teach C header emission to distinguish producer export mode, shared consumer import mode, and static consumer mode.~~
- ~~Ensure dependency resolution records both link-time and runtime artifacts.~~
- ~~On MSVC, link consumers against the generated import `.lib` and copy the `.dll` beside final runnable artifacts.~~
- ~~On macOS/Linux, link consumers against the generated shared artifact and ensure runtime lookup/copy behavior is target-defined and tested.~~
- ~~Ensure shared runtime dependency copying is transitive and does not copy static-only dependencies.~~
- ~~Ensure declarations-only packages still emit API/header/metadata in `_static` and `_shared` artifact directories even without object artifacts.~~
- ~~Add end-to-end shared dependency tests:~~
  - ~~root shared library build still exports symbols;~~
  - ~~executable consumes a shared project reference and runs;~~
  - ~~executable consumes a shared package dependency and runs where package tests are available;~~
  - ~~MSVC consumer C header uses `__declspec(dllimport)`;~~
  - ~~static consumer C header does not use `dllimport`;~~
  - ~~shared dependency DLL/runtime file is copied beside the executable;~~
  - ~~transitive shared dependencies are copied once.~~

### Stage 7: LSP And Tooling Integration

- Update LSP/project-loading support for the new path model.
- Ensure LSP diagnostics understand removed `--build-dir`, malformed dependency-kind suffixes, and wrong dependency link kinds.
- Ensure LSP file watching or workspace indexing does not treat generated `cache/` and `bin/` outputs as ordinary source roots unless explicitly opened.
- Add LSP tests or targeted language-service tests for `.campbuild`/build-option parsing where the current harness permits it.

### Stage 8: Restore/Clean Documentation

- Document that `cache/` is deletable.
- Document output path rules.
- Update the LLM guide and command-line docs.
- Update any examples that still reference `pkg/bin`, `pkg-source`, or `--build-dir`.

## Test Plan

- Artifact directory naming tests:
  - executable omits libtype;
  - static/shared libraries include libtype;
  - default variants are omitted;
  - non-default variants are ordered by target declaration order;
  - profile is included.
- CLI tests:
  - `campc build file.campbuild` writes under `bin/<artifact-directory>/`;
  - direct source-file build bases default `bin` on the first source file;
  - `--out-dir custom` creates `custom/<artifact-directory>/`;
  - `--out-dir custom/.` writes directly into `custom/`;
  - `--build-dir` reports a migration diagnostic.
- Project-reference tests:
  - direct referenced project build and referenced-as-dependency build use matching artifact layout;
  - root target/variant/profile controls referenced artifacts;
  - referenced project’s own defaults are ignored when consumed.
- Package tests:
  - remote package install copies source into `cache/pkg/<pkg>/<version>/src`;
  - live package builds write artifacts to workspace `cache/pkg`, not external source;
  - global installed packages are not copied into workspace cache;
  - declarations-only package uses separate `_static` and `_shared` artifact directories without creating a meaningless static/shared binary.
- Link-kind tests:
  - `--use win32-forms@live:static`;
  - `--use win32-forms:static` resolves the latest available version;
  - omitted dependency kind defaults to shared;
  - `--artifact only-static` rejects shared dependency consumption;
  - `--artifact only-shared` rejects static dependency consumption;
  - static dependency is linked into shared dependency and not copied to final executable directory;
  - shared dependency is copied beside final executable;
  - static and shared cached artifacts for the same package do not collide.
- Shared dependency ABI tests:
  - root shared-library build emits target export decoration in the producer C header;
  - MSVC shared dependency consumer sees `__declspec(dllimport)` in the dependency C header or equivalent import mode;
  - static dependency consumer does not see `dllimport`;
  - executable links against a shared project-reference dependency and successfully runs;
  - executable links against a shared package dependency and successfully runs where package fixtures are available;
  - MSVC final executable links against the dependency import `.lib` and receives the runtime `.dll` beside the executable;
  - macOS/Linux final executable can locate the dependency `.dylib`/`.so` using the target-defined copy/rpath/install-name policy;
  - transitive shared dependencies are copied beside the final runnable artifact exactly once;
  - declarations-only dependencies still produce separate static/shared API/header cache directories without object artifacts.
- LSP/tooling tests:
  - `.campbuild` diagnostics still point at the offending option after `--build-dir` removal;
  - malformed `pkg@version:kind` has a useful diagnostic;
  - generated `cache/` paths are not treated as project source files by default.
- Regression tests:
  - std builds from compiler-root `lib/std` cache into compiler-root `cache/lib/std/bin`;
  - metadata and API outputs still appear beside the relevant artifact;
  - existing Windows, macOS, and Linux full suites pass.

## Open Questions

1. What is the exact project-reference syntax for static/shared consumption?

   Best guess: mirror package references with a suffix if possible, for example `--project-reference ../win32-forms:static`. If the current parser treats project-reference values as paths, suffix parsing must avoid confusing Windows drive letters.

2. What is the exact workspace root for a live package dependency cache?

   Best guess: the workspace root is the directory containing the root `.campbuild` file. For direct source-list builds, it is the directory containing the first source file.

3. Should generated API files for static libraries be named `_api.h` and placed beside the static library, or should public C headers use the artifact name without `_api`?

   Best guess: preserve current naming conventions initially, but place them beside the artifact in the new artifact directory. Any rename should be a separate API packaging proposal.

4. Does the LSP currently parse enough `.campbuild` state to need full cache-layout awareness?

   Best guess: yes, at least enough for diagnostics and workspace discovery. Even if the LSP does not build dependencies, it should understand the new option forms and should not index generated `cache/` trees as source by accident.

5. Should shared dependency C headers be macro-switchable or generated separately per consumption mode?

   Best guess: start with explicit emission modes because the artifact directory already separates `_static` and `_shared`. If multi-target distributable headers later need one header to work across static/shared/MSVC/clang/gcc consumers, move to a macro-driven header once the semantics are proven.

6. How should macOS/Linux runtime lookup be configured for copied shared dependencies?

   Best guess: keep this target-owned. The initial implementation should copy `.dylib`/`.so` beside the final executable and use target templates/capabilities for any rpath or install-name flags needed to make that runnable.

5. Should `--out-dir bin/.` be permitted in package/dependency builds?

   Best guess: it should be honored only for direct root builds. Dependency builds should always use cache-managed artifact directories to prevent collisions.
