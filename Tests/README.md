# Camp Compiler Tests

Run the golden integration suite from `/src` with:

```sh
dotnet test camplang.sln
```

Coverage can be collected with:

```sh
dotnet test camplang.sln --collect:"XPlat Code Coverage"
```

Golden test cases live under this directory. Each `.camp` file has a committed
`.expected.*` sibling baseline. Test runs always write a `.actual.*` file first.
If the expected file is missing, the test creates an empty expected file, keeps
the actual file, and fails.

When a compiler change intentionally changes output, inspect the `.actual.*` file
and manually copy or merge its content into the matching `.expected.*` file. There
is no automatic bless/update-baselines mode.
