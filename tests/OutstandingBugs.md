# Outstanding Bugs

Next bug number: BUG-084.

## BUG-082: `campc cover` mishandles layered package source roots and output directories

Status: open

Observed while running coverage for a layered package build where the response file includes source files from sibling package directories.

For example, a package build file may include:

```text
../package-core/src/*.camp
../package-read/src/*.camp
src/*.camp
tests/*.camp
```

`campc test @package-write.campbuild` works correctly, but `campc cover @package-write.campbuild` fails unless an explicit shared source root is supplied:

```text
Source file '<project-root>/src/package-core/src/core.camp' is outside every --sourcefile-root.
```

This appears inconsistent with the documented behavior in `dev/docs/compiler/01-campc-command-line.md` and `dev/docs/proposals/accepted/008-first-class-testing-and-coverage.md`, where `test` and `cover` use the same source pattern/build-file model, and build-file source roots should participate in source-capture paths.

Workaround:

```sh
campc cover @package-write.campbuild \
  --sourcefile-root <project-root>/src \
  --out-dir <project-root>/src/package-write/bin/coverage-write \
  --coverage-output-dir <project-root>/src/package-write/bin/coverage-write
```

A second issue appears when multiple layered packages are covered without explicit `--out-dir` / `--coverage-output-dir`: derived artifact paths collapse under an unexpected dependency source directory such as `package-core/src/bin/...`, causing different package coverage runs to overwrite each other. In one run this also produced a stale-link failure where the generated harness referenced tests from another package.

Impact:

- Coverage is usable, but each layered package coverage run currently needs explicit `--sourcefile-root`, `--out-dir`, and `--coverage-output-dir`.
- No source workaround is required beyond using those command-line flags in coverage runs.

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
