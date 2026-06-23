# Camp Compiler Tests

Run the golden integration suite from `/src` with:

```sh
dotnet test camplang.sln
```

## Agent Test Workflow

### Commit Gate

Use targeted tests while fixing a bug or implementing a feature, but do not
commit until the full non-skipped test suite has passed at least once after the
final change for that commit. Platform- or environment-skipped tests, such as
MSVC-only tests on non-Windows machines, are acceptable skips. Failing tests,
unexpected skips, and leftover `.actual.*` files must be resolved before the
commit.

When the test project is already built, prefer `dotnet vstest` over
`dotnet test` for targeted and repeated runs:

```sh
dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll
```

`dotnet vstest` runs the already-built test assembly directly and avoids most
MSBuild project/solution evaluation. This matters for sandboxed agents because
MSBuild may create local worker-node IPC/named pipes that the sandbox rejects
with `SocketException (13): Permission denied`.

Use `dotnet build src/camplang.sln` when source changes need to be compiled,
then use `dotnet vstest` for repeated verification. Reserve `dotnet test` for
full local validation, restore/build validation, coverage collection, or cases
where MSBuild behavior itself is what you are testing.

For a fast compiler-feedback pass from the repository root, run:

```sh
dotnet msbuild src/test-fast.proj
```

This runs golden tests only and skips `StdRun`, command-line process tests, and
MSVC smoke tests. If the solution is already built, use:

```sh
dotnet msbuild src/test-fast.proj -p:NoBuild=true
```

Sandboxed agents should prefer the equivalent direct `vstest` form after a
build:

```sh
CAMP_TEST_KIND=Ast,Declarations,LoweringXml,Lowering,Diagnostics,CEmit,CCompile,Api,Metadata,Std dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll --TestCaseFilter:FullyQualifiedName~GoldenFileTests
```

During compiler work, prefer targeted runs. Golden discovery supports these
environment variables:

```sh
CAMP_TEST_KIND=Metadata dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll --TestCaseFilter:FullyQualifiedName~GoldenFileTests
CAMP_TEST_KIND=CCompile CAMP_TEST_CASE=generic_array dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll --TestCaseFilter:FullyQualifiedName~GoldenFileTests
CAMP_TEST_CASE=generic_self_link dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll --TestCaseFilter:FullyQualifiedName~GoldenFileTests
```

`CAMP_TEST_KIND` matches top-level test folders such as `Metadata`, `CCompile`,
`Lowering`, or `StdRun`. `CAMP_TEST_CASE` matches one or more comma-separated
substrings of the repository-relative case path without the `.camp` extension.
When either variable is set, command-line process tests skip themselves so the
targeted golden run stays focused. Use a full suite only for broad compiler
changes or before larger commits.

`StdRun` tests share a standard-library package artifact cache under
`tmp/golden-stdrun-packages` so the standard library is not rebuilt for every
runtime case. If the std cache appears stale, deleting that directory forces a
clean rebuild.

Run tests and generate a console + HTML coverage report with:

```sh
dotnet msbuild coverage.proj
```

To also open the HTML report after it is generated:

```sh
dotnet msbuild coverage.proj -p:Open=true
```

The report is written to `tmp/coverage-report/Summary.txt` and
`tmp/coverage-report/index.html` at the repo root.

Golden test cases live under this directory. `Ast`, `Lowering`, `Diagnostics`,
and `CEmit` cases run with `--nostdlib` so language-feature tests stay
standalone. `Std` cases opt into the standard library and are used for behavior
that depends on bundled std declarations. Each `.camp` file has a committed
`.expected.*` sibling baseline. Test runs always write a `.actual.*` file first.
If the expected file is missing, the test creates an empty expected file, keeps
the actual file, and fails.

When a compiler change intentionally changes output, inspect the `.actual.*` file
and manually copy or merge its content into the matching `.expected.*` file. There
is no automatic bless/update-baselines mode.
