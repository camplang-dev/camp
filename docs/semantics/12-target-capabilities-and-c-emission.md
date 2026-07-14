# Target Capabilities And C Emission

This supplement describes how target definitions affect semantic validation and
C emission. The command-line target/build workflow is documented in
[Targets And Native Builds](../compiler/04-targets-and-native-builds.md); this
document focuses on compiler-writer invariants.

Camp's target model is intentionally data-driven. Target files define primitive
C spellings, call/type specifiers, natural integer widths, pointer widths,
conversion policies, preprocessor defines, toolchain commands, artifact names,
and C-emitter details. The semantic analyzer and emitter must treat the
selected target as part of the compilation environment.

## Target Definition Resolution

Target files are loaded from the target directory, merged through base target
chains, and validated before compiler requests use them. Variant overlays are
applied after selection.

Resolution rules:

- each target INI declares a unique `[target] name`;
- a target may name a base target;
- base targets are resolved before derived targets;
- circular base chains are invalid;
- sections are merged so derived values override base values;
- requested variants are resolved against variant groups and overlays;
- the selected target and variants become part of the artifact/cache identity.

Do not bypass the target catalog by reading target files directly in feature
code. The catalog owns validation and merging.

## Target Sections

Compiler-visible sections include:

- target identity and base chain;
- variants and variant groups;
- call specs;
- type specs and type-spec ordering;
- primitive C type spelling;
- natural integer widths;
- pointer widths;
- target-owned defines;
- include/preamble lines;
- toolchain commands;
- artifact naming;
- native build templates;
- C-emitter settings;
- profile-specific flags and defines;
- conversion policy tables.

Adding a new section should include target-loader validation, docs in the
compiler target reference, tests for merge/override behavior, and clear default
behavior when the section is absent.

## Target-Owned Defines

Target-owned defines are added to preprocessing and should be distinguishable
from user `--define` values where diagnostics or tooling need that distinction.

The compiler uses target-owned defines for platform-specific source selection.
A user should not be able to spoof a target-owned define and trick the compiler
into type-checking against one target while emitting for another. Keep user
defines and target defines separate in the compilation model.

Profile defines, such as debug/release symbols, are also part of preprocessing.
They should be deterministic and visible to dumps/tooling as part of the
compilation environment.

## Type Specs And Call Specs

Type specs apply to target-capable carrier types. Call specs apply to concrete
callables. The parser may accept names syntactically, but semantic validation
checks selected-target definitions.

Examples of target type spec domains include near/far/huge pointer families or
memory-space annotations on targets that need them. Examples of call specs
include calling conventions such as a target-specific C calling convention.

Rules for compiler writers:

- validate every source type spec against the selected target;
- validate every call spec against the selected target;
- preserve type spec ordering where the target defines one;
- apply type specs to the carrier they decorate, not to unrelated generic
  arguments or callable signatures;
- apply call specs to concrete callable values and emitted function
  declarations;
- include call/type specs in callable compatibility and conversion
  classification where required.

## Conversion Policy Tables

Targets can define conversion policy tables for data pointers, function
pointers, natural integers, and ABI slot compatibility. The conversion
classifier consumes those tables.

The language's core conversion categories are target-independent, but some
specific casts depend on target domains. For example, a 16-bit segmented target
can define pointer-domain conversions that differ from a flat 64-bit target.

The analyzer should ask the target conversion policy for target-sensitive cases
instead of baking platform assumptions into language code. Diagnostics should
name when behavior is target-sensitive.

## Natural Integers And Pointer Widths

`nint` and `nuint` are target-sized integer carriers. Pointer-to-integer and
integer-to-pointer conversions depend on the selected target's natural integer
widths and pointer widths. A target may define widths per type spec domain.

Compiler code that reasons about pointer depth, integer magnitude, constant
folding, or C suffixes should avoid assuming host-machine widths. Use the
selected target.

## Primitive C Spelling

`[ctype]` defines C spellings for primitive Camp types. Unsupported primitive
types should be diagnosed when used for a selected target.

The emitter should use target C spelling for:

- integer and floating primitives;
- character/string unit types;
- `bool`;
- `void`;
- natural integers;
- target-specific unsupported primitive diagnostics.

If a target marks a primitive as unsupported, the analyzer should diagnose at
the source use before emission rather than generating invalid C.

## C Emission Preconditions

C emission requires a lowered bindable tree with no unresolved/error marker
types. The emitter should validate before writing files and delete partial
generated files if emission fails.

C emission targets C99. If an emit kind is unsupported, the emitter should fail
with an emitter diagnostic rather than trying to approximate another C dialect.

Generated output includes:

- private header;
- source files for non-API source inputs;
- public headers for files with exported declarations;
- project API header where requested;
- optional executable main wrapper;
- emitted includes/preamble from the target;
- generated helper declarations needed by lowered ABI.

## Expanded Forms In C

C emission receives lowered expanded forms. It must emit the component layout
chosen by analysis/lowering:

- arrays as element pointer plus length components;
- delegates/once values as call pointer plus context pointer;
- interface instance slots as the interface vtable pointer/context shape;
- params/grouped values as their component layout;
- async functions as completion-callback ABI functions;
- materialized generic returns as explicit storage where lowering requires it.

The emitter should not rediscover expansion rules independently. Use the same
expanded-form services as the analyzer/lowerer.

## Enums And Inline Constants In C

Exported Camp enums should be emitted as target-sized integer typedefs plus
named value macros/constants. Do not emit them as C `enum` declarations unless a
future target contract explicitly defines compatible width and signedness
behavior. The Camp enum underlying type is semantic; the C spelling must
preserve it.

Inline constants are emitted as typed macro-style constants when they are part
of the native surface. Their source values and kinds remain metadata facts.
Emitter code should consume the analyzer's computed constant value rather than
re-evaluating source expressions in C-emission-specific logic.

`@symbol` overrides apply to the typedef, enum value macro/constant, global
inline constant, static inline constant, function, or static field symbol that
declaration analysis accepted. C emission must not apply `@symbol` to instance
fields or non-enum type declarations rejected by analysis.

## C Reserved Identifiers

C emission must avoid reserved identifiers and collisions. Diagnostics should
catch source names that cannot be safely emitted when a stable translation is
not possible.

Reserved identifier validation should include:

- C keywords;
- Camp reserved words when they become emitted identifiers;
- target-reserved names if defined;
- generated helper prefixes;
- expanded component names;
- header guards and include names;
- `@symbol` overrides.

Generated names should be stable and readable enough for C emission tests, but
they must not collide with source declarations or target-reserved identifiers.

## Symbol Emission

Symbol emission combines source name, namespace, visibility, `@symbol`, target
export/import prefixes, and generated helper naming rules.

Rules:

- source lookup uses source names, not emitted symbols;
- `@symbol` affects emitted native symbols;
- namespaced/export-as modules affect symbol qualification where the emitter
  defines it;
- generated interface/vtable/virtual/lambda/async symbols should carry
  provenance;
- exported symbols should receive target export decorations;
- imported/shared-library references should receive target import decorations
  where the target defines them.

Symbol policy must match metadata/API expectations without making metadata an
ABI dump.

## Headers

The private header contains declarations needed by generated source files in
the current compilation. Public headers expose exported declarations from a
source file. Project API headers expose source API for downstream Camp
compilation.

Header emission should preserve:

- header guards derived from stable project/file names;
- target preamble/inclusions;
- declaration order sufficient for C compilation;
- forward declarations where needed;
- export/import decorations;
- generated helpers only where ABI requires them.

## Shared Library Export/Import

Shared library builds use target C-emitter values such as export/import
prefixes and shared-library C flags. API headers for shared dependencies must
expose the correct import/export surface.

When a project builds a shared library, the same source declaration may be:

- exported from the library being built;
- imported by a dependent project;
- private inside the current artifact.

The compiler and native build driver should keep these roles distinct. Do not
emit export decorations into a consumer import surface.

## Object, Static, Shared, And Executable Artifacts

Native build templates compile source files to objects, then link or archive
objects into the requested artifact kind. Generated file lists must include
link and runtime files.

Emission should provide the native build driver with:

- generated source files;
- generated headers;
- link inputs from packages/project references;
- runtime files that must be copied next to an executable or library;
- import libraries where the target/toolchain creates them.

The native build driver owns command execution, profiles, and artifact paths.
The emitter owns C source/header correctness.

## Objective-C Capability Boundary

Targets can describe Objective-C-related capabilities. Unless the active
language docs define a source syntax and emission rule for a feature, target
entries should be treated as target metadata only. The compiler should not infer
new language behavior from the presence of a target capability value.

## Diagnostics

Target and emission diagnostics should identify:

- missing target directory or empty target catalog;
- duplicate target names;
- missing or circular base target;
- unknown target variant;
- user-defined target-owned symbol;
- unknown type spec or call spec;
- unsupported primitive on selected target;
- target-sensitive conversion warning/error;
- unresolved type reaching C emission;
- C reserved identifier collision;
- unsupported emit kind;
- file-system failure during emission.

Diagnostics should include target name, variant, source range, or file path as
appropriate.

## Test Surface

Target/C emission changes should cover:

- target catalog load/merge/variant behavior;
- target-owned define validation;
- callspec/typespec parsing and diagnostics;
- conversion policy differences;
- unsupported primitive diagnostics;
- reserved C identifiers;
- emitted headers/source for expanded forms;
- shared/static/executable artifact metadata;
- export/import decoration behavior;
- unresolved lowered-tree emission failures.

## Implementation Anchors

Primary implementation points include:

- `TargetCatalog.cs` for target loading, merging, variants, and target
  capability values;
- `CompilerDriver.cs` for target selection and compiler options;
- `BindableNodeAnalyzer.TypeBinding.cs` and conversion classification for target
  spec validation;
- `CCodeEmitter.cs` for C output, header generation, and emission validation;
- `NativeBuildDriver.cs` for native command templates and artifacts;
- target files under `targets/`;
- target, conversion, C emit, and native build tests.
