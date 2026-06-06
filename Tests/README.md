# Camp Compiler Tests

Run the golden integration suite from `/src` with:

```sh
dotnet test camplang.sln
```

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
