# Documentation Contributor Guide

This guide explains how Camp documentation is organized, who each document is
for, and how contributors should write and maintain it. It is for people
editing documentation, reviewing documentation changes, or updating docs while
changing the compiler.

The short version: write for the reader in front of you, keep the canonical
information in the right place, and do not let important semantics live only in
old proposals, old specs, comments, or tests.

## Documentation Map

The active documentation lives under `docs/`.

| Path | Audience | Purpose |
|---|---|---|
| `docs/language/` | Camp programmers | The readable language guide and user-facing reference. |
| `docs/semantics/` | Compiler writers | Normative compiler semantics, lowering, diagnostics, metadata, and ABI details. |
| `docs/compiler/` | Toolchain users | Command-line compiler, builds, packages, targets, metadata files, dumps, and editor tooling. |
| `docs/compiler-development-guide.md` | Compiler contributors | Process for working in the compiler repo and C# solution. |
| `docs/camp-llm-coding-guide.md` | LLM agents writing Camp | Dense, token-conscious Camp coding guidance. |
| `docs/proposals/` | Design reviewers and maintainers | Historical accepted proposals, pending proposals, and rejected proposals. |

Do not save private setup notes, machine-specific context, personal environment
details, copied historical source material, or other noncanonical working notes
to the repo.

## Source Of Truth

For implemented language behavior, the canonical public surface should be
covered by the combination of:

- the language guide in `docs/language/`;
- the semantic supplements in `docs/semantics/`;
- the current compiler implementation and focused smoke tests when behavior is
  unclear.

Accepted proposals are historical. Their design should be integrated into the
language guide and semantic supplements when accepted. After that, the docs are
canonical and the accepted proposal remains a record of why the decision was
made.

When the compiler, old specs, accepted proposals, and active docs disagree,
do not smooth over the conflict. Investigate it. If the compiler is certainly
wrong, log a bug in the right outstanding-bugs file. If the documentation
semantics cannot be determined from docs, source, and smoke tests, record the
uncertainty in `docs/OutstandingIssues.md`.

## Intended Audiences

### Camp Programmers

Camp programmers are the audience for `docs/language/`. Assume they already
know one or more C-family languages and want to understand what is familiar,
what is different, and how to write correct Camp code.

They are not compiler contributors. They do not want parser trivia, lowering
algorithms, documentation-editor instructions, or implementation bookkeeping.
They do need enough ABI explanation to design APIs honestly.

### Compiler Writers

Compiler writers are the audience for `docs/semantics/`. They need exact rules:
binding order, type compatibility, vtable shapes, lifetime facts, lowering
contracts, diagnostics, metadata structure, and target-sensitive behavior.

This material may be dry and precise. It should be clear enough that someone
could write a compatible Camp compiler from the language guide plus the
semantic supplements.

### Toolchain Users

Toolchain users are the audience for `docs/compiler/`. They need practical
answers: how to invoke `campc`, how build pragmas work, how packages are
resolved, how target files affect builds, where artifacts go, and how metadata
and dumps are produced.

### Compiler Contributors

Compiler contributors use `docs/compiler-development-guide.md`, the README files
in each C# project folder, and source comments near complicated code.

Local coding instructions that apply to one source file or helper should usually
be source comments, close to the code. General process, cross-project rules,
testing expectations, and documentation policy belong in docs.

Each C# project folder should keep its own README with basic usage, setup,
testing, and local coding notes, linking back to broader docs where needed.

### LLM Agents

LLM agents use `docs/camp-llm-coding-guide.md`. This guide should be compact,
high-density, and optimized for producing correct Camp code with limited
context. It may repeat the most important rules from the language guide and
semantics, but it should aggressively remove duplicated prose.

### Proposal Readers

Proposal readers are maintainers trying to understand design history. Proposals
are not the language reference. They explain why a change was accepted,
rejected, or still pending.

## Language Guide Standards

The language guide is both a readable guide and a return-to-it reference. Its
tone should be warm, direct, and human. It should not sound like a compiler dump
or an assembly manual.

Each topic should begin with a short introduction that helps the reader decide:

- what the topic covers;
- why it matters;
- what they will understand after reading it.

Subsections should build on each other. Prefer a narrative order that helps a
programmer start writing Camp:

- first small programs and declarations;
- then everyday types, functions, methods, lifetimes, errors, and ownership;
- then more advanced API features such as namespaces, enums, interfaces,
  generics, iterators, async, and interop;
- finally compact reference material for expressions, statements, operators,
  attributes, and metadata hints.

Do not put documentation-editor notes in the language guide. Avoid sections
like "how this guide is organized", "what counts as user-facing", or disclaimers
about examples. Readers are there to learn Camp.

### What Belongs In The Language Guide

Include information a programmer needs to write, read, debug, or design Camp
code:

- syntax and common source shapes;
- practical semantics;
- user-visible ABI consequences;
- short examples;
- design rationale when it helps a programmer choose the right feature;
- warnings about common mistakes.

Some ABI details are user-facing. For example, struct layout visibility, class
opacity, interface pointer lifetimes, async callback shape, iterator state
privacy, and exported enum symbols are all useful to API authors.

### What Does Not Belong In The Language Guide

Move compiler-writer details to `docs/semantics/`, including:

- parser/token/trivia details;
- exact lowering algorithms;
- vtable slot construction algorithms;
- metadata serializer internals;
- lifetime fact graph algorithms;
- conversion classifier internals;
- generated helper ordering when users do not need it;
- test fixture or implementation instructions.

If a detail helps a user understand what code means, keep it in the language
guide. If it only helps a compiler emit correct code, put it in semantics.

## Semantic Supplement Standards

The semantic supplements are normative for compiler behavior. They should be
precise, complete, and organized around compiler responsibilities.

A good semantic supplement usually includes:

- the source forms it covers;
- binding and validation rules;
- lowering and ABI shape;
- interaction with lifetimes, `constof`, generics, callables, async, or
  interfaces;
- metadata/API behavior;
- diagnostics;
- test surface;
- implementation anchors.

Use implementation anchors to point compiler writers toward important files, but
do not turn the supplement into a line-by-line code tour. Source comments are
better for local implementation details.

When active docs and old source material disagree, prefer the compiler only after
checking whether the mismatch is a bug. If it is a bug, document the intended
semantics and log the bug.

## Compiler Documentation Standards

The compiler docs are task-oriented. They should answer practical questions:

- What command do I run?
- What file format do I write?
- What output should I expect?
- What does this build/package/target/metadata concept mean?
- How does this affect downstream tools?

Avoid private absolute paths, machine-specific shell setup, local package cache
details, or personal environment notes. Do not save those notes to the repo.

Examples should use plausible project names and paths, not private user paths.

## LLM Coding Guide Standards

The LLM guide should be optimized for correctness per token.

Keep:

- high-risk rules;
- source patterns that prevent errors;
- compact examples;
- conventions for generated code;
- feature interactions that LLMs commonly miss.

Remove:

- long narrative introductions;
- duplicated examples already covered elsewhere;
- detailed compiler algorithms;
- broad standard-library API catalogs that will go stale.

The LLM guide may mention a small amount of standard library API when it helps
produce realistic code, but avoid making it a second standard library reference.

## Proposal Standards

Proposals live under:

- `docs/proposals/accepted/`;
- `docs/proposals/pending/`;
- `docs/proposals/rejected/`.

Do not add an index file to the proposals folder.

Proposal filenames should be numbered from oldest to newest within each folder:

```text
001-short-topic-name.md
002-next-topic.md
```

Every proposal should include:

- proposal date;
- last updated date;
- status.

Rejected proposals should also include a rejected date near the top. As a rule,
do not update rejected proposals after rejection except to fix broken formatting
or archival mistakes.

Pending proposals should stay aligned with compiler changes. If a compiler
change makes the proposal uncertain, note the uncertainty in the proposal and
list it as something that must be resolved before implementation.

Accepted proposals should not be updated as living docs. Once accepted, their
details belong in the language guide, semantic supplements, compiler docs, and
LLM guide as appropriate. Accepted proposals should state that the active docs
are canonical and that the proposal remains historical.

When writing about rejected or omitted features in active docs, avoid historical
phrasing like "Camp no longer supports X". Prefer present-tense language:

```text
Camp does not support X because Y.
```

## Examples

Examples should illuminate the prose, not overwhelm it.

Use short examples at important moments. A few lines of code can often explain a
rule better than a paragraph.

Follow these conventions:

- Sort declarations in call order: callers before callees when that helps the
  reader. Camp does not need C-style forward-declaration ordering.
- Avoid `export` unless the example is about program entry points, native/API
  visibility, or exported library design.
- Avoid placeholder names like `foo`, `bar`, `baz`, and related filler.
- Use meaningful domain names such as `Image`, `Buffer`, `ParserState`,
  `TextSink`, `WindowHandle`, or `TaskScheduler`.
- Use UPPER_SNAKE_CASE for enum values and inline constants.
- Prefer actual standard library API when showing standard library use.
- If an example invents an API, make that obvious with a comment or surrounding
  prose.
- Do not make examples artificially complete when an excerpt is clearer.
- Use C snippets when ABI representation is the point.

If a code example is not meant to compile, make sure it is still semantically
honest. Do not show impossible lifetimes, invalid generic fields, or fake
standard library calls without marking them as illustrative.

## Cross-References And Duplication

Prefer cross-references over repeating long explanations.

Repeat only enough context for the reader to continue. Phrases like "you have
already seen" are useful in the language guide when a later topic builds on an
earlier one.

Do not split a single semantic rule across several places without a clear
canonical home. For example:

- user-facing explanation belongs in the language guide;
- exact compiler rule belongs in semantics;
- command/tool behavior belongs in compiler docs;
- compact reminders belong in the LLM guide.

When a rule changes, update every affected surface intentionally.

## Markdown And File Conventions

Use plain Markdown without static-site front matter.

Use relative links between docs files:

```md
[Generics And Capabilities](language/13-generics-and-capabilities.md)
```

Prefer numbered filenames for ordered guides:

```text
01-topic-name.md
02-next-topic.md
```

Use fenced code blocks with language tags where useful:

````md
```camp
export int main()
{
	return 0;
}
```
````

Use tables when they make comparison easier. Do not use giant tables as a
substitute for explanation.

Keep headings descriptive and stable. External links, docs, and agents may rely
on them.

## Index Files

`docs/language/index.md`, `docs/semantics/index.md`, and
`docs/compiler/index.md` are navigational maps. Update them when you add,
remove, rename, or materially reorganize documents.

Indexes should list top-level documents and their major subsections. They should
not duplicate the full contents of each document.

The proposals folder should not have an index.

## Handling Bugs And Uncertainty

Use smoke tests when documentation depends on compiler behavior and reading the
source is not enough. A tiny file under `tmp/` is appropriate for this kind of
check.

When you are certain a compiler behavior is a bug:

- log analyzer, emitter, lowering, metadata, and language-service bugs in
  `tests/OutstandingBugs.md`;
- log command-line compiler bugs that are specifically about the `campc` CLI in
  `src/campc/OutstandingBugs.md`.

When the issue is documentation uncertainty rather than a confirmed compiler
bug, record it in `docs/OutstandingIssues.md`. Number issues consistently so
the team can resolve them later.

Do not write uncertain semantics as if they were canonical. Either resolve the
question, log it, or phrase the docs around the known rule.

## Updating Docs With Compiler Changes

When a compiler change affects user-visible behavior, update the docs in the
same change whenever possible.

Use this checklist:

- Does the language guide need a user-facing explanation?
- Does a semantic supplement need a compiler-writer rule?
- Does compiler tooling behavior need an update under `docs/compiler/`?
- Does the LLM guide need a compact warning or pattern?
- Does an accepted or pending proposal need a historical note?
- Do examples still use real or clearly invented APIs?
- Are indexes still accurate?
- Are bugs or documentation issues logged for unresolved mismatches?

Documentation-only changes usually do not require unit tests. Targeted smoke
tests are useful when they prevent documenting the wrong behavior.

## Writing Checklist

Before finishing a documentation change, check:

- The document is written for the correct audience.
- The tone matches the folder: warm and explanatory for `language`, precise and
  normative for `semantics`, practical for `compiler`, compact for the LLM
  guide.
- Important semantics are not stranded in old specs, proposals, comments, or
  tests.
- Examples are meaningful, accurate, and not misleading about the standard
  library.
- Cross-references point to the canonical home instead of duplicating long
  explanations.
- Machine-specific details are absent.
- Index files are updated.
- Known compiler bugs or documentation uncertainties are recorded.

The goal is not just to have many pages. The goal is that a programmer can learn
Camp, a compiler writer can implement Camp, and a contributor can change Camp
without losing the shape of the language.
