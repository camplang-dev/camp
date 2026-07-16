# Compiler Supplement

This supplement documents the Camp compiler command line, project/build model,
package system, target metadata, output layout, metadata JSON, dump modes,
language-server integration, debug adapter integration, and standard-library
build integration.

It is for Camp users who build projects and for tool authors who consume
compiler artifacts. Compiler-internal semantic lowering rules live in
[Semantic Supplements](../semantics/index.md). Compiler development process
guidance lives in the compiler development guide and project README files.

## 1. [`campc` Command Line](01-campc-command-line.md)

- Command Model
- `build`
- `run`
- `dump`
- `restore`
- `pkg`
- `help`
- Build Options
- Output And Status Lines
- Diagnostics
- Standard Input

## 2. [Build Files And Pragmas](02-build-files-and-pragmas.md)

- `.campbuild` Files
- Response File Tokenization
- Source Patterns
- Include Patterns
- `#build` Pragmas
- Global, Local, And Command-Line Precedence
- Conditional Symbols
- `#within` Directives
- Project References In Build Files
- Recommended Build File Shape

## 3. [Package System](03-package-system.md)

- Package Specs
- Version Resolution
- Source Root Layout
- Package Sources
- Installed Package Roots
- Live Source Packages
- Dependency Link Kinds
- Standard Library Package
- Restore
- Package Commands
- Project References Versus Packages
- Transitive Native References
- Cache Validity

## 4. [Targets And Native Builds](04-targets-and-native-builds.md)

- Target Catalog
- Base Targets And Merging
- Target Sections
- Variants
- Defines
- Primitive C Types
- Call Specs
- Type Specs
- Natural Integers And Pointer Widths
- Conversion Policy Tables
- Toolchains
- Profiles
- Native Build Templates
- Artifacts
- Frameworks
- Windows Subsystem Builds
- C Emitter Capabilities

## 5. [Artifacts, Cache, And Output Layout](05-artifacts-cache-and-output-layout.md)

- Output Roots
- Artifact Directory Names
- Generated C Files
- Build Intermediates
- Executable Artifacts
- Static Libraries
- Shared Libraries
- Metadata Artifacts
- Package Artifact Cache
- Project Reference Output
- Project Reference Freshness Inputs
- Native Reference Resolution
- Runtime File Copying
- Cleaning

## 6. [Metadata JSON](06-metadata-json.md)

- Emitting Metadata
- Visibility Modes
- File Naming
- Top-Level JSON Shape
- Metadata IDs
- Common Declaration Fields
- Declaration Kinds
- Type Declarations
- Enum Values And Inline Constants
- Callable Newtypes
- Function And Method Metadata
- Parameters And Generic Parameters
- Type Strings
- Metadata Attributes
- Aliases
- Stubs
- Consumer Guidance

## 7. [Dumps, Diagnostics, And Introspection](07-dumps-diagnostics-and-introspection.md)

- Dump Command
- Token Dumps
- CST Dumps
- AST Dumps
- Declaration Dumps
- Lowering Dumps
- Metadata Dumps
- XML Output
- API Inspection
- Diagnostic Format
- Diagnostic Sources
- Golden Tests
- Smoke Tests For Documentation Work

## 8. [Language Server And Editor Tooling](08-language-server-and-editor-tooling.md)

- Server Process
- Project Discovery
- Project Loading
- Source Overlays
- Diagnostics
- Hover
- Go To Definition And References
- Completion
- Signature Help
- Document And Workspace Symbols
- Standard Library And Packages
- LSP Range Mapping
- Backlog Ownership

## 9. [Standard Library Build Integration](09-standard-library-build-integration.md)

- Source Layout
- Default Inclusion
- `--nostdlib`
- `lib/global.camp`
- API Preparation
- Native Helper Sources
- Native Package Artifact
- Metadata Filtering
- Target Interaction
- Package Cache Inputs
- Runtime Tests And Scratch Cache
- Documentation Boundary

## 10. [Debug Adapter And VS Code Debugging](10-debug-adapter-and-vscode-debugging.md)

- Build Requirements
- Debug Artifacts
- Debug Adapter
- macOS LLDB MVP
- VS Code Integration
- Known V1 Limits
- Testing Guidance
