# Targets And Native Builds

Targets describe the C environment that Camp emits for and the native commands
used to compile generated C. A target is an INI file under `targets/**/*.ini`.
Targets are compiler inputs: changing target metadata invalidates project
reference and package cache checks.

## Target Catalog

The compiler loads all `*.ini` files under the target root. Each file must have
a `[target]` section with a unique `name`:

```ini
[target]
name=gcc-linux-x64
base=c99
```

The selected target is resolved by exact name. Duplicate target names, missing
target names, missing target directories, parse failures, and circular base
chains are fatal diagnostics.

## Base Targets And Merging

A target may inherit from another target with `base=<name>`. The compiler
resolves the base first, copies its sections, then merges the derived target's
sections. Derived keys overwrite base keys in ordinary sections. Some sections,
such as includes, variants, and type spec ordering, preserve ordered lists.

The bundled targets use this to share a `c99` base:

```ini
[target]
name=gcc-linux-x64
base=c99
```

Base inheritance is a metadata mechanism only. It does not imply language-level
target compatibility.

## Target Sections

The recognized sections are:

| Section | Purpose |
|---|---|
| `[target]` | Target name, base, and C include list. |
| `[define]` | Target-owned Camp preprocessor symbols. |
| `[capability]` | Boolean or string feature flags used by compiler services. |
| `[callspec]` | Callable calling-convention spellings. |
| `[typespec]` | Target-specific representation domains and pointer defaults. |
| `[ctype]` | Camp primitive to C spelling map. |
| `[nint]` | Widths for natural integer domains. |
| `[pointer]` | Widths for data and function pointer domains. |
| `[variant]` | Variant groups and defaults. |
| `[toolchain]` | Native tools such as `cc`, `ar`, and `ld`. |
| `[artifact]` | Object, executable, static, shared, and import-library naming. |
| `[build]` | Native build command templates. |
| `[cemit]` | C emitter options such as import/export prefixes and helper names. |
| `[profile.NAME]` | Profile-specific C and linker flags. |
| `[conversion.*]` | Target conversion policy tables. |

Unknown sections are ignored unless their names match a known section family
with invalid data. This lets targets carry future or tool-specific metadata, but
compiler-owned semantics must use documented sections.

## Variants

A `[variant]` section defines groups of mutually exclusive overlays:

```ini
[variant]
charwidth=ansi unicode*
```

The star marks the default variant for the group. A build can select a variant
by name:

```sh
campc build src/main.camp --target msvc-windows-x64 --variant ansi
```

A selected target always has one selected variant per group: either the default
or the command-line value. Selecting two variants from the same group is a
diagnostic. Selecting an unknown variant is a diagnostic and lists available
variants.

Variant-specific sections use `:<variant>` suffixes:

```ini
[define:unicode]
UNICODE=1
_UNICODE=1

[profile.DEBUG:unicode]
cflags=/Zi /DUNICODE /D_UNICODE
```

Variant overlays are applied after base target merging and before target
metadata validation.

## Defines

Target `[define]` keys become Camp preprocessor symbols. They are also
target-owned. A user cannot supply a target-owned symbol manually through
`--define`; doing so is a diagnostic. Select the target or variant that owns the
symbol instead.

This rule keeps `#if WINDOWS` and similar conditions tied to real target
selection.

## Primitive C Types

`[ctype]` maps Camp primitive names to C spellings:

```ini
[ctype]
int=int32_t
nuint=uintptr_t
wchar=uint16_t
```

The spelling `<unsupported>` marks a primitive as unavailable for that target.
Semantic analysis and emission must diagnose uses of unsupported primitives.

The target include list in `[target] include=...` provides headers needed by
those C spellings, such as `stdint.h` and `stdbool.h`.

## Call Specs

`[callspec]` maps Camp callable call-spec names to C spelling:

```ini
[callspec]
_cdecl=
_stdcall=__stdcall
_sysv=__attribute__((sysv_abi))
```

Calls specs apply only to concrete callable types and function declarations.
They do not apply to `void*`, `fn*`, `nint`, `nuint`, or `untyped`. See
[Conversions, Raw Carriers, And Fence Casts](../semantics/03-conversions-raw-carriers-and-fence-casts.md).

## Type Specs

`[typespec]` names target representation domains, address-space spellings, or
similar target-specific carrier categories:

```ini
[typespec]
_near=__near
_far=__far
_huge=__huge
```

The special `default=<code>/<data>` entry selects default function-pointer and
data-pointer domains for a target or variant:

```ini
[typespec:large]
default=_far/_far
```

Type specs are ordered. The compiler uses that order for the default widening
relationship when no explicit conversion policy is present.

## Natural Integers And Pointer Widths

`[nint]` and `[pointer]` define widths for natural integer and pointer carrier
domains:

```ini
[nint]
default=64
_near=16
_far=16
_huge=32

[pointer]
default=64
_near=16
_far=32
_huge=32
```

Widths must be 16, 32, or 64. A domain-specific width key must name a valid
typespec. If no key matches, the compiler falls back to the default entry, and
then to 32 bits.

## Conversion Policy Tables

Targets may specify conversion levels between type-spec domains:

```ini
[conversion.data_pointer]
_near->_far=implicit
_far->_near=unsafe

[conversion.function_pointer]
_near->_far=explicit

[conversion.abi_slot]
_near->_far=forbidden
```

The carrier families are `data_pointer`, `function_pointer`, `nint`, and
`abi_slot`. Value conversion levels are `implicit`, `explicit`, `unsafe`,
`fence`, and `forbidden`. ABI-slot compatibility uses only `compatible` or
`forbidden`.

When a value conversion has no table entry, the compiler permits same-domain
conversions, allows widening according to type-spec order, allows explicit
conversion among known compatible domains, and otherwise rejects the conversion.
ABI-slot compatibility has no implicit fallback across distinct domains.

## Toolchains

`[toolchain]` gives tool names:

```ini
[toolchain]
cc=gcc
ar=ar
```

If a tool is missing, the compiler uses the tool key as the command name. MSVC
targets may declare `msvc_arch`. On Windows, Camp uses a matching loaded Visual
Studio C++ environment when one is present. Otherwise, it tries to find Visual
Studio Build Tools, load `vcvarsall.bat` for the requested architecture, and run
native build commands with that environment. Set `CAMP_VCVARSALL` to the full
path of `vcvarsall.bat` for a custom installation.

## Profiles

Profiles are named by `[profile.NAME]` sections. The compiler accepts `DEBUG`
and `RELEASE` profile names and normalizes them to uppercase:

```ini
[profile.DEBUG]
cflags=-g
ldflags=

[profile.RELEASE]
cflags=-O2
ldflags=
```

Profile flags are available to build templates as `{profile_cflags}` and
`{profile_ldflags}`. Variant-specific profile sections may override or extend
profile flags after variant selection.

## Native Build Templates

`[build]` templates are shell command strings. The current native driver uses:

| Template | Used for |
|---|---|
| `compile` | Compile each generated C source to an object. |
| `exec` | Link an executable. |
| `winexe` | Link an executable with the Windows subsystem. |
| `static` | Archive a static library. |
| `shared` | Link a shared library. |

Template variables include:

| Variable | Meaning |
|---|---|
| `{cc}` | Quoted C compiler command. |
| `{ar}` | Quoted archive command. |
| `{ld}` | Quoted linker command. |
| `{source}` | Quoted C source path. |
| `{object}` | Quoted object path. |
| `{objects}` | Space-separated object paths. |
| `{libs}` | Space-separated library and framework arguments. |
| `{output}` | Quoted final artifact path. |
| `{import_library}` | Quoted shared import-library path, when any. |
| `{profile_cflags}` | Selected profile C flags. |
| `{profile_ldflags}` | Selected profile linker flags. |
| `{build_cflags}` | Emitter-selected flags for exec/static/shared builds. |

The native driver runs commands from the build subdirectory and captures stdout
and stderr. A command timeout or non-zero exit code is a compiler error.

## Artifacts

Target `[artifact]` values define final names:

```ini
[artifact]
object_ext=.o
exec_prefix=
exec_ext=
static_prefix=lib
static_ext=.a
shared_prefix=lib
shared_ext=.so
shared_import_prefix=
shared_import_ext=.lib
```

Static builds delete an existing archive before archiving so stale object
members do not survive. Shared builds may produce both a runtime library and an
import library. If an import library exists, downstream link commands use it;
otherwise they link the shared library itself.

## Frameworks

`--framework` is valid only when a native artifact is being built, the artifact
is not static, and the selected target supports framework linking. Framework
names may contain letters, digits, `_`, `-`, and `.` and may not start with
`-`.

On supported targets the driver expands each framework as:

```text
-framework Cocoa
```

Targets that do not support frameworks diagnose the option before invoking the
native toolchain.

## Windows Subsystem Builds

`--subsystem windows` may be used only with `--artifact exec`. Internally the
request selects the `winexe` native build template. A target that lacks that
template reports a missing-template diagnostic.

This is separate from the ordinary shared-library import/export machinery in
the C emitter.

## C Emitter Capabilities

`[cemit]` values control target-specific C emission details:

```ini
[cemit]
dll_export_prefix=__declspec(dllexport)
dll_import_prefix=__declspec(dllimport)
shared_cflags=-fvisibility=hidden
alloca=_alloca
memcpy=memcpy
memmove=memmove
memset=memset
```

The emitter, target semantic checks, and native build driver must agree on
these values. For example, shared-library API headers depend on import/export
prefixes, and shared native builds depend on the matching build C flags.
