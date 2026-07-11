# Dumps, Diagnostics, And Introspection

Compiler dumps are for seeing what the compiler understood at specific stages.
They are not substitutes for the language reference, but they are the best way
to ground documentation, tests, and compiler changes in current behavior.

## Dump Command

The command form is:

```sh
campc dump <kind> [pattern.camp...] [options]
```

Valid kinds are `tokens`, `cst`, `ast`, `declarations`, `lowering`, and
`metadata`. Dump commands accept analysis options such as `--include`,
`--target`, `--profile`, `--variant`, `--define`, `--use`, `--use-source`,
`--project-reference`, `--metadata`, `--nostdlib`, and `--xml` where applicable.
They do not accept build-only output options.

## Token Dumps

Token dumps tokenize the first root source file and print one token per line:

```sh
campc dump tokens src/literals.camp --nostdlib
```

Each line contains the escaped token text and token class. Token dumps are useful
when investigating:

- keyword recognition;
- string and character literal boundaries;
- comment/prelude interactions;
- punctuation or operator tokenization;
- unexpected parser recovery caused by lexical shape.

Token dumps do not run the parser or semantic analysis.

## CST Dumps

`cst` dumps parse the first root source file and serialize the concrete syntax
tree as XML:

```sh
campc dump cst src/declarations.camp --nostdlib
```

Use CST dumps when a grammar rule, parse recovery path, or source range is in
question. CST XML preserves syntax-node structure rather than semantic meaning.

## AST Dumps

`ast` dumps serialize the bindable syntax-level tree as XML after parsing and
early binding:

```sh
campc dump ast src/declarations.camp --nostdlib
```

This stage is useful for checking the parser-to-bindable-node builder:

- source declarations are represented as the expected bindable node type;
- doc comments and attributes are attached to the intended declaration;
- type syntax is preserved before semantic resolution;
- preprocessor-active source has the expected shape.

AST dumps do not imply that semantic analysis accepted the program.

## Declaration Dumps

`declarations` runs declaration expansion and semantic declaration analysis,
then prints the analyzed declaration surface owned by the first root source
file:

```sh
campc dump declarations src/library.camp --nostdlib
campc dump declarations src/library.camp --nostdlib --xml
```

The non-XML dump uses Camp-like syntax. XML is intended for golden tests and
structural inspection. Declaration dumps are useful for:

- generated declarations and synthetic members;
- resolved signatures;
- export/public surfaces;
- interface conformance declarations;
- callable ascription;
- `constof`, `this`, `classtype`, and target-spec spelling.

Declaration dumps stop before body lowering. A declaration can dump correctly
while a body still fails analysis.

## Lowering Dumps

`lowering` runs the full lowering pipeline and prints the lowered Camp-like
output for declarations owned by the first root source file:

```sh
campc dump lowering src/library.camp --nostdlib
campc dump lowering src/library.camp --nostdlib --xml
```

Use lowering dumps for questions about:

- expanded parameter and return components;
- generated interface vtables and vtable accessors;
- async completion callbacks and await lowering;
- delegate, once, and lambda context shapes;
- constructor/destructor helper calls;
- generic capability threading;
- default argument insertion and thunking;
- rewritten member calls, property calls, and casts.

Lowering dumps are not C output. They are a compiler-maintainer view of the
lowered Camp model that the C emitter consumes.

## Metadata Dumps

`metadata` emits metadata JSON to standard output:

```sh
campc dump metadata src/library.camp --metadata export
campc dump metadata src/library.camp --metadata all
```

If no `--metadata` view is provided, the export view is used. Metadata dumps run
the same serializer used for build artifacts, but do not write an artifact file.
See [Metadata JSON](06-metadata-json.md).

## XML Output

`--xml` is valid only with `declarations` and `lowering`:

```sh
campc dump declarations src/library.camp --xml
campc dump lowering src/library.camp --xml
```

The driver reports a diagnostic if XML is requested with token, CST, AST, or
metadata output.

## API Inspection

The compiler driver has an API-inspection mode used by the test runner and
tooling. It serializes the Camp API header surface that downstream Camp
compilations consume. This is distinct from metadata JSON and from C API
headers.

Use API inspection when validating:

- `export` filtering;
- generated class extern surfaces;
- interface accessor exposure;
- generated constructor/destructor API declarations;
- omitted implementation details in exported virtual/interface surfaces.

The public `campc` command-line workflow exposes most inspection through
`build --artifact ...` generated files and `dump` modes.

## Diagnostic Format

Command-line diagnostics use:

```text
path/to/source.camp(line,column): severity: message
```

When no source range is available, the compiler reports a fallback location and
marks it as lacking a line/column. Error severity blocks successful compilation.
Warning severity reports accepted behavior that remains noteworthy.

The same range data feeds the language server. A range change can affect editor
tests even when the command-line message text is unchanged.

## Diagnostic Sources

Diagnostics can come from:

- command-line parsing and build-option merging;
- package and project-reference resolution;
- target loading and variant selection;
- preprocessing;
- tokenization and parsing;
- bindable-node building;
- declaration expansion and semantic analysis;
- body analysis, conversion checking, lifetime checking, and lowering;
- metadata serialization;
- C emission;
- native build commands.

When adding diagnostics, keep the range tied to the user's source construct
whenever possible. Generated-node diagnostics should map back to the source
declaration or expression that caused generation.

## Golden Tests

Golden tests compare exact output text. Dump modes are used for token, syntax,
declaration, lowering, metadata, diagnostic, C emission, API-surface, and
runtime-output coverage.

When using dumps to update docs or investigate behavior:

1. Prefer the narrowest source file that exercises the rule.
2. Use `--nostdlib` when the standard library is irrelevant.
3. Select the target and variant explicitly when target-specific behavior is in
   question.
4. Compare declaration, lowering, generated C, and metadata outputs only when
   each layer is relevant.

Do not treat one dump layer as proof of another layer's behavior. A declaration
dump can be correct while C emission still has a target-specific issue.

## Smoke Tests For Documentation Work

When documentation semantics are uncertain, use small smoke tests rather than
guessing. A useful smoke test should:

- live under `tmp/` or another scratch location;
- use clear domain names in source;
- run `campc dump` or `campc build --artifact none` when native compilation is
  not needed;
- record confirmed documentation uncertainty in `docs/OutstandingIssues.md`;
- record confirmed compiler bugs in the appropriate `OutstandingBugs` file.

Do not add broad compilation test runs merely to write prose. Use focused smoke
tests to answer focused semantic questions.
