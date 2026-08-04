# Diagnostics, Source Ranges, And Error Quality

This supplement describes diagnostic policy for parser, analyzer, lowering,
emitter, driver, and LSP-facing errors. The goal is not just nicer messages:
diagnostics are a semantic surface of the compiler. Tests, editor tooling, LLM
agents, and users all depend on them being precise and stable.

## Diagnostic Model

Diagnostics may be errors, warnings, or informational messages. Errors block
successful compilation. Warnings report accepted but risky or target-sensitive
behavior. Informational diagnostics should be rare and used only where tooling
has an explicit consumer.

The core diagnostic record carries:

- optional token range;
- message;
- optional stable code;
- severity.

Compiler code should not encode severity only in message text. Use the severity
field so CLI, LSP, tests, and future structured output agree.

## Stable Codes

Stable diagnostic codes should be used where tests, tooling, or users need to
recognize a diagnostic independent of message text.

Codes are most valuable for:

- syntax or semantic rules that external tools will suppress or explain;
- warnings whose wording may evolve;
- common educational diagnostics;
- target-sensitive diagnostics;
- diagnostics surfaced in LSP quick fixes.

Many existing diagnostics are message-only. When adding a code, keep it stable,
document what rule it represents, and avoid reusing it for a different rule.

## Parser Diagnostics

Parser diagnostics should be local to the syntax construct that failed and
should recover enough to report additional useful errors.

Parser messages should:

- point at the unexpected token or missing delimiter location;
- mention the expected construct;
- preserve token ranges for downstream bindable-node construction;
- avoid swallowing tokens that the analyzer could still use for later
  diagnostics;
- keep doc-comment and attribute attachment recoverable where possible.

Parser diagnostics should not try to perform semantic validation. If a construct
is syntactically valid but semantically invalid, let the analyzer report the
semantic rule with richer context.

## Analysis Diagnostics

Analysis diagnostics come from binding, declaration validation, body analysis,
conversion classification, lifetime analysis, lowering validation, target
checks, and metadata validation.

Each pass owns certain rules:

- declaration collection owns duplicate declarations and scope setup;
- type binding owns unknown types, invalid specifiers, lifetime/const anchors,
  and capability parameter binding;
- declaration validation owns inheritance, interface conformance, virtual
  rules, extern rules, inline constants, and callable ascription;
- body analysis owns expression typing, overload resolution, call arguments,
  conversions, flow, returns, `await`, `yield`, and `delete`;
- lowering validation owns generated-form constraints and unsupported lowering
  surfaces;
- target validation owns target-specific specs and unsupported primitives;
- metadata validation owns doc-comment targets and metadata-only constraints.

Avoid reporting the same root problem from multiple passes. When a pass sees
`#ERROR`, `#MISSING`, or another marker produced by an earlier diagnostic, it
should usually suppress follow-on noise unless the later message gives useful
independent information.

## Source Ranges

Ranges should be tight enough for LSP highlighting and broad enough for users
to understand the failing construct. Generated-node diagnostics should map back
to the source expression or declaration that caused generation.

Common range choices:

- declaration name for duplicate/invalid declaration identity;
- keyword for invalid construct category;
- attribute name for invalid attribute placement;
- type annotation for invalid type, lifetime, constness, or target specifier;
- argument expression for call/conversion failures;
- operator token for invalid operator use;
- member name for invalid member lookup or interface slot mismatch;
- whole expression only when no smaller useful range exists.

Generated declarations and lowered expressions should retain `SourceSyntax` or
provenance. If a generated helper fails validation, map the diagnostic to the
source declaration or expression that required the helper.

## Range Helpers

Range helper methods centralize syntax-to-range mapping. Feature code should
prefer helpers such as declaration-name, parameter-name, label-name,
generic-parameter-name, type, expression, and postfix ranges rather than
manually reaching into syntax nodes.

Adding a new syntax node should include:

- parser ranges on relevant tokens;
- syntax-range helper support;
- bindable-node `SourceSyntax` propagation;
- LSP range mapping tests if editor behavior changes.

## Warnings

Warnings should use diagnostic severity rather than message wording alone.
Warning paths must remain testable and should not silently become errors or
accepted behavior.

Good warning cases include:

- target-sensitive casts that are accepted but risky;
- unsafe cast forms where the language allows the operation but wants it
  visible;
- compatibility behavior that is valid only under selected target policy.

Warnings should not be used for invalid programs. If generated code would be
incorrect, report an error.

## Error Message Style

Messages should name the rejected construct, the expected rule, and the action
the user can take when that action is clear. Avoid vague messages that require
reading compiler source to understand the fix.

Good diagnostics answer:

- What failed?
- What rule was expected?
- Which source declaration/expression is involved?
- What can the author change?

Examples of useful wording patterns:

- "Cannot index `T[]` because element stride for erased type parameter `T` is
  unavailable. Add `sizeof(T)` to the parameter list or use a representation
  constraint."
- "Method `read` does not implement interface member `IReadable.read(...)`
  because it is missing an explicit interface marker."
- "Async definitions that can suspend require a resumer; add `@awaitwith` to an
  ordinary parameter or define `resumeAsync` on the receiver."

Do not make diagnostics depend on test fixture names, host paths, or private
machine details.

## Prepared Call Diagnostics

Prepared-result diagnostics are divided by owning pass and should retain the
original source construct:

- declaration validation reports an invalid prep type/return match, duplicate
  prep slot, incompatible modifier, prep in `once`, required ordinary or `out`
  parameter after prep, and overload selector after prep at the responsible
  parameter or declaration;
- body analysis reports wrong explicit prep arguments, unsatisfied non-prep
  slots, invalid scoped lifetime, missing allocation context, and invalid
  `(new)` direct-result/owner-losing chains at the written argument, call, or
  `(new)` token;
- property binding recognizes a prep accessor candidate and reports that
  explicit method-call syntax is required at the property access;
- interpolation reports missing, ambiguous, wrong-buffer, wrong-return,
  unsupported-thrown, receiver, and lifetime failures at the hole or selected
  formatter.

Generated temporaries and generated sizing/writing calls are never the primary
diagnostic ranges. No diagnostic recommends the removed `prep` expression
prefix. Contextual uses of `prep` that do not form a complete modifier parameter
are parsed and diagnosed as ordinary identifiers or types.

## Multi-Diagnostic Situations

Some source mistakes correctly produce more than one diagnostic. For example,
an invalid call may have both a conversion error and a `constof` anchor error.
When multiple diagnostics are emitted:

- each message should explain an independent rule;
- order should be deterministic;
- follow-on diagnostics should be suppressed if they only restate an unknown
  type or failed lookup;
- tests should lock the intended set.

If a new diagnostic makes a previous follow-on message unnecessary, update the
expected file after inspecting the actual output.

## Driver And Emitter Diagnostics

Driver diagnostics cover command-line options, target selection, package
resolution, build-file pragmas, file I/O, metadata output, and native build
execution. They may not have a source range. Include the file path, target name,
package spec, command, or option name that failed.

Emitter diagnostics should include the source range when a lowered node still
has source provenance. If emission fails because an unresolved marker reached
C emission, describe the node and its source location or serialized snippet.

Native command failures should report command, exit code, standard output, and
standard error in a concise form suitable for CLI use.

## Golden Diagnostic Tests

Golden diagnostics live under `tests/Diagnostics`. Add focused cases for new
diagnostics and update expected files manually after inspecting actual output.

Diagnostic tests should:

- include the smallest source that exercises the rule;
- avoid unrelated failures;
- cover both message and range through expected line/column output;
- include warnings where warning behavior matters;
- avoid relying on absolute machine paths;
- update `.expected.txt` only after reviewing `.actual` output.

Do not weaken a diagnostic test by making the source ambiguous. A dense fixture
can cover related errors, but distinct rule families usually deserve separate
fixtures.

## LSP Diagnostic Mapping

LSP diagnostics consume compiler ranges and severities. Changes to diagnostic
ranges can affect editor tests even when command-line text is unchanged.

The language service should:

- recompute diagnostics from the same parser/analyzer pipeline as CLI builds;
- map token ranges to LSP ranges using zero-based LSP conventions;
- preserve severity;
- debounce/reload project state without losing diagnostics for open overlays;
- avoid reporting diagnostics from stale project snapshots.

When changing ranges, inspect both CLI diagnostics and LSP tests. A range that
looks fine in text output may highlight the wrong token in an editor.

Source-capture default diagnostics should follow the same rule. Invalid
`caller(...)` or `sourceof(...)` syntax in a declaration should highlight the
intrinsic call, selector, or argument that is wrong. A call that omits a
`caller(propertyname)` default outside a property accessor body should highlight
the call expression and name the parameter whose default was not supplied.
Generated helper functions, interface thunks, and callable adapters must not
replace the source range used for source-capture diagnostics or values.

## Test And Coverage Diagnostics

Invalid source placement of `@test`, `@testonly`, and `@skip` is a normal
compiler diagnostic. The diagnostic should point at the attribute name when the
attribute itself is invalid, or at the declaration name when the declaration
shape is the clearest source of the problem.

The built-in runner signature is not a compiler-stopping rule. A top-level
`@test` function with the wrong return type, parameters, thrown slot, generic
parameters, extern body, async modifier, iterator shape, non-pointer thrown
type, or thrown pointer type without `message`, `sourcefile`, and `sourceline`
fields is discovered and reported as an invalid test result or non-blocking
tooling diagnostic. This lets valid tests in the same module continue to run.

Production-to-test-only dependencies are compiler errors in every command mode.
The diagnostic should point at the production declaration that depends on the
test-only declaration and name the test-only dependency.

Assertion failures are runtime test results, not compiler diagnostics. When a
tool imports `camp.test-results` JSON, it may surface assertion and invalid-test
failures as editor diagnostics using the captured `sourcefile` and `sourceline`
from the result. Import failures for test results, coverage results, or coverage
map CSV should be reported as tooling diagnostics that name the unreadable or
malformed artifact.

Coverage decorations are derived from coverage artifacts, not compiler
diagnostics. A line with an executable-line counter and a positive count is
covered; a line with an executable-line counter and zero count is uncovered; a
line without an executable-line counter receives no coverage diagnostic or
decoration.

## Outstanding Bugs And Documentation Issues

When documentation work uncovers a certain compiler bug, log it in
`OutstandingBugs` using the repository's bug numbering convention. When the
behavior is uncertain after reading docs, source, and smoke tests, record it in
`docs/OutstandingIssues.md` instead of guessing.

Do not turn uncertainty into canonical documentation. If a semantic point cannot
be determined, document the uncertainty explicitly in the issues file and keep
the main docs limited to known behavior.

## Test Surface

Diagnostics changes should cover:

- parser recovery;
- invalid declaration/type/member surfaces;
- static class misuse in type, construction, inheritance, interface,
  lifecycle, receiver, and instance-member contexts;
- overload and call argument failures;
- conversion errors and warnings;
- lifetime and `constof` mismatches;
- generic capability failures;
- interface and virtual dispatch validation;
- async/resumer validation;
- target-specific errors;
- metadata/doc-comment errors;
- LSP range/severity mapping.

## Implementation Anchors

Primary implementation points include:

- `CompilerDiagnostic.cs` for severity/code/message structure;
- `CampTokenizer.cs` and `CampParser.cs` for token ranges and parser errors;
- `BindableNodeAnalyzer.SyntaxRanges.cs` for range helpers;
- analyzer pass files for semantic diagnostics;
- `CCodeEmitter.cs`, `CompilerDriver.cs`, and native build code for driver and
  emitter diagnostics;
- language service tests for LSP mapping;
- fixtures under `tests/Diagnostics`.
