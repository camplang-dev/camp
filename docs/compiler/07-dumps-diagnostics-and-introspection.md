# Dumps, Diagnostics, And Introspection

## Dump Kinds

`campc dump` supports `tokens`, `cst`, `ast`, `declarations`, `lowering`, and
`metadata`.

## Tokens

Token dumps show lexical output after tokenization. They are useful for
debugging keywords, punctuation, literals, and trivia boundaries.

## CST

CST dumps show concrete syntax tree structure. Use them when parser behavior is
the subject of investigation.

## AST

AST dumps show bindable syntax-level structure after parsing and early binding.

## Declarations

Declaration dumps show analyzed declaration surface. With `--xml`, declarations
are emitted as XML for stable golden testing and inspection.

## Lowering

Lowering dumps show lowered Camp-like output after semantic analysis and
rewrites. With `--xml`, lowering can also be inspected structurally.

## Metadata

Metadata dumps emit metadata JSON. If no visibility is selected, the dump uses
the export view.

## XML Output

`--xml` is valid only with declaration and lowering dumps.

```sh
campc dump declarations source.camp --xml
campc dump lowering source.camp --xml
```

## API Inspection

API inspection serializes the exported API surface used by other compilations.
It is distinct from metadata JSON and lowered C output.

## Diagnostic Format

Diagnostics include source location where available, severity, and message. Some
diagnostics have stable codes. Diagnostics that affect LSP behavior should keep
source ranges tight and predictable.

## Using Dumps In Tests

Golden tests compare emitted text against committed expected files. Dump modes
are used for parser, declaration, lowering, metadata, diagnostics, C emission,
and API-surface coverage.
