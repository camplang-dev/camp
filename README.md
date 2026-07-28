# Camp

Camp is a C-like systems programming language that lowers modern source forms to ordinary, interoperable C-compatible output. It keeps the familiar parts of C's reading model while adding language features such as strings, arrays, classes, interfaces, overloads, iterators, async/await, generics, metadata, allocators, first-class testing, and coverage.

The compiler, language server, debug adapter, standard library, target files, editor support, documentation, and tests live in this repository.

## Status

Camp is early software. The current preview release is `v0.2.0-preview.1`.

## Prerequisites

Release builds of `campc`, `camp-lsp`, and `camp-dap` do not require .NET to be
installed.

To build or run Camp programs, install a native C toolchain for the target you use:

- Windows MSVC targets require Microsoft Visual Studio Build Tools with the Desktop development with C++ workload. Camp uses a loaded Developer Command Prompt when one is present, and otherwise tries to find Visual Studio Build Tools automatically. Set `CAMP_VCVARSALL` to the full path of `vcvarsall.bat` for a custom installation.
- macOS targets require Apple's command line developer tools, which provide Clang and the system SDK:

  ```bash
  xcode-select --install
  ```

- Linux targets require GCC and the usual C development files. On Debian/Ubuntu:

  ```bash
  sudo apt install build-essential
  ```

  The `gcc-linux-x86` target also needs 32-bit multilib support.

## Install

Unix:

```bash
curl -fsSL https://raw.githubusercontent.com/camplang-dev/camp/master/install.sh | sh
```

Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/camplang-dev/camp/master/install.ps1 | iex
```

The installers download the matching GitHub Release archive, verify its SHA-256
checksum, unpack it into a stable user install directory, and print PATH
instructions. Use `--add-to-path` on Unix or `-AddToPath` on Windows to update
PATH automatically.

Manual archives are available from GitHub Releases. Preview host tool
distributions are:

- `win-x64`
- `win-x86`
- `linux-x64`
- `osx-x64`
- `osx-arm64`

Linux x86 is supported as a generated-code target, not as a preview host tool
distribution.

## Build From Source

To build Camp itself, install the .NET 10 SDK.

After installing the prerequisites, build the solution:

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

Native compilation uses the C toolchain for the selected Camp target.

## Publish Release Tools

Release-style tool binaries are produced with `src/publish-tools.proj`:

```bash
dotnet msbuild src/publish-tools.proj -p:RuntimeIdentifier=osx-x64
```

The published tools are written to `bin/publish/<rid>/`. Release archives are
created with:

```bash
local/package-release.sh --version v0.2.0-preview.1 --rid osx-x64
```

When installed outside the source tree, Camp expects its installation root to
contain `bin`, `lib`, `targets`, and `cache`. Set `CAMP_HOME` to override
automatic installation-root discovery.

## Documentation

- Language guide: `docs/language/`
- Compiler guide: `docs/compiler/`
- Compiler development guide: `docs/compiler-development-guide.md`
- Documentation contributor guide: `docs/documentation-contributor-guide.md`

## License

Camp is licensed under the MIT License. See `LICENSE`.

Programs compiled with Camp are not considered derivative works of Camp merely because they were compiled with Camp, linked with the Camp standard library/runtime, or contain compiler-generated support code.

The Camp name, logo, domains, and release branding are project identity assets. See `TRADEMARKS.md`.
