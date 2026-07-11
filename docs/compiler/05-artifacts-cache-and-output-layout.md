# Artifacts, Cache, And Output Layout

## Output Directories

`--out-dir` selects the final artifact directory. When a build file is used and
`--out-dir` is omitted, the default is a `bin` directory next to the build file.

## Artifact Directory Names

Artifact directories combine selected target, non-default variants, link kind
when relevant, and profile:

```text
gcc-linux-x64_DEBUG
gcc-linux-x64_static_RELEASE
msvc-windows-x64_unicode_shared_DEBUG
```

## Build Intermediates

Native build intermediates are written under the selected output layout's
`build` subdirectory. Object file extensions come from the selected target.

## Static Libraries

Static artifacts use the target's static prefix and extension, such as
`libname.a` or `name.lib`.

## Shared Libraries

Shared artifacts use the target's shared prefix and extension. Some targets
also produce an import library for linking.

## Executables

Executable artifacts use the target's executable prefix and extension. A Windows
subsystem build uses the `winexe` build template.

## Runtime Files

Shared libraries may produce runtime files that must be copied or distributed
with consumers. Import libraries are link files when the target creates them.

## Link Files

Link files are the artifacts passed to downstream native link commands. Static
libraries and executable dependencies usually link the artifact path directly.
Shared dependencies may link an import library or the shared library itself.

## Package Cache Layout

Packages are installed under global or local `cache/pkg` roots. Package build
artifacts use the same target/profile/link-kind directory naming as project
artifacts.

## Project Reference Cache Checks

Project references compare inputs such as build files, source files, includes,
target definitions, and relevant global configuration against current
artifacts. When current artifacts satisfy the request, the compiler can reuse
them.

## Cleaning And Rebuilding

Delete the relevant output or cache directory to force a rebuild. Standard
library runtime tests use a `tmp/` cache during test runs; that scratch cache is
not source-of-truth.
