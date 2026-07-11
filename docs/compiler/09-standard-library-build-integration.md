# Standard Library Build Integration

## Default Standard Library Inclusion

The compiler includes the standard library package by default. Standard library
API headers and native helper sources are prepared before root source files are
loaded.

## `--nostdlib`

`--nostdlib` omits the standard library package. Use it for isolated language
tests, freestanding interop experiments, or compiler golden cases that should
not depend on bundled declarations.

## `lib/global.camp`

`lib/global.camp` contains global Camp declarations and build defaults. It is
read as part of effective build pragma processing and standard-library surface
setup.

## Standard Library Package Build

The standard library is built as a package dependency when a native artifact is
requested. Its artifact kind and target/profile layout follow the same package
cache rules as other dependencies.

## Native Helper Sources

The standard library includes Camp sources and native C helper sources. Native
helper files are compiled with the selected target toolchain as part of the
standard library package artifact.

## StdRun Package Cache

Runtime tests share a standard-library package cache under `tmp/` so the
standard library does not need to rebuild for every runtime case. That cache is
scratch output.

## Consuming Std Metadata

Tools that need exact standard-library API details should consume generated
metadata or source rather than relying on broad prose docs. The language
reference intentionally documents only the small standard-library surface needed
for examples and semantics.
