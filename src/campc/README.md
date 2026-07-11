# campc

`campc` is the Camp command-line compiler.

## Setup

Build from the repository root:

```sh
dotnet build src/camplang.sln
```

The build copies the command-line runtime to the repository `bin/` directory.

## Usage

```sh
bin/campc build samples/main.camp
bin/campc run samples/main.camp
bin/campc dump lowering samples/main.camp
```

Command and option details are documented in
`docs/compiler/01-campc-command-line.md`.

## Testing

Command-line process tests live in `src/Camp.Compiler.TestRunner`. Golden
compiler behavior lives under `tests`. Documentation-only changes do not
require unit tests unless requested.

## Coding Instructions

Keep command parsing, response-file expansion, build pragmas, package commands,
and project-reference behavior close to `Program.cs` unless the logic belongs
in the compiler library. Confirmed compiler bugs should be logged in
`OutstandingBugs.md`.
