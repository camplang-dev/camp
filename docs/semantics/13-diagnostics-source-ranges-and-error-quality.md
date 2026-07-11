# Diagnostics, Source Ranges, And Error Quality

## Diagnostic Severity

Diagnostics may be errors or warnings. Errors block successful compilation.
Warnings report accepted but risky or target-sensitive behavior.

## Stable Codes

Stable diagnostic codes should be used where tests, tooling, or users need to
recognize a diagnostic independent of message text.

## Parser Diagnostics

Parser diagnostics should be local to the syntax construct that failed and
should recover enough to report additional useful errors.

## Analysis Diagnostics

Analysis diagnostics come from binding, declaration validation, body analysis,
conversion classification, lifetime analysis, lowering validation, target
checks, and metadata validation.

## Source Ranges

Ranges should be tight enough for LSP highlighting and broad enough for users
to understand the failing construct. Generated-node diagnostics should map back
to the source expression or declaration that caused generation.

## Warnings

Warnings should use diagnostic severity rather than message wording alone.
Warning paths must remain testable and should not silently become errors or
accepted behavior.

## Golden Diagnostic Tests

Golden diagnostics live under `tests/Diagnostics`. Add focused cases for new
diagnostics and update expected files manually after inspecting actual output.

## LSP Diagnostic Mapping

LSP diagnostics consume compiler ranges and severities. Changes to diagnostic
ranges can affect editor tests even when command-line text is unchanged.

## Error Message Style

Messages should name the rejected construct, the expected rule, and the action
the user can take when that action is clear. Avoid vague messages that require
reading compiler source to understand the fix.
