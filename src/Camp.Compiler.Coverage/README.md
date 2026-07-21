# Camp.Compiler.Coverage

`Camp.Compiler.Coverage` drives coverage reporting for the C# compiler test
suite. This is separate from Camp source coverage produced by `campc cover`.

## Setup

Build from the repository root:

```sh
dotnet build src/camplang.sln
```

## Usage

Generate coverage from the repository root:

```sh
dotnet msbuild src/coverage.proj
```

Open the HTML report after generation:

```sh
dotnet msbuild src/coverage.proj -p:Open=true
```

## Testing

Coverage generation runs tests and writes output under `tmp/coverage-report`.
Those files are scratch artifacts and should not be committed.

## Coding Instructions

Keep this project focused on report generation and integration with the shared
test workflow. Test semantics belong in `Camp.Compiler.TestRunner`.
