# Standard Library Build Integration

The standard library is bundled with the compiler installation and prepared
through the same package machinery used for ordinary packages. The user-facing
language docs mention only the small standard-library surface needed for
examples; exact API details should come from source, generated API headers, or
metadata.

The compiler treats the Camp installation root as the parent of `bin` in a
normal install. Set `CAMP_HOME` when the tools are launched from a layout where
that root cannot be inferred.

## Source Layout

The standard library source package lives under:

```text
lib/std/src/
```

It contains Camp source files, native C helper files, and helper headers. The
current source tree includes areas such as arrays, allocation, strings, console,
file I/O, formatting, hash collections, math, parsing, streams, timing, UTF-8,
UTF-16, and platform abstraction helpers.

The compiler treats `lib` as the bundled package source root, and `std` as a
package under that root.

## Default Inclusion

Unless `--nostdlib` is supplied, the compiler prepares package `std` before
loading root source files. The prepared Camp API header is added to the include
list, and each ordinary source file receives an implicit root import equivalent
to `using Std;`, so ordinary user source can reference `Std` declarations:

```camp
export int main()
{
	Console.writeLine("hello");
	return 0;
}
```

This default applies to command-line builds, dumps, language-service loose-file
analysis, and project analysis. If a file contains an explicit root `Std` import
such as `using Std;`, `using Std as S;`, or `using Std { Console };`, that
explicit import replaces the implicit root import for that file. Imports of
unrelated namespaces or child namespaces do not replace it.

## `--nostdlib`

`--nostdlib` disables automatic standard library preparation:

```sh
campc build tests/minimal.camp --nostdlib --artifact none
```

Use it for:

- language tests that define their own extern runtime surface;
- compiler golden tests where `Std` declarations would add noise;
- freestanding interop experiments;
- target or lowering tests that should not depend on bundled library sources.

Source that uses `Std` declarations will fail analysis under `--nostdlib` unless
it provides equivalent declarations through API files or root source files.

## `lib/global.camp`

`lib/global.camp` is read during build-option collection before local source
pragmas. It is the global place for compiler-checkout build defaults such as
package sources. It is not a replacement for the standard library package, and
user package restore does not route the standard library through `packages.ini`.

Because global pragmas affect every build using the checkout, keep this file
small and avoid project-specific settings. Project-specific package sources and
uses belong in source preludes or `.campbuild` files.

## API Preparation

For analysis-only requests, the compiler prepares a Camp API header:

```text
cache/lib/std/bin/<artifact-directory>/std_api.camp
```

That header is included in the root compilation. It contains source-level
exported declarations, not generated C implementation details.

When the standard library API is current and no command-line defines are active,
the compiler reuses the cached header. Otherwise it rebuilds the API from
`lib/std/src`.

## Native Helper Sources

For native builds, the standard library may compile native C helper files from
`lib/std/src` along with generated C from Camp source. Helper headers and C
files are cache inputs. The target's compiler and build templates determine how
those C files are compiled.

Native helper sources are implementation details of `std`. User code should
depend on the Camp API, not on private helper symbols, unless the standard
library explicitly exports an interop surface.

## Native Package Artifact

When the root build requests a native artifact, the compiler builds `std` as a
static package dependency:

```text
cache/lib/std/bin/<target>_static_<PROFILE>/
  std_api.camp
  std_api.h
  std_api.json
  libstd.a
  build/
```

The static library is added to the root native link line. Static is used for the
bundled standard library so executable and library builds have one predictable
link dependency from the compiler checkout.

## Metadata Filtering

Metadata for a user project filters out standard library declarations because
they come from API headers, not from the project's own source module. A project
that imports `Std` should see its own exported declarations in metadata, not a
copy of the standard library declaration tree.

Tools that need the standard library metadata should build or dump metadata for
`std` itself, or consume the generated `std_api.json` artifact.

## Target Interaction

The selected target affects standard library preparation in the same way it
affects any package:

- target-owned preprocessor defines select platform-specific source;
- target C spellings affect generated C;
- target build templates compile native helpers;
- target artifact naming controls the package cache directory;
- target import/export prefixes affect C API headers where relevant.

If a target lacks a primitive, build template, framework capability, or helper
C function required by the standard library, the standard library package build
reports the diagnostic before the root build can succeed.

## Package Cache Inputs

The standard library package cache considers:

- `lib/std/src/**/*.camp`;
- `lib/std/src/**/*.c`;
- `lib/std/src/**/*.h`;
- selected target metadata;
- compiler binaries;
- relevant global configuration;
- command-line defines.

Command-line defines disable package cache reuse for that request. This avoids
using a cached standard library API or native library built under a different
conditional source surface.

## Runtime Tests And Scratch Cache

Runtime tests may use a shared package cache under `tmp/` to avoid rebuilding
the standard library for each case. That cache is scratch output, not
source-of-truth. Documentation should refer to the normal `cache/lib/std` and
package-cache layout, not test-only scratch paths.

## Documentation Boundary

Keep standard-library prose in language docs minimal and semantic. The standard
library API is expected to evolve, and duplicating large API tables in the
language reference creates unnecessary maintenance. Prefer examples that use
small, stable surfaces such as `Console.writeLine`, arrays, strings, and
allocation only where they clarify language behavior.
