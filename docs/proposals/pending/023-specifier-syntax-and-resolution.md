# Call And Type Specifier Syntax And Resolution

## Status

Pending. Numbered and moved to pending with maintainer approval on 2026-10-05.
Not yet implemented or accepted. This lifecycle transition does not itself
authorize implementation.

## Proposal Date

2026-10-05

## Last Updated Date

2026-10-05

## Summary

Make call specifier (callspec) and type specifier (typespec) parsing depend only
on source spelling and grammatical position. Resolve names, kinds, requirements,
and target applicability during analysis, never during parsing. Specify one
callspec per declaration/callable and one typespec per carrier, with explicit
rules for aliases, default markers, and underscore-shaped ordinary names.
This is a parsing and diagnostics change, not a change to the meaning or ABI
of typespecs or callspecs. Explicit native names supplied through `@symbol`
are separate from Camp declaration identifiers and are exempt from Camp's
specifier-name and reserved-word restrictions.

The maintainer approved the syntax decisions underlying this draft and these
additional clarifications:

- The nameless-parameter disambiguation rule also applies before `=`. Naming
  a pointer parameter `_value` with a default requires, for example,
  `int* _targettype _value = null`.
- All alternatives of a conditional specifier alias must resolve to the same
  kind: callspec or typespec. Selecting one branch does not excuse a mixed-kind
  alias.
- `@symbol` may explicitly select a spec-shaped or Camp-reserved native name
  without making that spelling a Camp source identifier or specifier use.
- Preserve all existing typespec/callspec semantics, including casting,
  ascription, interface implementation, and callable-newtype requirements.

## Scope And Non-Goals

Update the bootstrap compiler, target-catalog validation, syntax/editor paths,
API serializers, tests, and living documentation. Do not introduce another
compiler/runtime dependency, new target ABIs, new calling conventions, or new
conversion policies. Other compiler implementations are outside this proposal.

The authorized changes concern spelling, placement, disambiguation, syntax-tree
representation, name resolution for these syntactic forms, and diagnostics for
invalid forms. They do not alter the semantics of any resolved typespec or
callspec. In particular, preserve:

- target representation, widths, ABI identity, and native calling conventions;
- implicit/explicit conversions, safe/unsafe/fenced casts, and target-defined
  conversion ordering and restrictions;
- ascription requirements and signature compatibility;
- interface implementation and override requirements, including omission versus
  explicit default-convention inheritance;
- callable-newtype identity, fixed declared specifiers, compatibility, and
  ascription requirements;
- existing declaration/flow requirement proofs and target applicability rules.

The carrier table and placement/cardinality rules specify which source forms
are accepted or diagnosed; they do not authorize inventing new representations,
conversions, or contract behavior. Restatements of default-marker or callable
semantics below are preservation requirements, not semantic amendments. If an
implementation change would require altering those semantics, stop and obtain
separate direction rather than treating this proposal as authorization.

## Current Implementation And Motivation

Read-only source-review baseline:
`0c90d6e8c441a7cb38ae561385d3db0bcac8867e`.
No builds or tests were run to prepare this proposal.

- `CampParser.cs` exposes `CampParserOptions.TypeSpecs` and `CallSpecs` populated
  through `FromTarget`. Its recognition helpers consult those sets and also
  recognize unknown underscore identifiers.
- Callable parsing currently has two specifier slots immediately after the
  callable keyword. Known typespecs can also enter through a prefix-type path.
- `BindableNodeAnalyzer.cs` resolves separate call/type alias kinds;
  `BindableNodeAnalyzer.TypeBinding.cs` resolves and validates specifiers.
  These paths must retain semantic responsibility without deciding parser shape.
- `TargetCatalog.cs` owns declared specs, requirements, native spellings, and
  target-domain validation. `CompilerDefinedSpecs.cs` owns the default markers.
- BUG-227 records ignored leading specifiers on globals/fields. BUG-228 records
  declarations using known specifier names. These are related acceptance cases,
  not authority for additional language changes.
- Living semantics currently describe a target-dependent parser vocabulary and
  “target-capable carriers” without the complete eligibility table below.
  Existing fixtures include `CEmit/nonunderscore_target_specs`, which must be
  reconciled with the newly required spelling rule rather than silently retained
  as accepted syntax.

The desired result is identical syntax trees for identical source regardless of
target catalogs, while diagnostics still distinguish unknown names, wrong kinds,
invalid carriers, and unproven requirements.

## 1. Spec-Shaped Identifiers And Known Names

A spec-shaped identifier starts with one underscore followed by a lowercase
ASCII letter, contains only lowercase ASCII letters, digits, and underscores,
has no consecutive underscores, and does not end in an underscore.

Equivalent spelling pattern: `_[a-z][a-z0-9]*(?:_[a-z0-9]+)*`.

Examples: `_cdecl`, `_far`, `_my_api`, `_x1`.
Non-examples: `_Far`, `__far`, `_far_`, `far`, `_1x`.

Target-declared callspecs and typespecs must conform. Reject malformed catalog
declarations when constructing the catalog; do not silently ignore or rename
them. Specifier aliases must conform too.

This spelling is not a universal reserved-name convention. Ordinary types,
functions, fields, parameters, and locals may use spec-shaped names that are not
known specifiers, subject to the position/disambiguation rules below. Type aliases
are the exception: a spec-shaped alias name always declares a specifier alias.

The analysis known-name set includes all declared callspecs/typespecs in the
build's supplied catalog, including entries unavailable on the selected target,
plus the two language-defined markers. Do not restrict it to active native
spellings or scan arbitrary unrelated catalogs from disk. Construct the catalog
once per build and share it with analysis; parsing needs none of its names.

An ordinary declaration cannot use a known specifier name, even when that spec
is unavailable on the active target. Enforce this for types, functions, globals,
fields, locals, parameters, and aliases that would redefine a known specifier.
This is semantic rejection for catalog names, not target-dependent tokenization.

### Explicit Native Names Are Not Camp Identifiers

The preceding restriction applies to the Camp declaration name, not the string
value of `@symbol`. Explicit native names may be spec-shaped, match a known
specifier, or match a Camp reserved word, including `_targetcall` and
`_targettype`, provided the selected native target permits that spelling.

```camp
@symbol("_stdcall")
export void stdcall() { }

export struct Data
{
	@symbol("_far") int far;
}
```

Camp lookup uses `stdcall` and `far`. The native function symbol is `_stdcall`
and the emitted struct member name is `_far`; a field is not a separate linker
symbol. Neither attribute changes calling convention, representation, source
lookup, nor specifier resolution. `@symbol` does not itself export anything or
expose opaque layout; normal declaration visibility still applies.

Preserve ordinary native-symbol/member collision checks, C-identifier validity,
and actual native-toolchain restrictions. Exemption from Camp reserved names is
not exemption from native reserved identifiers. Generated Camp API headers must
retain the source name plus `@symbol`, never substitute the native spelling as
the Camp declaration name. Apply known-specifier-name diagnostics to the source
name, not the effective native symbol.

## 2. Language-Defined Default Markers

Exactly two specifiers are language-defined reserved words:

- `_targetcall`: the target's ordinary calling convention.
- `_targettype`: the annotated carrier's ordinary representation.

Both are always available, including without stdlib or a target-file declaration.
Targets cannot declare, redefine, or configure them. Preserve existing rejection
of attempts to configure them as target domains, widths, or conversion endpoints.

`_targetcall` has the effective identity and compatibility of omission, but its
explicit presence remains significant for contract inheritance. Omission may
inherit a nondefault interface/base convention; explicit `_targetcall`, including
an alias resolving to it, must report a mismatch against that convention.

`_targettype` affects only its carrier, creates no new identity or conversion,
and is permitted only on eligible carriers. Existing code/data pointer defaults,
natural integer defaults, and target variants still determine representation.

Diagnose duplicate/conflicting explicit specifiers before normalizing defaults.
Neither marker is emitted as a native C specifier. An explicit `@symbol` string
matching a marker remains an ordinary native name under section 1, not a marker
to normalize or suppress. Preserve explicit `_targetcall`
through source API/metadata round-tripping; retain declared/default information
where the existing metadata contract requires it.

## 3. Positions And Cardinality

Only these four positions recognize specifiers:

1. Leading callspec on a method or type declaration.
2. Callspec immediately after `fn`, `delegate`, `async`, or `once`.
3. Typespec immediately after the carrier it annotates; for a concrete callable,
   after its parameter list.
4. Specifier-alias right-hand side.

Outside these positions a spec-shaped identifier is an ordinary identifier.
There is at most one callspec per declaration or callable type, and one typespec
per carrier. Identical repetitions are errors too. Different nested carriers
may each have their own typespec: `byte* _far * _near q`.

The parser can retain leading specifier occurrences on any declaration so analysis
can diagnose inappropriate placement. Leading callspecs are valid only on method
or type declarations, not fields, globals, or locals. `_far byte* p` is a leading
specifier in a callspec position, not an alternate prefix-typespec spelling.

```camp
_stdcall class _rect { }
_cdecl int f() { return 0; }
fn _pascal nint() _far callback;
delegate _stdcall bool(int x) _far handler;
fn* _near raw;
```

Raw `fn*` has no concrete signature and cannot take a callspec. The old
`fn _near _pascal nint()` form is not retained as a compatibility spelling.
Repeating a convention on separate contract/implementation declarations is not
a duplicate syntax occurrence: analysis checks their compatibility.

## 4. Typespec Carrier Eligibility

| Carrier | Typespec allowed |
| --- | --- |
| `nint`, `nuint` | Yes |
| `string`, `wstring`, `astring` | Yes |
| `T*`, including `void*` | Yes, on that pointer layer |
| `T[]` | Yes, on the array carrier, not its elements |
| `T?` | Yes, on the optional carrier |
| Concrete `fn`, `delegate`, `async`, `once` | Yes, after the parameter list |
| Raw `fn*` | Yes, after `*` |
| Generic instantiation `X<...>` itself | No |
| Fixed array itself | No |
| `untyped` | No |
| Other primitives, including `int`, `bool`, `byte` | No |
| Plain named class, struct, enum, newtype, generic parameter, or type alias | No |

Eligibility attaches to the written carrier, not a named type's underlying
representation. An eligible pointer/array/optional wrapped around another type
has its own slot. No annotation propagates to elements, generic arguments, or
the result/parameters of a callable.

Callable newtypes fix their specs at declaration:

```camp
newtype delegate bool PlainFunc(int x);
newtype delegate _stdcall bool FuncWithSpecs(int x, int y) _far;
```

References cannot add or change them. `PlainFunc _near f`,
`PlainFunc _targettype f`, and `FuncWithSpecs _far f` are invalid named-type
annotations. `_stdcall PlainFunc f` and `_cdecl FuncWithSpecs f` are invalid
leading callspecs on variables. Combined invalid forms do not override either
restriction.

## 5. Aliases

The alias name selects the syntactic category. A spec-shaped name declares a
specifier alias; another name declares a type alias or existing function/declaration
alias. Do not classify the alias by consulting the target or inspecting the
resolved right-hand side during parsing.

A specifier alias targets specs or specifier aliases. Chains are allowed; cycles
are diagnosed. An ordered conditional list must end in exactly one unguarded
fallback. All alternatives, including unselected alternatives, must resolve to
the same kind. Requirement satisfaction is distinct from knowing an alternative's
kind: an inactive target-specific spec can still be a known callspec/typespec.

```camp
alias _my_api = configured(OS_WIN32): _stdcall, _targetcall;
alias _someptr = configured(OS_WIN16): _far, _targettype;
alias _next = _my_api;
```

Reject mixed callspec/typespec alternatives rather than letting the alias change
kind with configuration. Preserve existing alias scope/import rules; this is
not an authorization to create a new alias namespace or lookup mechanism.

- `alias c = _someclass;` is an ordinary alias; reject if its target is a spec.
- `alias _c = someclass;` is invalid because its target cannot be a spec name.
- `alias _c = _rect;` parses as a specifier alias but fails if `_rect` is a class.
- Type aliases cannot have spec-shaped names, even for pointer/callable targets.

Analysis may own all these errors, keeping parser decisions purely structural.

## 6. Parsing And Disambiguation

Each specifier occurrence has its own syntax node and source range. Analysis
must not rediscover occurrences from raw token text. Preserve erroneous repeated
occurrences sufficiently to diagnose them, rather than overwriting a single
token slot or normalizing them away.

Use bounded grammatical lookahead/backtracking, not known-name lookup:

1. **Leading declaration:** a spec-shaped identifier is a callspec when the
   following tokens can still form the declaration's type and declarator.
   `_rect _far _get()` means callspec `_rect`, result `_far`, name `_get`.
   `_rect _get()` instead means result `_rect`, name `_get` because the leading
   callspec reading cannot form a declaration.
2. **Callable keyword:** accept a spec-shaped callspec if a result type can
   still follow. `fn _a()` has result type `_a`, not a callspec and missing result.
3. **After callable parameters:** the spec-shaped occurrence is the callable
   carrier's typespec, subject to the declarator-name rules below.
4. **After a type:** a spec-shaped occurrence followed by a name or continued
   type structure such as `*` or `[` is a typespec. Analysis determines carrier
   validity: `int _far x` gets an invalid-carrier diagnostic, not a parser error.
5. **Fields/locals and other name-required variable declarations:** when the
   last spec-shaped occurrence is followed by `;`, `=`, or `,`, it is the name.
   `int _far _x;` has typespec `_far` and name `_x` (invalid carrier); `int* _far;`
   has name `_far` (known-specifier-name diagnostic if `_far` is in the catalog).
6. **Parameters:** on an eligible carrier with an unfilled typespec slot, a
   spec-shaped identifier before `)`, `,`, or `=` is always the typespec.
   This applies equally to ordinary declarations, callable types, delegates,
   and callable newtypes. Fill the slot with `_targettype` to disambiguate a
   spec-shaped parameter name. On an ineligible carrier the identifier is a
   name without a disambiguator.
7. **Casts:** no declarator name exists. In `(_rect[] _far)value`, `_far` is the
   array's typespec.

Examples for parameters:

| Source | Interpretation |
| --- | --- |
| `fn void(int* _bogus)` | Nameless pointer parameter, typespec `_bogus` |
| `fn void(int* _targettype _bogus)` | Pointer parameter named `_bogus` |
| `fn void(int _bogus)` | Integer parameter named `_bogus` |
| `fn void(PlainFunc _bogus)` | Named-type parameter named `_bogus` |
| `void f(int* _value = null)` | Nameless pointer parameter with spec `_value` and default |
| `void f(int* _targettype _value = null)` | Pointer parameter named `_value` with default |

An unknown spec diagnostic in this parameter position must suggest
`_targettype _name` when a name may have been intended. Once a slot is filled,
a grammatical declarator name is a name; an actual second annotation on the
same carrier is an error. Never consult the catalog to choose between these
readings.

The representative nested form is:

```camp
_cdecl _rect[] _far _getrect(fn _my_api _rect(int* _far) _targettype _param);
```

It has leading callspec `_cdecl`, array-result typespec `_far`, callable-parameter
callspec `_my_api`, a nameless pointer argument with typespec `_far`, and callable
typespec `_targettype` disambiguating parameter name `_param`. Whether those
spec names exist and are applicable is a later analysis question.

## 7. Analysis And Diagnostics

Resolve each occurrence through visible specifier aliases, then language-defined
markers, then catalog declarations. Unknown names are errors. Alias resolution
must detect cycles, validate all alternative kinds, and retain explicitness when
the selected target is a default marker.

Report distinct errors for:

- malformed catalog names and forbidden redefinitions of language markers;
- unknown specifier, including the parameter-name hint where applicable;
- wrong specifier kind for its position;
- leading callspec on a noncallable/non-type declaration;
- typespec on an ineligible carrier;
- repeated/conflicting specifiers in one slot;
- invalid alias target, missing/misplaced fallback, cycles, or mixed kinds;
- ordinary declaration named after a known specifier;
- known specifier whose requirement is not proven;
- explicit contract convention mismatch.

Use declaration/flow requirement proofs for known but unavailable specs. Do not
misreport them as unknown, or make them available simply because the parser
recognized their spelling. Preserve existing effective-convention compatibility
and carrier conversion policy after validation.

## 8. Implementation Approach

1. Centralize the spelling predicate and validate target catalog declarations.
   Keep the two reserved language markers explicit and always available.
2. Replace target-name-driven recognition in `CampParser.cs` with grammatical
   recognition. Remove spec-name dependency from parser options/call sites;
   preserve unrelated options. Update syntax nodes, visitors, projections,
   bindable-node construction, and recovery together.
3. Update analysis alias categorization/resolution, spec-slot/cardinality and
   carrier checks, source-declaration-name validation (excluding `@symbol`
   values), and preservation of existing contract explicitness.
   Inspect `BindableNodeAnalyzer.Declarations.cs`, `TypeBinding.cs`, and the
   shared resolution helpers rather than patching individual examples.
4. Update source/API serialization and metadata paths, including callable
   newtypes and anonymous/defaulted parameters. Generated Camp must parse with
   the same shape-only grammar and preserve effective ABI plus explicit default
   callspec intent. Do not accidentally emit the old two-prefix callable form.
5. Reconcile affected fixtures and repository source/target definitions, then
   update living docs together. Recheck current BUG-227/228 records; close only
   upon verified fixes, under the existing bug-record protocol.

These are dependency-ordered implementation areas, not authorization to run
work or a replacement for a future handoff. Do not hide shape differences behind
different parsing modes for the CLI, language service, or API importer. If a
public parser API needs a transitional wrapper, it must not affect syntax based
on supplied name lists.

## 9. Documentation Changes

Canonical semantics must specify the complete rules, with the language guide
and LLM guide giving concise usable examples:

- Semantics 01: target-independent parsing and analysis-owned name resolution.
- Semantics 03: replace vague carrier eligibility with the exact table; retain
  target-specific conversion rules separately.
- Semantics 07 and 09: callable/newtype placement and explicit/default callspec
  handling for contract implementations and overrides.
- Semantics 11: alias/API serialization, spec nodes and declared/effective
  spec information where applicable; explicitly distinguish source-name
  restrictions from permitted `@symbol` native spellings.
- Semantics 12: spelling/catalog constraints, built-ins, requirement validation,
  and removal of the target-known-name parser requirement.
- Semantics 13: distinct diagnostics and the parameter-name disambiguation hint.
- Language guide declaration/alias, pointer, callable, and interop sections:
  correct existing forms, especially `fn _near _pascal nint() callback` in
  chapter 08 to `fn _pascal nint() _near callback`.
- Compiler target-file documentation: required spec-name spelling and catalog
  versus active native configuration distinction.
- `docs/camp-llm-coding-guide.md`: valid positions, named-parameter escape via
  `_targettype`, one-per-slot rules, and callable-newtype restrictions.

Search living docs and repository examples for stale accepted spellings. Do not
rewrite historical accepted/rejected proposals or introduce cross-project
implementation references into these documents.

## 10. Verification And Acceptance

Prefer extending existing dense parser, semantic, target, API, and C-emission
tests. Planned maintained cases and implementation should be ready before the
focused verification run; do not add redundant coverage after a passing run.

- [ ] Identical source yields identical specifier nodes/ranges and parse errors
  with no catalog and with differing catalogs; editor parsing uses the same rule.
- [ ] All spelling examples, ordinary unknown spec-shaped names, and reserved
  catalog-name collisions behave as specified, including BUG-228 cases.
- [ ] Explicit `@symbol` names matching specs or Camp reserved words are not
  rejected by Camp source-name checks. Function and struct-field cases preserve
  source lookup, native spelling, visibility, and API round-tripping. Genuine
  native collisions or target-invalid names still receive diagnostics.
- [ ] All four positions, declaration backtracking, callable result-name
  ambiguity, casts, parameter delimiters/defaults, and disambiguators are covered.
- [ ] Invalid leading globals/fields/locals are diagnosed (including BUG-227),
  rather than ignored or interpreted as supported prefix typespecs.
- [ ] Every eligible/ineligible carrier category is covered, including nested
  independent carriers, generic instantiation rejection, strings, optionals,
  raw `fn*`, and callable-newtype declarations versus references.
- [ ] Identical and conflicting duplicates are rejected before normalization;
  one spec per independent carrier remains valid.
- [ ] Alias chains, cycles, ordered fallback validation, alias-name categories,
  and mixed-kind alternatives are tested. An unselected branch still has its
  kind validated without requiring it to be active on the current target.
- [ ] Unknown, wrong-kind, invalid-carrier, and requirement-not-proven cases
  remain distinguishable; guarded known-spec uses remain valid.
- [ ] Both language markers work without stdlib/target declarations; omission
  versus explicit `_targetcall` is preserved across interface/override contracts,
  aliases, and API round trips. Native output contains neither marker as a
  specifier; an explicitly requested native symbol of that spelling is preserved.
- [ ] Existing conversion/cast, ascription, interface/override, and callable-
  newtype tests retain their semantic expectations. Syntax migrations may change
  fixture spelling and relevant diagnostics, not relax or redefine those rules.
- [ ] Emitted Camp API reparses without catalog-sensitive syntax, preserves
  parameter names/defaults, and retains declared ABI intent; representative
  native declarations/callbacks still compile and execute on supported hosts.
- [ ] Living docs describe the same grammar and resolution rules; obsolete
  syntax is not silently accepted as a second supported dialect.

Use focused macOS development tests and targeted Windows/Linux checks for native
calling conventions and generated API compatibility. Reserve full-suite testing
for final implementation closure, Windows then Linux then macOS, with valid
cumulative evidence retained. A future implementation handoff will pin the
exact commands/filters and gates; no tests are run as part of drafting.

## Compatibility And Risks

This deliberately changes source acceptance: nonconforming target names,
spec-shaped type-alias names, old callable spec placement, duplicate specs,
generic-instantiation annotations, and declarations colliding with known specs
must be corrected rather than grandfathered. A parameter formerly read as a name
may require explicit `_targettype`, including before its default initializer.

The principal risks are overly greedy declaration parsing, alias resolution
depending on branch selection, loss of explicit default callspec intent, and API
serialization that fails to reproduce parameter names. The acceptance matrix
targets these boundaries. Default markers and syntax recognition do not change
the ABI of otherwise equivalent valid declarations.

No unresolved questions remain from the supplied decisions and maintainer
clarifications. Any further genuine grammar conflict found during
implementation must be raised rather than resolved by reintroducing target-name
knowledge into parsing.
