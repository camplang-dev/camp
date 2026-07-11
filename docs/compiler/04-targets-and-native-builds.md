# Targets And Native Builds

## Target Catalog

Targets are loaded from `targets/**/*.ini`. Each target file has a `[target]`
section with a `name`, and optionally a `base`.

```ini
[target]
name=gcc-linux-x64
base=c99
```

The selected target controls primitive C spelling, call specs, type specs,
toolchain commands, profile flags, artifact extensions, and native build
templates.

## Target Inheritance

A target can inherit from a base target. The compiler resolves base chains,
merges sections, and reports duplicate or circular target definitions.

## Variants

Variant groups let a target select overlays such as a character-width or memory
model.

```ini
[variant]
charwidth=ansi unicode*
```

The star marks the default variant. Command-line `--variant` selects a variant.

## Defines

`[define]` sections add preprocessor symbols for target-conditioned code.
Variant-specific define sections add symbols only when that variant is
selected.

## C Types

`[ctype]` maps Camp primitive names to target C spellings. A primitive can be
marked unsupported by target metadata.

## Call Specs And Type Specs

`[callspec]` maps callable call-spec names to C spellings. `[typespec]` maps
target type-spec names to target spellings or representation domains.

## Natural Integer And Pointer Widths

`[nint]` and `[pointer]` sections define natural integer and pointer widths by
default or by type-spec domain. These widths affect checking and code
generation.

## Toolchains

`[toolchain]` defines tools such as `cc`, `ar`, and `ld`. MSVC targets can
declare an expected Visual Studio C++ environment architecture.

## Profiles

Profiles such as `DEBUG` and `RELEASE` define compiler and linker flags.
Variant-specific profile sections can override or extend flags.

## Build Templates

`[build]` templates define shell commands for compile, executable, Windows
executable, static library, and shared library builds. Template variables
include source, object, output, objects, libraries, profile flags, and selected
tool names.

## Frameworks

Targets can allow or reject native frameworks. macOS clang targets permit
framework linking. Targets that do not support frameworks reject `--framework`.

## Subsystems

`--subsystem windows` can be used with executable builds when the target defines
a Windows-executable build template.
