# Compiler Supplement

This supplement describes Camp compiler tooling, build configuration, package
resolution, target metadata, artifacts, metadata JSON, dump modes, editor
tooling, and standard-library build integration.

## 1. `campc` Command Line

- Command Overview
- `build`
- `run`
- `dump`
- `restore`
- `pkg`
- `help`
- Common Build Options
- Exit Codes And Diagnostics
- Response Files

## 2. Build Files And Pragmas

- `.campbuild` Files
- Bare Build File Expansion
- Response File Tokenization
- `#build` Pragmas
- Local And Global Pragmas
- Precedence Rules
- Source And Include Patterns
- Conditional Build Symbols

## 3. Package System

- Package Specs
- Version Specs
- Link Kinds
- Package Sources
- Global And Local Package Roots
- Installing And Uninstalling
- `--use`, `--use-source`, And `#build`
- Restore Behavior
- Project References

## 4. Targets And Native Builds

- Target Catalog
- Target Inheritance
- Variants
- Defines
- C Types
- Call Specs And Type Specs
- Natural Integer And Pointer Widths
- Toolchains
- Profiles
- Build Templates
- Frameworks
- Subsystems

## 5. Artifacts, Cache, And Output Layout

- Output Directories
- Artifact Directory Names
- Build Intermediates
- Static Libraries
- Shared Libraries
- Executables
- Runtime Files
- Link Files
- Package Cache Layout
- Project Reference Cache Checks
- Cleaning And Rebuilding

## 6. Metadata JSON

- Emitting Metadata
- Visibility Modes
- Top-Level JSON Shape
- Metadata IDs
- Declaration Objects
- Type Objects
- Function And Method Objects
- Inline Constants
- Aliases
- Attributes And Symbol Links
- Consumer Guidance

## 7. Dumps, Diagnostics, And Introspection

- Dump Kinds
- Tokens
- CST
- AST
- Declarations
- Lowering
- Metadata
- XML Output
- API Inspection
- Diagnostic Format
- Using Dumps In Tests

## 8. Language Server And Editor Tooling

- `camp-lsp`
- Project Discovery
- Editor Setup
- Diagnostics
- Hover
- Go To Definition
- Completion
- Limitations And Backlog

## 9. Standard Library Build Integration

- Default Standard Library Inclusion
- `--nostdlib`
- `lib/global.camp`
- Standard Library Package Build
- Native Helper Sources
- StdRun Package Cache
- Consuming Std Metadata
