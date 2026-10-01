# Typed Witness Capabilities With A Shared C Table Layout

## Status

Accepted and implemented in DEV-014. This proposal records the approved design;
current behavior is specified by the living language and semantics guides.

## Proposal Date

2026-10-01

## Last Updated Date

2026-10-01

## Summary

`vtableof(T: I)` supplies a **witness capability** for a concrete or generic type
`T` implementing interface `I`. It does not supply an interface vtable value.
The compiler must preserve that distinction in Camp's type system while emitting
one shared C table layout for the interface contract.

For ordinary instance slots, a witness accepts a `T*` receiver in Camp; an
interface vtable accepts an `I*` interface-instance receiver. Both slot receivers
are represented as `void*` in the shared C layout. Witness constructor slots have
no receiver and return `T*` in Camp, erased to `void*` in C. Witness destructor
slots accept `T*` and reference `destroy`, not `op_delete`.

The C ABI intentionally erases this semantic distinction. Camp checks it before
lowering; C callers must pair the correct table with the correct receiver.
Separate C witness and interface struct declarations are not proposed.

## Motivation And Current Implementation

Source review baseline: `5419799bb3913fe516f09ee4613e5df929ab8912`.
No builds or tests were run to prepare this proposal.

The current implementation mixes two different contracts:

- `BindableNodeAnalyzer.Lowering.VTableOf.cs` assigns interface-pointer-shaped
  types to expressions, capability parameters, and generated fields.
- `BindableNodeAnalyzer.Lowering.Interfaces.cs` casts generic receivers to the
  interface-context shape while dispatching through a capability.
- `BindableNodeAnalyzer.Expansion.cs` uses the same interface slot signatures for
  direct concrete entries and receiver-fixup entries.
- `BindableNodeAnalyzer.MethodBody.cs` has an expression-specific correction for
  raw slots accessed directly through `vtableof(...)`. It substitutes the target
  receiver type without establishing a distinct witness type for all access paths.
- Semantic supplements 06 and 09 describe the capability as `const Interface*`.

The result is misleading metadata, fragile type checking, and casts that obscure
whether a call supplies a concrete object address or an interface context.
The BUG-193 corrections distinguish those dispatch paths operationally, but do
not finish separating their source-level types.

## Goals

- Make witness identity and slot typing structural, not dependent on expression
  spelling or special handling of a direct `vtableof` expression.
- Diagnose incorrect receiver/table combinations before C emission.
- Keep a compact, C-first API with one table struct definition per interface ABI
  shape and direct witness entries wherever no genuine adaptation is necessary.
- Preserve erased generics, existing interface dispatch, lifecycle contracts,
  capability forwarding/storage, and source/API round-tripping.
- Correct the canonical semantic and language documentation together with the
  compiler implementation.

## Non-Goals

- No monomorphization or separate generic body per concrete instantiation.
- No new source keyword, public `Witness` declaration syntax, or runtime type tag.
- No implicit interchange between witnesses and interface tables in Camp.
- No new concrete-class interface overriding or interface reimplementation.
- No redesign of allocation policy, interface carrier lifetimes, or constructors.
- No general permission to cast incompatible Camp callable signatures.
- No portable-ISO-C requirement for the narrowly specified target ABI erasure.
- No automatic generation of two C table types or two table objects for every
  interface declaration.

## Source-Level Contract

### Three distinct values

| Camp form | Meaning | Ordinary slot's explicit receiver |
| --- | --- | --- |
| `I` | Pointer to an interface vtable | `I*` |
| `I*` | Interface-instance pointer, referring to its vtable slot | Bound by ordinary interface call syntax |
| Result/capability of `vtableof(T: I)` | Witness for `T` implementing `I` | `T*` |

The witness type carries both the target type and interface identity. A generic
parameter's identity must survive substitution and forwarding; two unrelated
generic parameters are not interchangeable merely because both erase to `void*`.
Neither interface table values nor interface-instance pointers are witness values.

No extra pointer level is added to the source witness receiver. Ordinary existing
constness, lifetime, calling-convention, parameter-modifier, and callable
compatibility rules still apply; this proposal does not relax them.

### Calls and diagnostics

The following fragments illustrate the proposed contract, assuming compatible
declarations and `concreteInstance` of type `ConcreteType*`:

```camp
// Valid: explicit concrete receiver through a witness slot.
vtableof(ConcreteType: SomeInterface).interfaceMethod(concreteInstance, interfaceArgument);

// Valid: ordinary interface dispatch.
concreteInstance.getSomeInterface().interfaceMethod(interfaceArgument);

// Valid: explicit interface receiver through an interface table slot.
SomeInterface* instanceAsInterface = concreteInstance.getSomeInterface();
SomeInterface table = *instanceAsInterface;
table.interfaceMethod(instanceAsInterface, interfaceArgument);
```

These are diagnostics:

```camp
// A witness slot requires ConcreteType*, not SomeInterface*.
vtableof(ConcreteType: SomeInterface).interfaceMethod(instanceAsInterface, interfaceArgument);

// An interface table slot requires SomeInterface*, not ConcreteType*.
table.interfaceMethod(concreteInstance, interfaceArgument);
```

Explicit unbound slot invocation must not silently materialize an interface view
to make the second invalid call succeed. This restriction applies to the slot's
receiver contract; it does not remove normal concrete-to-interface conversions
elsewhere. Diagnostics should identify the required and supplied receiver types,
not suggest a raw cast that hides the mismatch.

### Parameters, fields, and slot values

The same witness rules apply to:

- concrete and generic `vtableof(...)` expressions;
- compiler-supplied capability parameters and references through their generated
  names, such as `vtableof_T_I`;
- generated retained fields such as `_vtableof_T_I`;
- inferred locals/copies and forwarded arguments wherever already permitted;
- extracted slot function values and calls through those values;
- capability preservation in generated iterator, lambda, and other helper state.

Slot values are unbound `fn` values, not delegates. Extracting an ordinary slot
from `vtableof(T: I)` yields a function with an explicit `T*` receiver. Reading the
same logical slot from an interface table yields a function with `I*` receiver.
Function-value assignment must not lose this distinction. Concrete-slot extraction
may require a C function-pointer cast under the target policy below; it does not
require a new context-bearing callable or allocation.

Retain existing capability naming conventions. The capability is evidence for
dispatch, not element stride, allocation permission, or ownership. `sizeof(T)`
and allocator requirements remain independent.

## Lifecycle And Implementation Selection

### Constructors

Witness constructor slots point to the concrete type's static `create` helper.
They have no receiver parameter. Their Camp result is `T*`, with an erased C
pointer result. Preserve constructor arguments, allocator parameters, failure
channels, and existing restrictions on constructor-bearing interfaces.

An interface-table constructor must continue to produce the interface-construction
result required by its source contract. Converting a concrete creation result to
that interface view may require a real adapter. Sharing C layout does not make
these two result meanings interchangeable or permit a scoped carrier to escape.
Audit existing supported struct/class construction forms before selecting those
entries; do not silently broaden construction or lifetime rules.

### Destructors

Witness destructor slots point to `T`'s `destroy` helper, not `op_delete`.
They accept the ordinary concrete receiver and preserve required allocator
arguments, retained-allocator behavior, and destruction/deallocation semantics.
Interface destructor entries receive an interface context and recover the concrete
receiver before invoking the appropriate destroy path.

Correct documentation that describes these helpers as interchangeable or says
that an interface destroy path necessarily excludes deallocation. Do not add a
second free after `destroy` has already deallocated the instance.

### Ordinary, inherited, defaulted, and optional members

- A witness uses the selected concrete implementation directly when its ABI is
  compatible under pointer erasure. It does not use an interface-offset thunk.
- Ordinary class interface entries recover the concrete receiver from the
  interface slot; struct interface entries recover it from the scoped indirect
  carrier. Existing optimized equivalent paths may remain.
- Interface inheritance retains stable inherited slot order and declaring-slot
  identity. Witness projection substitutes the witness target's receiver while
  respecting that inherited structure. Diamond handling must not duplicate slots.
- Concrete types do not gain interface implementation overriding or
  reimplementation. Preserve existing implementation selection and any permitted
  virtual behavior without introducing new semantics.
- If reaching an inherited implementation genuinely requires an address
  adjustment, an adapter is justified by that adjustment, not by pointer spelling.
- Optional missing slots remain null, with existing optional-call behavior.
- Existing default interface targets have interface-receiver contracts. Proposed
  handling is a witness-side adapter that obtains the correct interface context
  and calls the default. This is a real semantic adaptation. Its carrier lifetime,
  escape requirements, and result lifetimes must be valid. An incompatible
  default cannot simply receive a concrete pointer disguised as an interface
  pointer. Any currently underspecified escaping-default case must be resolved
  with the maintainer before implementation is declared complete.

## Shared C Representation

Emit one named C layout per interface ABI shape, retaining the existing interface
type name where practical. A simplified layout is:

```c
typedef struct SomeInterface SomeInterface;
struct SomeInterface {
    void *(*create)(int argument);
    int (*interfaceMethod)(void *receiver, int argument);
    void (*destroy)(void *receiver);
};
```

This example omits allocator/error parameters, target/call specifiers, and ordinary
ABI expansion only for readability; actual emission must preserve all of them.
Receiver erasure does not erase unrelated argument types or change slot count.

Both a witness pointer and an interface-table value can lower to a pointer to this
layout. An interface-instance pointer still has the extra physical pointer level.
Thus `I*` in Camp continues to be represented as an interface-slot pointer (`I**`
in C), even though an unbound slot's C receiver parameter is now `void*`.

### Shared type does not mean interchangeable table contents

Witness entries and interface fixup entries have different meanings. Where both
are needed, emit their distinct table contents using the same C struct type.
Do not unify objects merely because their layouts match. Conversely, do not emit
unused copies or require a second exported table just because an interface exists.
Public/exported capabilities remain available for separate consumers even if no
local call references them; emission decisions must include export obligations.

Preserve the recent unnamed compound-literal representation for scoped struct
interface vtables. Those tables are not witness storage. Witness capabilities
retained by objects/helpers require appropriately durable backing storage, normally
static concrete witness objects. No new named struct `__object_storage` objects
are needed to implement this proposal.

C header documentation should state the role of exported table values/parameters
and the required receiver convention. No runtime discriminator or distinct C
wrapper type is added; C consumers are responsible for correct pairing.

## Target-Specific Function-Pointer Policy

The backend targets specific compiler/platform configurations, not arbitrary ISO C
implementations. For validated configurations, it may cast implementation function
pointers to the shared erased slot types, including pointer return erasure for
constructors, without generating erasure-only thunks.

This permission is limited to ABI-compatible pointer substitutions:

- same parameter count and expansion, calling convention, and return mechanism;
- compatible object-pointer representation/address-space requirements;
- no scalar-width, aggregate-return, variadic, or hidden-argument mismatch;
- source receiver/result correctness already established by Camp's type system.

Do not claim all MSVC, Clang, or GCC versions/options are automatically compatible.
Validate the actual supported target configurations, including optimized native
builds. The target inventory includes non-default architectures/toolchains; list
those not validated rather than extending a desktop result to every target.

Clang's function sanitizer and indirect-call CFI can enforce stricter function
type identity. The supported build-policy decision for such configurations must
be documented: reject/declare them unsupported for this ABI policy, or separately
approve compatible handling. Do not silently disable user-requested instrumentation
or introduce a large thunk subsystem as part of this proposal.

References:

- [C11 draft, sections 6.3.2.3 and 6.5.2.2](https://www.open-std.org/jtc1/sc22/wg14/www/docs/n1570.pdf):
  explains why this is a target-specific backend contract, not portable C.
- [Clang UBSan](https://clang.llvm.org/docs/UndefinedBehaviorSanitizer.html):
  `function` checks indirect function type mismatches.
- [Clang CFI](https://clang.llvm.org/docs/ControlFlowIntegrity.html):
  indirect-call checks can constrain otherwise ABI-compatible erasure.

## Compiler Integration

Implement an explicit semantic witness type holding target and interface identity.
An internal type node/service is preferable to another special resolved-type string
or a test for the syntactic form of the receiver. No public type syntax is required.

The coherent change includes:

1. Type binding, substitutions, compatibility, member lookup, and diagnostics.
   Preserve witness identity through parameters, fields, expressions, and slots.
2. Capability insertion/forwarding and generated storage. Stop deriving capability
   meaning from an ordinary interface-pointer type; retain source relationships
   until ABI lowering.
3. A shared interface contract model with two semantic slot views: interface and
   witness. Both map to one C layout. Lifecycle slots must be modeled by category,
   not by blindly replacing every first parameter.
4. Dispatch lowering. Separate interface-context calls from witness calls and
   remove generic-receiver casts to `Interface**`. Preserve receiver evaluation
   exactly once and all default/capability/expanded arguments.
5. Table initialization and slot extraction. Select direct implementation or
   necessary adaptation explicitly; preserve null/default/inherited slots.
6. C emission, generated C headers, Camp API headers, import reconstruction, export
   visibility, and metadata. Remove special cases that disguise a witness as an
   interface pointer, including the literal-expression slot-type repair.

Starting owners are `BindableNodeAnalyzer.Lowering.VTableOf.cs`,
`BindableNodeAnalyzer.Lowering.Interfaces.cs`, `BindableNodeAnalyzer.Expansion.cs`,
`BindableNodeAnalyzer.MethodBody.cs`, `CallableShapeService.cs`, capability handling
in iterator/helper expansion, `CCodeEmitter.cs`, `MetadataJsonSerializer.cs`, and
the export/API reconstruction paths. This is a semantic change across those
owners, not just a replacement of C type text.

## API And Compatibility

Camp API output must retain `vtableof(T: I)` and its relationship to generic type
parameters. Metadata must identify a witness capability, its target type, and its
interface; reporting only the erased C pointer is insufficient. Existing metadata
fields can be extended where appropriate without prescribing a new schema here.
Imported values must regain the same semantic distinctions as locally declared ones.

Use the same C layout declaration for both roles. Keep existing stable public
symbols where their contract remains appropriate; record any required symbol or
schema changes explicitly. Do not assume layout equality guarantees source/API
compatibility: C function-pointer declarations change, and formerly accepted
Camp receiver mistakes become errors. Rebuild dependent generated headers,
libraries, and consumers together; do not promise mixed-version compatibility.

## Canonical Documentation Changes

Update these together with the implementation:

- `docs/semantics/06-generics-erasure-and-capabilities.md`: witness identity,
  erasure, forwarding/retention, and generic dispatch; remove interface-pointer
  capability claims and generic-to-interface-context casts.
- `docs/semantics/09-interface-vtables-and-dynamic-dispatch.md`: distinguish all
  three source forms, shared C layout, ordinary/default/inherited/lifecycle slots,
  and examples of invalid receiver pairing.
- Relevant sections of supplements 02, 03, 04, 05, 07, 08, and 12: representation,
  conversions, callable compatibility, carrier lifetime, lifecycle and target ABI.
  Change only claims affected by this contract.
- `docs/language/12-interfaces-and-dynamic-dispatch.md` and the generics/callables
  guide sections: explain practical use without presenting witness capabilities
  as bare interface values.
- Relevant compiler API/metadata documentation and `docs/camp-llm-coding-guide.md`:
  preserve witness typing and prohibit mixing the two receiver conventions.

Do not update historical accepted proposals to rewrite past decisions. Current
canonical documentation owns the corrected behavior.

## Verification And Completion Criteria

Prefer extending coherent existing fixtures over creating a new test for every
variation. Existing starting points include `vtableof_generic_dispatch`,
`vtableof_raw_slot_concrete_receiver_runtime`, `vtableof_raw_slot_interface_receiver`,
`generic_interface_constraint_forwarding_runtime`,
`generic_interface_dispatch_struct_receiver_runtime`,
`generic_interface_constructor_construction_runtime`, and
`inherited_interface_generic_dispatch`. Audit fixtures such as
`vtableof_slot_source_abi_mismatch`: an expected runtime result must not preserve
an invalid source-level receiver assignment merely because erased C permits it.

- [x] Direct and generic witness calls, interface calls, and explicit interface
  table calls produce correct results; both wrong-receiver directions diagnose.
- [x] Extracted slot types remain correct through concrete/generic expressions,
  named parameters, stored fields, forwarding, and API round-trips.
- [x] Constructors have no receiver and the proper result type; destroy slots
  preserve allocator and deallocation behavior without invoking `op_delete` as
  a substitute or freeing twice.
- [x] Default/optional slots, interface inheritance, and existing permitted
  concrete inheritance work without new reimplementation/override semantics.
- [x] C output uses one table struct definition per interface shape, erased
  receiver/result pointers where specified, direct entries where compatible,
  and no erasure-only thunks on validated configurations.
- [x] Scoped struct-interface compound literals and durable retained witness
  storage each obey their own lifetime requirements.
- [x] Exported/static/shared producer-consumer cases preserve the distinction
  through Camp APIs and metadata, and the shared C header is usable by a C caller.
- [x] Canonical docs no longer describe witnesses as interface-vtable values.
- [x] Target/toolchain support and remaining instrumentation restrictions are
  stated explicitly, not inferred from a single host result.

Use focused local development tests, then focused optimized native and
producer-consumer verification with Clang on macOS, MSVC on Windows, and GCC on
Linux. Cover supported ABI-width differences where applicable. A later execution
plan should select the final broader regression gate for the shared binder,
lowering, API, and stdlib dependency impact. Do not run full suites after each
small fix; reuse valid passing evidence and repeat only invalidated tests.

## Alternatives And Decisions To Finalize

Rejected design directions: a separate C witness struct family; treating witness
capabilities as interface pointers in Camp; unconditional adapters solely for
pointer erasure; or reusing the same table contents where different receiver
interpretations require different functions.

Before dependent implementation changes are finalized, resolve:

1. The exact default-target adapter policy when the default has escape-sensitive
   receiver/result contracts. Preserve valid existing behavior without escaping
   a temporary carrier.
2. The validated target/compiler-option envelope for function-pointer erasure,
   including sanitizer/CFI treatment and non-default target coverage.
3. Any necessary exported symbol/metadata compatibility changes, while retaining
   a single C layout and existing capability names wherever possible.

These are bounded implementation/compatibility decisions. They do not reopen the
core direction: distinct Camp semantics, shared erased C layout, and no redundant
erasure-only thunks on supported target configurations.
