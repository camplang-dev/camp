# Outstanding Bugs

Next bug number: BUG-084.

## BUG-083: Diagnostic should explain that unnamed `thrown` parameters are implicitly named `error`

Status: open

Observed with a test that has an unnamed thrown result and a local named `error`.

A test with an unnamed `thrown Assertion*` result and a local variable named `error` produced a name-collision failure that looked like a generated-C bug:

```camp
@test void repro(thrown Assertion*)
{
	IoError error = default;
}
```

This behavior is intentional at the language level: an unnamed `thrown` parameter has the implicit name `error`, and that name is part of the function body surface. A local variable named `error` would shadow the implicit thrown parameter and make it inaccessible, so it should be rejected.

The issue is diagnostic quality. The error should explain that `thrown Assertion*` introduces an implicit parameter named `error`, and that the local declaration conflicts with that implicit parameter. It should not feel like an accidental native-code redefinition.

Expected behavior:

- The compiler should report a source-level diagnostic before native C compilation.
- The diagnostic should identify the implicit thrown parameter name `error`.
- The diagnostic should suggest renaming the local or explicitly naming the thrown parameter if that is the intended API shape.

Workaround:

- Use a non-conflicting local name such as `ioError`, or explicitly name the thrown parameter and avoid colliding with it.

Impact:

- Affected tests or functions should use a local name such as `ioError` instead of `error` until the diagnostic is improved.
