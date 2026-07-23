# Camp

Camp is a C-like systems programming language that lowers modern source forms to ordinary, interoperable C-compatible output. It keeps the familiar parts of C's reading model while adding language features such as strings, arrays, classes, interfaces, overloads, iterators, async/await, generics, metadata, allocators, first-class testing, and coverage.

The compiler, language server, debug adapter, standard library, target files, editor support, documentation, and tests live in this repository.

## Status

Camp is early software. The first preview release is planned as `v0.1.0-preview.1`.

## Build From Source

Install the .NET 10 SDK, then build the solution:

```bash
dotnet build src/camplang.sln
```

The development build copies the tools into `bin/`:

```bash
bin/campc --help
bin/campc build path/to/program.camp
bin/campc run path/to/program.camp
bin/campc test path/to/program.camp
bin/campc cover path/to/program.camp
```

Native compilation also requires a C compiler for the selected Camp target. On macOS and Linux, Clang is the usual default. On Windows, use a Visual Studio C++ developer environment for MSVC targets.

## Publish Tools

Release-style tool binaries are produced with `src/publish-tools.proj`:

```bash
dotnet msbuild src/publish-tools.proj -p:RuntimeIdentifier=osx-x64
```

The published tools are written to `bin/publish/<rid>/`.

Supported preview distribution targets are planned to be:

- `win-x64`
- `win-x86`
- `linux-x64`
- `osx-x64`
- `osx-arm64`

When installed outside the source tree, Camp expects its installation root to contain `bin`, `lib`, `targets`, and `cache`. Set `CAMP_HOME` to override automatic installation-root discovery.

## Documentation

- Language guide: `docs/language/`
- Compiler guide: `docs/compiler/`
- Compiler development guide: `docs/compiler-development-guide.md`
- Documentation contributor guide: `docs/documentation-contributor-guide.md`

## License

Camp is licensed under the MIT License. See `LICENSE`.

Programs compiled with Camp are not considered derivative works of Camp merely because they were compiled with Camp, linked with the Camp standard library/runtime, or contain compiler-generated support code.

The Camp name, logo, domains, and release branding are project identity assets. See `TRADEMARKS.md`.
