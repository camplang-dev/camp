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

## Configuration Flags

Targets declare the standard configuration flag universe and configure selected
target facts. Flags are booleans. A declaration gives a flag an ambient value;
a configuration gives it a selected value. Target-owned flags may not be
configured by command-line `--configure`.

Standard target flags include platform, architecture, runtime, subsystem, and
capability facts such as `OS_WIN32`, `OS_WIN64`, `OS_LINUX`, `OS_MACOSX`,
`OS_WASI`, `RUNTIME_EMSCRIPTEN`, `SUBSYSTEM_POSIX`, `SUBSYSTEM_DARWIN`,
`ARCH_X86`, `ARCH_X64`, `ARCH_ARM64`, `ARCH_WASM32`, `SUPPORTS_FILES`,
`SUPPORTS_TIMERS`, `SUPPORTS_THREADS`, `SUPPORTS_NETWORK`, and `UNICODE`.

Source must not use flag names as ordinary identifiers. Expression-level checks
must use the compiler intrinsic `configured(FLAG_EXPRESSION)`. Declarations use
`requires (FLAG_EXPRESSION)`, and build inputs use `--requires` for module-level
requirements.

Legacy source-level conditionals and source-local symbol mutation are not part
of the semantic model. `#if`, `#elif`, `#else`, `#endif`, `#define`, and
`#undef` remain parseable enough for migration diagnostics, but they are hard
errors in Camp source.

## Type Specs And Call Specs

Parsing recognizes spec-shaped spelling and grammatical positions without any
catalog vocabulary. Analysis knows all callspecs/typespecs declared in the build's
supplied catalog, including other targets and inactive variants, plus the two
compiler-defined markers. It checks kind, placement, written carrier, and whether
the current requirement context proves availability. Unknown specs and known
but unavailable specs have different diagnostics.

Target declarations and specifier aliases must match
`_[a-z][a-z0-9]*(?:_[a-z0-9]+)*`. Catalog construction rejects malformed names,
including `far`, `_Far`, `__far` and `_far_`. Ordinary source declarations may
use unknown spec-shaped names, but cannot use any known specifier name. The
catalog is constructed once and shared with analysis; unrelated files on disk
do not contribute names.

There is one callspec per method/type declaration or concrete callable, and one
typespec per carrier. Identical duplicates are errors before default normalization.
Only method/type declarations accept a leading callspec. For example:

```camp
_stdcall class _rect { }
_cdecl int f() { return 0; }
fn _pascal nint() _far callback;
delegate _stdcall bool(int x) _far handler;
fn* _near raw;
```

The old two-prefix callable spelling is invalid. Concrete callable typespecs
follow the parameter list. A callable newtype fixes its specs at declaration:
`newtype delegate _stdcall bool Callback(int x) _far;`. References to
`Callback` cannot add specs; a pointer/array/optional around it has its own slot.

| Written carrier | Typespec allowed |
| --- | --- |
| `nint`, `nuint`, `string`, `wstring`, `astring` | Yes |
| `T*`, including `void*` | Yes, on that pointer layer |
| `T[]`, `T?` | Yes, on that wrapper |
| Concrete `fn`, `delegate`, `async`, `once` | Yes, after its parameter list |
| Raw `fn*` | Yes, after `*`; no callspec |
| Generic instantiation itself, fixed array itself | No |
| `untyped`, other primitives | No |
| Plain named class, struct, enum, newtype, generic parameter or type alias | No |

Eligibility belongs to the written carrier, never a named type's underlying
representation. Nested carriers may have separate specs: `byte* _far * _near q`.
No annotation propagates to elements, generic arguments, or callable signatures.

An alias's name selects its category. A spec-shaped name always declares a
specifier alias; an ordinary name declares an ordinary type/declaration alias.
Specifier aliases can chain to specs or specifier aliases. Validate cycles,
exactly one final unguarded fallback, and every alternative's kind, including
inactive alternatives. All alternatives must have the same kind. Knowing an
inactive alternative's kind does not require proving its availability. Thus
`alias _c = configured(OS_WIN32): _stdcall, _targetcall;` is valid;
mixing `_stdcall` and `_far`, aliasing a type through `_c`, or aliasing a spec
through an ordinary name is invalid.

Examples of target type spec domains include near/far/huge pointer families or
memory-space annotations on targets that need them. Examples of call specs
include calling conventions such as a target-specific C calling convention.

Rules for compiler writers:

- validate every source type spec against the selected target;
- validate every call spec against the selected target;
- distinguish unknown specs from known but unavailable specs;
- allow known specs under declarations or flow guarded by their requirements;
- preserve type spec ordering where the target defines one;
- apply type specs to the carrier they decorate, not to unrelated generic
  arguments or callable signatures;
- apply call specs to concrete callable values and emitted function
  declarations;
- include call/type specs in callable compatibility and conversion
  classification where required.

Target files declare the semantic spec universe separately from selected C
spellings:

```ini
[declare.callspec]
_winapi=OS_WIN16 || OS_WIN32
_sysv=SUBSYSTEM_POSIX

[declare.typespec]
_near=OS_WIN16 || OS_WIN32
```

Concrete targets configure platform flags and provide native spellings for the
specs they actually emit. A use of `_winapi` in a `requires (OS_WIN32)`
declaration is valid even when another target parses the same file for metadata.
The declaration is simply unavailable for that target.

### Compiler-Defined Default Specs

`_targetcall` is a callspec and `_targettype` is a typespec defined by the language,
not target files. Both are unconditionally available, including in target-free
analysis and targets without a base. Their names cannot be shadowed by source
declarations or configured by targets as specs, widths, default domains, or
conversion endpoints. They do not participate in target typespec ordering.

`_targetcall` selects the ordinary target calling convention. `_targettype`
selects the representation of the same carrier with no explicit typespec:
code-pointer and data-pointer defaults are separate, natural integers retain
their default integer representation, and selected variants still apply.
Existing placement and carrier restrictions apply to both built-ins.

Aliases may resolve to these specs, including as conditional fallbacks:
`alias _someapi = configured(OS_WIN32): _stdcall, _targetcall;`.
Declared specs remain available for API/metadata serialization, but effective
type identities, signature comparisons, conversions, and width queries treat
the markers as default. This does not merge any other named ABI domains.
Repeated or conflicting explicit specs on the same carrier or callable are diagnosed before
normalization, even when one of them is a default marker.

Explicit `_targetcall` on an interface implementation is an ABI choice: it must
match the interface's effective convention. Only an omitted callspec may inherit
a nondefault interface convention. Aliases and API round-tripping preserve this
distinction.

Native emission follows the existing unannotated carrier/declaration paths and
emits neither built-in spelling as an ABI annotation. An explicit
`@symbol("_targetcall")` or `@symbol("_targettype")` still preserves that native
identifier when the target permits it. Generated callable helpers and export
forwarders must preserve the same effective ABI.

## Requirement-Aware Native Emission

Native emission filters declarations whose effective requirements are false for
the selected target. It also constant-folds `configured(...)` and prunes
selected `if (configured(...))` branches and unreachable short-circuit operands
so emitted C does not reference
declarations that were filtered out.

Emission filtering is deliberately later than parsing and semantic analysis.
The compiler should preserve one source graph for metadata, API headers, and
tooling, then select only the active native surface at C emission.

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

Character literals with ASCII `char` or `achar` resolved types may emit as
ordinary C character literals when the spelling is safe. Non-ASCII and
supplementary character literals must emit as numeric constants with the
resolved target C type, such as `((uint16_t)0xE9)` or `((uint32_t)0x1F600)`, so
generated C does not depend on implementation-defined multicharacter or
wide-character literal behavior.

Emit string literals from their decoded values, never by copying source escape
spellings into C. Narrow literals use the same encoded bytes as their element
counts and fixed-storage copies. Use bounded C escapes so following digits
cannot extend an escape, including an embedded NUL followed by a digit. Wide
strings preserve UTF-16 units and use C-safe escapes or unit storage.

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

When a test or coverage command builds a harness, the requested native artifact
is an executable harness regardless of the production artifact shape. The
compiler may expose private emitted function names to the harness in that
compile, but it must not alter source visibility, Camp API headers, metadata
views, or production shared-library exports.

## Expanded Forms In C

C emission receives lowered expanded forms. It must emit the component layout
chosen by analysis/lowering:

- arrays as element pointer plus length components;
- delegates/once values as call pointer plus context pointer;
- interface instance slots as the interface vtable pointer/context shape;
- witness tables and interface-context tables through one interface C layout,
  with ordinary slot receivers erased to `void*` and witness constructor
  results to `void*`; source receiver and result distinctions must already
  have been checked before C emission;
- params/grouped values as their component layout;
- async functions as completion-callback ABI functions;
- materialized generic returns as explicit storage where lowering requires it.

The emitter should not rediscover expansion rules independently. Use the same
expanded-form services as the analyzer/lowerer.

### Prepared Result Emission

A prep declaration emits its ordinary scalar return and expanded mutable-array
parameter components, even when source used return-position prep; there is no
synthetic array-returning ABI function. A lowered transformed call emits a
sizing call, checked allocation arithmetic, storage allocation, and a writing
call. Both calls use the same captured receiver, explicit arguments, capability
values, target/call specs, error path, and dynamic dispatch target. A full call
emits only its single ordinary call.

Array length and required-size arithmetic use the selected target's length
component type, not the compiler host width. Element-size multiplication and
any existing string-conversion terminator addition must be checked before
allocation or writing. A zero required length remains valid. Explicit short
buffers remain ordinary full calls and rely on the prep contract to clamp
writes. An analysis-approved immediate `.length` case may emit only the sizing
call because the elements are unobservable.

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

`@symbol` overrides apply to every symbol-bearing declaration that declaration
analysis accepts: ABI-visible type declarations, typedef-like newtypes, enum
value macros/constants, global inline constants, static inline constants,
functions, methods, static fields, and struct instance fields. A struct field's
override names the native layout member. For a type declaration, C emission uses
the effective type symbol for the emitted type spelling and as the default
prefix for generated ABI helpers and static members. C emission must not apply
`@symbol` to aliases, parameters, generic parameters, or class instance fields.

Static class containers have no emitted type spelling and no independent ABI
symbol. Their static members are emitted like type-scoped static members using
the static class name as the default prefix, or the member's own `@symbol` when
one is supplied.

## C Reserved Identifiers

C emission must avoid reserved identifiers and collisions. Diagnostics should
catch source names that cannot be safely emitted when a stable translation is
not possible.

Reserved identifier validation should include:

- C keywords;
- Camp source-name restrictions before automatic native-name translation;
- target-reserved names if defined;
- generated helper prefixes;
- expanded component names;
- header guards and include names;
- `@symbol` overrides.

Explicit `@symbol` strings may match Camp reserved words or known specifiers.
Validate them against native identifier and collision rules without applying
Camp source-name restrictions to the strings.

Generated names should be stable and readable enough for C emission tests, but
they must not collide with source declarations or target-reserved identifiers.

## Symbol Emission

Symbol emission combines source name, namespace, visibility, export projection
name, `@symbol`, target export/import prefixes, and generated helper naming
rules.

Rules:

- source lookup uses source names, not emitted symbols;
- default native symbols are computed before emission and are distinct from
  source names;
- `@symbol` supplies the complete effective native symbol for the declaration
  it is attached to;
- namespaced declarations without `@symbol` receive namespace-prefixed default
  native symbols according to declaration category;
- export projections use the projected source name when computing default
  exported symbols;
- emitters should consume the analyzed effective native symbol instead of
  reconstructing native names locally;
- generated interface/vtable/virtual/lambda/async symbols should carry
  provenance and derive from the effective symbol of the source declaration or
  containing type that owns them;
- exported symbols should receive target export decorations;
- imported/shared-library references should receive target import decorations
  where the target defines them.

Existing platform or C-library imports inside a namespace should normally use
`@symbol` to preserve the exact ABI spelling. Without `@symbol`, an `extern`
declaration follows the same namespace-prefixed default symbol policy as a
Camp-authored declaration. If the source declaration itself should be root and
unprefixed, place it in `namespace global` and reference it with `global::`
from namespaced code.

Symbol policy must match metadata/API expectations without making metadata an
ABI dump.

## Headers

The private header contains declarations needed by generated source files in
the current compilation. Public headers expose exported declarations from a
source file. Project API headers expose source API for downstream Camp
compilation.

Camp API headers are self-contained source API files. They should not copy
`using` declarations from producer source files. If a single generated API
header contains declarations from multiple source namespaces, it should emit
namespace blocks and spell cross-namespace type references with qualification
unless the reference is in the current namespace section or is an unambiguous
implicit `Std` name. Root references from a non-root namespace should use
`global::`.

Header emission should preserve:

- header guards derived from stable project/file names;
- target preamble/inclusions;
- declaration order sufficient for C compilation;
- forward declarations where needed;
- export/import decorations;
- generated helpers only where ABI requires them.

Static classes do not emit C forward declarations, struct layouts,
constructors, destructors, create/init/delete helpers, vtables, interface
storage, or receiver parameters. Only their ABI-visible static members and
static storage/inline constants emit C artifacts. Project Camp API headers
preserve `static class` source containers; C headers do not need a container
declaration because the container has no C type identity.

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

For external test modules, project references are consumed through their normal
shared-library API and native library. The production dependency is not compiled
in test-module participation mode. For external coverage, selected shared
project references are rebuilt with production participation and coverage
instrumentation, then linked as instrumented shared-library subjects.

## Coverage Counter Emission

Coverage instrumentation emits C counters for Camp source sequence points. The
runtime counter array and touch function are implementation symbols and are not
source API. They must be unique to the project name and safe for the selected C
target.

The coverage map CSV is the canonical mapping from runtime counter IDs back to
Camp source. Its compact row format is:

```text
v,<version>
p,<file-id>,<mapped-sourcefile>
n,<name-id>,<qualified-function-name>
c,<counter-id>,<kind>,<file-id>,<line>,<name-id>
```

`kind` is `f` for a function-entry counter or `l` for an executable-line
counter. Rows use UTF-8 text, LF line endings, and normal CSV quoting for fields
that require it.

The generated coverage runtime writes a separate count file named from the
coverage map subject. The runner sets the corresponding environment variable
before launching the harness. After the harness exits, the driver parses the map
and count files, writes `camp.coverage-results` JSON when requested, and writes
LCOV as a projection when requested.

Coverage emission must exclude generated helpers, tests, test-only
declarations, harness code, and unselected dependencies from the production
coverage denominator. If a source statement lowers through helper calls, the
counter belongs to the user source statement, not to the helper declaration.

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
- test harness entry-point replacement for executable projects;
- coverage counter emission, map CSV rows, runtime count files, JSON results,
  and LCOV projection.

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
