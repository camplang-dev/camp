# Camp Compiler Tests

Run the golden integration suite with:

```sh
dotnet test
```

Coverage can be collected with:

```sh
dotnet test --collect:"XPlat Code Coverage"
```

Golden cases live in `Tests/Cases`. Each `.camp` input has a committed
`.expected.*` sibling. Test runs write `.actual.*` files; passing tests delete
them, and failing tests keep them for inspection.

To approve an intentional output change, manually copy or merge the `.actual.*`
content into the corresponding `.expected.*` file. Missing expected baselines
intentionally fail after creating an empty expected file.
