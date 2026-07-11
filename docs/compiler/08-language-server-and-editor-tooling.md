# Language Server And Editor Tooling

## `camp-lsp`

`camp-lsp` provides Language Server Protocol support for Camp editors. It runs
over stdio and analyzes source files using the same compiler project loader and
language service surfaces as command-line tooling.

## Project Discovery

The language server finds the nearest `.campbuild` file by walking from the
source file or directory toward the filesystem root. If a directory contains a
preferred build file named after the directory, that file wins. If exactly one
`.campbuild` exists in a directory, it is used.

## Editor Setup

Editor setup is project-specific. The `src/camp-lsp` project README contains
Sublime Text and Micro setup examples and should remain the project-local source
for basic editor usage.

## Diagnostics

The language server publishes diagnostics from parsing and semantic analysis.
It uses project loading to include referenced source, includes, package API
headers, and project-reference sources when artifacts are not available.

## Hover

Hover uses symbol lookup and declaration information to describe the item under
the cursor.

## Go To Definition

Definition mapping uses syntax ranges and symbol analysis. It supports common
declaration, member, property, and interface surfaces covered by language
service tests.

## Completion

Completion includes cheap syntactic fallbacks and semantic completion where a
fresh enough analysis snapshot is available.

## Limitations And Backlog

The LSP backlog tracks features such as semantic tokens, broader diagnostics,
code actions, rename, formatting, incremental compilation, multi-root workspaces,
and debug/trace mode. Backlog details belong in the LSP project docs or tracker.
