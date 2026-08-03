# Default Prepared Calls And `toString` Formatting

## Status

Accepted and implementation-complete.

Active language guidance now lives in the language guide, compiler-writer
semantics live in the semantic supplements, and command-line/tooling behavior
lives in the compiler docs. This proposal is retained as the accepted design
record.

## Proposal Date

2026-08-02

## Last Updated Date

2026-08-02

## Depends On

- Caller-Prepared Return Buffers.
- Eager String Interpolation Lowering.
- Interpolated Strings And Formatter Composition.
- Late Overload Selectors.

## Summary

This proposal makes the prepared array result the default result of calling a
function with an omitted `prep` parameter.

Today, an ordinary call uses the declaration's scalar return type, while the
`prep` expression prefix removes the prepared buffer from the source call and
produces the prepared array:

```camp
nuint required = value.format();
char[] text = prep value.format();
char[] owned = prep new value.format();
```

Under this proposal, written arguments first bind against the complete declared
signature. If no written argument binds to the `prep` parameter, the call uses
the transformed prepared signature automatically:

```camp
auto text = value.toString();
auto owned = (new) value.toString();

nuint required = value.toString(buffer: default);
char[] buffer = init char[required];
value.toString(buffer: buffer);
```

This proposal also:

- removes the `prep` expression prefix and `prep new` expression form;
- introduces `(new)` as the heap-allocation modifier for a direct transformed
  prepared call;
- makes prep-bearing methods ineligible for successful property accessor
  syntax;
- makes `prep` a contextual parameter-modifier keyword rather than a globally
  reserved keyword;
- renames the canonical UTF-8 formatting convention from `format` to
  `toString`;
- changes bare interpolation-hole formatter lookup from `format` to
  `toString`;
- preserves the declared prep signature in callable types, API headers, and
  metadata;
- preserves all existing prepared-result conversions, including terminator
  behavior.

The proposal changes source binding and expression syntax. It does not change
the prep ABI or the two-call behavioral contract.

## Motivation

### Prepared Arrays Are The Logical Result

A prep-bearing function returns a scalar length so the caller can allocate a
buffer, but that scalar is protocol machinery. The logical result is the
prepared array.

Requiring a `prep` prefix for the common result makes ordinary use noisier and
creates an inconsistency with interpolation, where prep formatting already
binds through the transformed shape without a prefix:

```camp
auto text = prep count.format();
```

The corresponding interpolation hole already binds `count.format` through the
prepared formatter protocol without a source `prep` prefix.

Making omission of the prep buffer select the transformed result gives the
same function one consistent source-level meaning in ordinary expressions and
interpolation:

```camp
auto text = count.toString();
```

The same `toString` declaration is also the implicit formatter for a bare
interpolation hole containing `count`.

The explicit-buffer protocol remains available and visible whenever the caller
wants to control storage.

### Property Syntax Should Remain Field-Like

Property access is intended for lightweight, field-like operations. A
prep-bearing getter can size an input-dependent result, allocate storage, call
the getter again, and copy data. Repeating such a property can hide substantial
work:

```camp
control.Text.trim();
control.Text.startsWith("ready");
```

Requiring explicit method-call syntax exposes the operation and encourages the
caller to retain the result when it will be reused:

```camp
auto text = control.getText();
if (text.trim().startsWith("ready"))
{
}
```

## Goals

- Make an omitted prep buffer produce the prepared array by default.
- Preserve explicit-buffer calls as ordinary single calls returning the
  declaration's scalar length type.
- Define a deterministic binding order based on the untransformed declaration.
- Preserve Camp's selector-driven overload model without adding result-based
  candidate ranking.
- Provide a clear heap-allocation spelling for prepared results.
- Prevent property syntax from hiding prep allocation and repeated work.
- Make `prep` contextual after its expression form is removed.
- Establish `toString` as the canonical UTF-8 formatting convention.
- Preserve eager interpolation and direct-to-final-buffer formatter lowering.
- Preserve callable, interface, virtual, generic, lifetime, `within`,
  `constof`, `thrown`, API, metadata, and ABI protocols.
- Define the compiler, standard-library, test, documentation, and editor work
  needed for a complete implementation.

## Non-Goals

- Do not add implicit conversion of arbitrary values to text.
- Do not change the prep two-call behavioral contract.
- Do not guarantee an exact number or ordering of prep protocol calls.
- Do not redefine the existing prepared-result string conversions or their
  terminator rules.
- Do not add UTF-16 or system-code-page interpolated string results.
- Do not require a particular signature or return type merely because a method
  is named `toString` outside the interpolation formatter protocol.
- Do not make prep-bearing methods valid properties.
- Do not change prep callable compatibility, API-header representation,
  metadata representation, or ABI expansion.
- Do not introduce compatibility aliases or migration diagnostics for the
  experimental preview language.

## Current Compiler Baseline

The current compiler implements caller-prepared results as described by the
active semantic supplements:

- `prep` is a parameter modifier and an expression prefix;
- `prep call` produces scoped `init`-like array storage;
- `prep new call` produces heap storage under the active `within` policy;
- an ordinary omitted-buffer call uses the scalar return and may consume the
  prep parameter's default;
- prep-bearing getter methods can participate in property syntax;
- interpolation discovers eligible `prep char[]` methods named `format` and
  can write them directly into the final interpolation buffer;
- method references and callable metadata retain the declared scalar return and
  prep parameter;
- prep is part of callable compatibility and survives interface, virtual, API,
  and metadata surfaces.

The main implementation anchors are:

- `src/Camp.Compiler/CampParser.cs`;
- `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs`;
- `src/Camp.Compiler/BindableNodeAnalyzer.Declarations.cs`;
- `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.cs`;
- `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs`;
- `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.PreparedBuffers.cs`;
- `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.InterpolatedStrings.cs`;
- `src/Camp.Compiler/CallableShapeService.cs`;
- `src/Camp.Compiler/CampSymbolService.cs`;
- the prep, interpolation, API, metadata, diagnostics, runtime, and LSP tests
  under `tests` and `src/Camp.Compiler.TestRunner`.

This proposal changes that behavior deliberately. The active documentation must
be updated with the implementation; the earlier accepted proposals remain
historical design records and should not be edited into living specifications.

## Terms

This proposal uses these terms:

- **declared signature** or **untransformed signature**: the source callable
  signature containing the scalar return and the `prep` parameter;
- **full call**: a call in which a written argument binds to the prep parameter;
- **transformed call**: a call in which the prep parameter is omitted and the
  expression produces the prep array type;
- **prepared result**: the logical mutable array produced by the two-call prep
  protocol;
- **canonical formatter**: the prepared UTF-8 `toString` method discovered for
  a bare interpolation hole.

## Prep Declaration Rules

### Existing Shape Rules Remain

The existing prep declaration contract remains in force:

1. A callable may declare at most one prep parameter.
2. The prep parameter is an ordinary mutable array parameter.
3. Its element type must be copyable.
4. The callable return type exactly matches the array length component type,
   including target specs.
5. Prep cannot be combined with `in`, `out`, `thrown`, `overload`, `within`, or
   a compiler capability parameter form.
6. `once` callable signatures cannot contain prep parameters.
7. Prep remains part of callable compatibility and source API metadata.

The prep parameter may retain a declared default value. Under the new call
rules, that default does not make an omitted prep slot into a full scalar call.
Omission always selects transformed mode. Callers that want the scalar mode
write an argument for the prep slot, commonly `default`.

### Behavioral Contract Remains

Declaring prep continues to assert the existing behavioral contract. For the
same receiver and non-prep arguments:

1. The scalar return is the minimum array length required for the complete
   logical result.
2. A supplied buffer receives the first `min(buffer.length, required)` logical
   result elements.
3. Required size and logical contents are stable across protocol calls.
4. The callee does not write outside the supplied array.
5. Failure depending only on the receiver and non-prep arguments occurs during
   sizing.
6. Terminators, sentinels, and padding are excluded unless they are part of the
   logical result.

The compiler validates the declaration shape but cannot generally prove this
runtime behavior. The contract permits the compiler and caller to issue the
size and write calls required by a use; it does not promise the prep
implementation an exact source-observable call count or ordering.

### Parameters After Prep

Every parameter after the prep parameter must be satisfiable without an
ordinary required caller argument. A following parameter is valid when it:

- has an ordinary default value; or
- is supplied implicitly under existing Camp call rules, such as a `within`
  slot or a `sizeof(T)`, `typenameof(T)`, or `vtableof(T: Interface)` capability;
  or
- is another established hidden call slot, such as a propagated `thrown` slot,
  whose omission is already handled by the ordinary call binder.

A required ordinary parameter or required `out` parameter cannot follow prep.
The validator should use the compiler's shared parameter-shape and implicit-slot
classification rather than maintaining a separate list in lowering.

Examples:

```camp
nuint render(
	Style style,
	prep char[] buffer,
	bool uppercase = false,
	sizeof(Style));
```

is valid, while this is invalid:

```camp
nuint render(prep char[] buffer, Style requiredStyle);
```

This restriction guarantees that transformed binding never needs to reinterpret
a positional argument after discovering that the prep slot was omitted.

### Overload Selectors Necessarily Precede Prep

The prep parameter and overload selector modifiers are mutually exclusive. An
overload selector cannot have a default value. Under the post-prep restriction,
the selector therefore cannot follow prep and must occur before it:

```camp
nuint toString(overload IntegerFormat format, prep char[] buffer = default);
```

This lets the compiler select the concrete overload entry before deciding
whether the selected declaration is called in full or transformed mode.

## Default Transformed Call Binding

### Two Call Modes

A call through a prep-bearing source surface has two modes:

1. **Full mode.** At least one written argument binds to the prep parameter.
   The call uses the complete declared signature and its declared scalar return
   type. The compiler performs no prepared allocation.
2. **Transformed mode.** No written argument binds to the prep parameter. The
   effective call shape omits that parameter and the expression's intrinsic
   result type is the substituted mutable array type of the prep parameter.
   The compiler performs the prep size/allocate/write protocol.

The declaration's prep default value does not select the mode. A written
`default` is still a written argument and selects full mode if it binds to the
prep slot.

### Normative Binding Order

Body analysis must use this order:

1. Perform ordinary name/member lookup and receiver binding against declared
   callable surfaces.
2. For an overload family, identify the selector at its declared callable
   position and select the concrete entry from the selector argument's
   independent type. Family-shape rules and the declaration restrictions above
   ensure that this occurs before the prep slot.
3. Bind every caller-written positional, named, `out`, `catch`, `within`, and
   capability argument to a slot in the selected untransformed declaration.
4. Record which declared slots were explicitly supplied.
5. If the prep slot was supplied, bind a full call and use the scalar result.
6. If the prep slot was not supplied, validate that every other omitted slot is
   defaultable or supplied under existing compiler rules, bind a transformed
   call, and use the prep array result.
7. Apply ordinary result-target conversion after the call mode and intrinsic
   result type are known.

Analysis must record the selected callable, generic and `constof`
substitutions, argument-to-parameter mapping, call mode, prep parameter, result
type, lifetime facts, and allocation mode. Lowering consumes those decisions.
It must not rediscover the mode from argument count, result use, or `.length`.

### `default` Is Unambiguous

Given:

```camp
nuint render(prep char[] buffer, Style style = default);
```

this is a full call:

```camp
nuint required = value.render(default);
```

The written `default` binds to `buffer` in the declared signature. The compiler
does not remove `buffer` first and reinterpret the argument as `style`.

To omit the buffer while supplying a later defaultable parameter, use its name:

```camp
auto text = value.render(style: Style.COMPACT);
```

To state the scalar measure call explicitly, name the buffer:

```camp
nuint required = value.render(buffer: default);
```

### Applicable Callable Surfaces

Default transformation applies at invocation time through every callable
surface that exposes the prep contract:

- free, static, instance, and extension functions;
- generic functions and generic constraint members;
- interface and virtual calls;
- calls through `fn`, `delegate`, callable newtypes, and other compatible
  callable values;
- declarations imported from Camp API headers.

Constructors, `once` signatures, and other callable kinds already prohibited
from declaring prep remain ineligible. Async and iterator surfaces continue to
follow their existing declaration restrictions.

A method reference does not transform merely because it is read:

```camp
auto method = value.toString;
```

The reference retains the declared scalar return and prep parameter. Calling
that reference with the prep slot omitted is the point where transformed call
binding occurs.

### Result Type And Existing Conversions

The transformed expression's intrinsic type is the substituted mutable prep
array type. `auto` preserves that intrinsic type:

```camp
auto text = value.toString(); // char[] for prep char[]
```

An assignment, argument, return, cast, conditional arm, or initializer target
may then apply an existing compatible conversion. This proposal does not
change any array-to-string or string-family conversion, including whether and
where a terminator is allocated or written.

## Measure-Only `.length`

The canonical prepared-size expression is:

```camp
auto required = value.toString().length;
```

These expressions bind normally: the call is transformed, its intrinsic result
is the prep array, and `.length` selects the array's length component.

When the prepared elements are not observable, lowering may replace the
ordinary size/allocate/write sequence with one full protocol call using a
default prep buffer and use its scalar return as the length. The common
immediate `.length` shape should be recognized as the primary optimization
case, but the language does not promise that every equivalent data-flow shape
will be optimized.

This is an optimization, not a different binding mode. If it is not applied,
the ordinary transformed call still produces the same `.length` value. The prep
contract does not promise callers an exact call count or ordering, so the
optimization does not require a new purity or side-effect restriction.

When the caller requires an explicit one-call size query, the caller uses the
full method name and supplies the buffer slot:

```camp
nuint required = value.toString(buffer: default);
```

That spelling is guaranteed to be one ordinary full call because the prep slot
is explicitly bound.

## Prepared Heap Allocation With `(new)`

### Syntax And Meaning

`(new)` is a cast-like prepared-allocation modifier:

```camp
auto owned = (new) value.toString();
auto arenaText = within (arena) (new) value.toString();
```

Its operand must bind directly to a transformed prep call. `(new)` changes only
the result storage from scoped `init`-like storage to ordinary `new` storage.
Receiver binding, overload selection, arguments, dispatch, prep result type,
and result conversion remain unchanged.

The allocation uses the active `within` policy exactly like other `new`
expressions. No new allocator-selection rule is introduced.

### Precedence And Ownership

`(new)` has unary/cast-like precedence and consumes its postfix operand. The
operand must still be the direct prepared result. These forms are valid:

```camp
auto owned = (new) value.toString();
within (arena) (new) value.toString() finally delete;
```

These forms are invalid:

```camp
(new) value.toString().length;
(new) value.toString().toUppercase();
(new) value.toString()[2..5];
(new) value.toHexString().toLowercase();
```

In each invalid form, the appended postfix operation makes the operand something
other than the direct prepared result. Accepting the form would lose the owning
reference and leave no reliable cleanup target. `finally delete` is different:
it registers cleanup for the direct owning result and is therefore valid.

`(new)` is not a general replacement for ordinary `new`. Type construction and
allocated interpolated strings retain their existing syntax.

### Lifetime

A transformed call without `(new)` has the same scoped lifetime as equivalent
`init` array storage. A `(new)` transformed call has ordinary allocated lifetime
and cleanup obligations. Existing escape, capture, return, yield, async-frame,
iterator-frame, and deletion rules apply without a new lifetime category.

## Property Accessor Ineligibility

A prep-bearing function cannot be used successfully through getter, setter,
indexed-property, or nameless-indexer syntax.

Given:

```camp
class Control
{
	nuint getText(prep char[] buffer = default)
	{
		// prepare the control text
	}

	void setText(const char[] text)
	{
	}
}
```

call the prep method explicitly:

```camp
auto text = control.getText();
control.setText(count.toString());
```

This property-shaped use is invalid:

```camp
auto text = control.Text;
```

Property lookup should still resolve the prep-bearing accessor candidate far
enough to recognize the programmer's intent. Body analysis then reports a
focused diagnostic that prep methods require explicit method-call syntax. It
must not degrade to a generic missing-member diagnostic.

The candidate is never accepted as a property access and must not be emitted as
property metadata. This applies consistently to named getters, indexed getters,
nameless indexers, extension accessors, interface accessors, and virtual
accessors. Setter exclusion is stated explicitly for consistency even though a
valid prep function's scalar length return is already incompatible with the
ordinary `void` setter shape.

The existing implicit const-receiver convention for `getX` methods remains a
receiver rule. Removing property syntax must not silently make an explicit
`getX()` call require a mutable receiver.

## Contextual `prep`

After the expression prefix is removed, `prep` is contextual only in parameter
declarations. It is otherwise available as an identifier or type name.

The parameter grammar is deterministic:

```camp
struct prep
{
}

void consume(prep value);
void fill(prep char[] buffer);
```

In `consume`, `prep` is the type and `value` is the parameter name. The modifier
reading requires a complete following type and parameter name, as shown by
`prep char[] buffer` in `fill`.

The parser should use its ordinary tentative type/parameter parsing rather than
a naming convention to disambiguate these forms. PascalCase type names make the
edge case uncommon, but capitalization is not part of the grammar.

Tokenization does not need a special keyword token. Syntax highlighters should
recognize `prep` in the parameter-modifier context and stop highlighting it as
a general expression keyword.

Legacy `prep call` and `prep new call` source receives ordinary parsing or
binding diagnostics under the new grammar. No focused migration diagnostic is
required for the experimental preview language.

## `toString` Formatting Convention

### Canonical UTF-8 Formatter Name

The canonical UTF-8 formatting convention becomes `toString` rather than
`format`:

```camp
public nuint toString(
	in int this,
	IntegerFormat options = default,
	prep char[] buffer = default);
```

This is a convention and an interpolation lookup name. The compiler does not
reserve the method or impose a general signature requirement on every method
with that name.

Arbitrary prep methods remain transformed by omission regardless of their
names:

```camp
auto bytes = packet.serialize();
auto hex = value.toHexString();
```

Likewise, explicitly selected alternate text representations can keep distinct
names. This proposal does not turn every textual representation into an
overload of `toString`.

### Interpolation Lookup

A bare non-text UTF-8 interpolation hole searches for an eligible prepared
formatter named `toString` instead of `format`. A hole containing `total`, for
example, binds the eligible `total.toString` formatter when `total` is not
already direct text.

Eligibility otherwise remains the current interpolation prep protocol: the
selected method has an eligible mutable `prep char[]` buffer, exact length
return, compatible receiver/lifetime behavior, no unsupported error path, and
no required caller formatting argument that the bare hole cannot supply.

An explicit eligible prep method call remains usable as a formatter part
regardless of its name:

```camp
total.toString(IntegerFormat.CURRENCY)
checksum.toHexString()
```

When those expressions appear directly in interpolation holes, interpolation
uses them as the selected prepared formatter parts.

An explicit ordinary non-prep call that already returns direct text is evaluated
once and used as an ordinary direct-text hole. Making ordinary allocating
`toString()` methods eligible for implicit bare-hole formatter discovery would
be a separate change; this proposal retains the prepared implicit formatter
protocol.

## Interpolation Interaction

For UTF-8 interpolation, a bare hole and a hole containing an explicit
parameterless `toString()` call are semantically equivalent when they select the
same eligible prep formatter. A bare hole with formatting options is not
invented; callers put the explicit `toString(options)` call in the hole.

Body analysis must retain the selected prep formatter call as a prepared
interpolation part. Interpolation lowering then:

1. evaluates the hole receiver and explicit formatting arguments once, in
   source order;
2. uses the formatter during interpolation sizing;
3. passes the final interpolation destination slice during writing;
4. avoids materializing an intermediate transformed array for the hole.

The general transformed-call lowering must not run first and erase the prepared
formatter identity. Doing so would add an allocation and copy, increase stack
use, duplicate protocol work, and undo the existing eager interpolation design.

Direct `string`, character-array, fixed-character, character, and constant-text
holes retain their existing direct paths. This proposal does not add constant
evaluation of arbitrary user method calls.

Interpolation remains UTF-8 only.

## Evaluation, Lowering, And Allocation

### Transformed Prepared Calls

For a transformed prep call, lowering preserves the current protocol:

1. evaluate and capture the receiver when needed;
2. evaluate and capture each caller-written non-prep argument once, in source
   order;
3. obtain defaults, generic capabilities, `constof` substitutions, `within`
   context, dispatch target, and explicit error handling from the recorded call
   analysis;
4. call the selected callable with a default prep buffer to obtain `required`;
5. validate allocation arithmetic and allocate the target storage;
6. call the same selected callable with the allocated buffer;
7. produce the prepared array view and apply any existing target conversion.

The second scalar return is not the expression result. Under the prep contract,
it agrees with the sizing result.

Interface and virtual calls dispatch through the statically selected prep
surface for both protocol calls. Calls through a non-prep interface or callable
surface do not transform merely because a concrete implementation has a
stronger prep contract.

### Full Calls

A full call is one ordinary call. It does not allocate prepared storage and does
not perform an implicit sizing pass:

```camp
value.toString(buffer: destination);
```

All ordinary argument, dispatch, `thrown`, `catch`, `out`, `within`, generic,
and lifetime rules apply.

### Error Handling

The compiler supplies only what existing call rules already supply. It does not
silently catch errors, discard `out` values, or invent an allocator context for
transformed calls.

When a transformed prep call has an explicit or propagated thrown slot, the
recorded handling is reused for both protocol calls exactly as under the current
prep lowering. The prep behavioral contract continues to require failures that
depend only on the receiver and ordinary arguments to occur during sizing.

### Overflow And Failure

Required-length arithmetic, terminator addition required by an existing result
conversion, element-size multiplication, and allocation must use the target's
ordinary checked bounds behavior. Runtime overflow must fail before allocation
or writing. This proposal adds no new thrown result or recovery protocol.

## Callable, Generic, Interface, And Metadata Surfaces

The declared callable contract remains source-shaped:

```camp
fn nuint(prep char[]) formatter;
```

Prep compatibility direction is unchanged. A prep callable may satisfy an
ordinary mutable-array callable because callers may ignore the stronger
guarantee. The reverse conversion still requires an explicit unsafe conversion.

API headers continue to emit the scalar return, `prep` modifier, declared
parameter order, and any default value:

```camp
public nuint toString(
	in int this,
	IntegerFormat options = default,
	prep char[] buffer = default);
```

Metadata continues to record `modifier: "prep"` on the source parameter. It
does not emit synthetic transformed overloads or generated sizing/write
functions.

Tooling should distinguish the declaration from the call expression:

- definition, hover, method reference, API, and metadata views show the
  declared scalar/prep signature;
- call-site type information shows the prep array for transformed mode and the
  scalar for full mode;
- signature help shows declared parameter slots and identifies that omitting
  prep transforms the call;
- interface, virtual, and callable navigation resolves to the statically
  selected source surface.

Generic prep arrays keep all existing element copyability, stride,
`sizeof(T)`, `vtableof`, `typenameof`, `constof`, and target-length
requirements. The compiler must pass the same captured capability values to
both generated calls.

## Standard Library Changes

### Complete Standard-Library `format` Rename

Every public standard-library function named `format` becomes `toString`. This
is an exhaustive rename, not only a change to interpolation lookup or a subset
of canonical formatters.

At the current compiler baseline, this includes the character, character-array,
primitive `string`, boolean, integer, `TimeSpan`, `UtcOffset`, `Instant`,
`Date`, `TimeOfDay`, `DateTime`, and `OffsetDateTime` functions declared in
`std_format.camp` and `std_time.camp`.

Every standard-library call to those functions, including calls from other
`toString` implementations and forwarding code, must be updated. Public
documentation comments, interpolation tests, runtime tests, metadata snapshots,
API snapshots, language examples, LSP fixtures, and generated standard-library
API documentation must be renamed in the same change. No public standard-library
function named exactly `format` remains after the migration, and no temporary
`format` alias is added.

Alternate representation methods remain distinct. For example, a hexadecimal
representation may use `toHexString` rather than becoming an unrelated
`toString` overload.

## Diagnostics

Required declaration diagnostics include:

- a non-defaultable, non-compiler-supplied parameter follows prep;
- an overload selector appears after prep;
- all existing invalid prep declaration shapes.

Required call-site diagnostics include:

- full/transformed binding leaves a required non-prep argument unsatisfied;
- an explicit argument for the prep slot has the wrong type or modifier;
- transformed prep storage cannot appear in the source lifetime/context;
- `(new)` does not directly target a transformed prep call;
- an appended postfix chain after `(new)` would lose the owning prepared
  result;
- `(new)` lacks a required explicit allocation context under the current
  `within` policy;
- property syntax resolves to a prep-bearing accessor and must use explicit
  method-call syntax;
- interpolation finds no eligible `toString` prep formatter or finds an
  ambiguous formatter;
- interpolation selects an incompatible prep buffer, return, thrown, receiver,
  or lifetime shape.

Diagnostics must use source provenance recorded during analysis. They should
point at the responsible prep parameter, written argument, property access,
`(new)`, call, or interpolation hole rather than at a generated temporary
or generated protocol call.

No diagnostic should suggest the removed `prep` expression syntax as a fix.

## Compiler Implementation Strategy

### Syntax And Bindable Model

- Remove `prep` and `prep new` unary-expression parsing.
- Parse `prep` contextually in complete parameter declarations.
- Add a dedicated `(new)` prepared-allocation syntax/bindable node or an
  equivalent recorded unary operation that preserves its source range and
  direct-result constraint.
- Update syntax serializers, syntax dumps, source formatters, and expression
  visitors for the new nodes.

### Declaration Analysis

- Retain existing prep shape validation.
- Validate that parameters following prep are defaultable or use the shared
  compiler-supplied/hidden-slot classification.
- Ensure overload selector validation demonstrates that the selector precedes
  prep.
- Remove successful prep property metadata classification while retaining
  enough candidate information for a targeted diagnostic.

### Call Binding

- Refactor direct and callable-value call binding to associate arguments with
  the complete selected declaration before testing prep omission.
- Record full versus transformed mode explicitly.
- Reuse shared overload-family and selector-position facts; do not add
  transformed-result ranking.
- Apply the same mode decision to direct, extension, generic, callable-value,
  interface, and virtual calls.
- Keep method references declaration-shaped.

### Lowering

- Reuse the current prepared-buffer size/allocate/write lowering for transformed
  mode without requiring a source prefix node.
- Preserve receiver and argument evaluation once.
- Consume recorded substitutions, dispatch, defaults, capabilities, error
  handling, lifetime, target conversion, and allocation mode.
- Add `(new)` allocation selection and direct-owner validation.
- Add best-effort immediate `.length` measure-only lowering.
- Preserve prepared interpolation parts for bare `toString` holes and explicit
  prepared formatter calls so they write directly into the final destination.

### Metadata, API, LSP, And Editors

- Keep prep declaration serialization unchanged except for the formatter rename
  and property metadata removal.
- Update API and metadata golden files for `format` to `toString`.
- Update hover, completion, signature help, semantic tokens, and diagnostics for
  transformed calls and contextual `prep`.
- Update Sublime, micro, Vim, Fresh, and VS Code grammar assets so `prep` is
  contextual.
- Keep protocol handling in shared compiler/language-service facts rather than
  duplicating semantic decisions in the LSP transport layer.

### Standard Library

- Rename every public standard-library `format` function and every internal
  call to it to `toString`.
- Update formatter documentation comments.
- Migrate library and test call sites to transformed calls.

## Test Surface

Use the smallest focused set that covers each independent rule, then run the
full non-skipped suite before the final implementation commit. Existing prep
and interpolation tests should be migrated when they cover the same behavior;
new tests should be added for genuinely new cross-products.

### Tokenizer, Parser, AST, And Syntax Dumps

Cover:

- `(new) value.toString()`;
- `within (arena) (new) value.toString() finally delete`;
- invalid owner-losing `(new)` chains;
- `prep` as a modifier, type name, parameter name, local, member, and ordinary
  identifier;
- removal of `prep call` and `prep new call` expression nodes;
- stable source ranges for `(new)` and contextual `prep`.

### Declaration And Signature Diagnostics

Cover:

- legal prep-last declarations;
- legal defaultable ordinary, `within`, thrown-propagation, and capability slots
  after prep under existing rules;
- invalid required ordinary and required `out` slots after prep;
- overload selector before prep;
- invalid selector after prep and prep/selector modifier combination;
- prep defaults preserved in API and metadata;
- `prep` type-name disambiguation without relying on capitalization;
- callable, interface, virtual, override, and ascription compatibility remains
  directional and declaration-shaped.

### Full-Signature-First Binding

Cover direct, extension, generic, callable-value, interface, and virtual calls
for:

- omitted prep buffer selecting transformed mode;
- explicitly supplied positional buffer selecting full mode;
- named `buffer:` selecting full mode;
- written `default` binding the prep slot before transformation;
- later named defaultable arguments with the prep slot omitted;
- prep parameters with and without declared defaults;
- explicit `catch`, propagated thrown slots, `within`, and generic capability
  arguments;
- overload selector binding before the prep decision;
- receiver, selector, and all explicit arguments evaluated once;
- method references remaining scalar/prep callables until invoked.

At least one focused case should use:

```camp
nuint render(prep char[] buffer, Style style = default);

nuint required = value.render(default);
auto text = value.render(style: Style.COMPACT);
```

and assert the two different call modes.

### Property Binding

Cover:

- named, indexed, nameless, extension, interface, and virtual prep getter
  attempts producing the focused explicit-call diagnostic;
- explicit `getX()` calls transforming normally;
- getter-style const receiver behavior remaining available;
- prep functions absent from successful property metadata;
- ordinary non-prep properties remaining unchanged;
- setter exclusion remaining consistent.

### Lowering, C Emission, And Runtime

Cover:

- transformed scoped size/allocate/write lowering;
- `(new)` heap allocation under default and explicit `within` contexts;
- `finally delete` cleanup of a direct `(new)` prepared result;
- full explicit-buffer calls producing one call and no hidden allocation;
- immediate `.length` using the measure-only optimization in the canonical
  lowering/C-emission case;
- explicit `buffer: default` providing the guaranteed single size call;
- zero-length results, short explicit buffers, target-specific length types,
  element-size multiplication, and overflow paths;
- byte arrays and custom copyable element arrays, not only text;
- generic capabilities and `constof` values reused for both protocol calls;
- interface and virtual dispatch used consistently for sizing and writing;
- scoped lifetime rejection and allocated lifetime acceptance in returns,
  captures, conditionals, loops, async bodies, and iterator bodies where the
  existing lifetime rules distinguish them;
- existing terminator behavior for `char[]` to `string` and corresponding
  string-family conversions.

Runtime tests must not assume a general exact prep invocation count. A focused
lowering test may assert the canonical `.length` optimization, while semantic
runtime tests assert only the prep contract's stable required size and contents.

### Interpolation

Cover:

- bare holes discovering `toString` instead of `format`;
- direct text and constant text retaining direct paths;
- explicit `toString(options)` prep holes;
- explicit alternate prep formatter methods;
- no intermediate prepared allocation for equivalent bare and direct-call holes;
- receiver and formatting arguments evaluated once from left to right;
- UTF-8-only result typing remains unchanged;
- ordinary non-prep direct-text calls inside holes;
- missing, ambiguous, wrong-buffer, wrong-return, thrown, receiver, and lifetime
  formatter diagnostics.

### Standard Library, API, Metadata, LSP, And Editor Assets

Cover:

- primitive, bool, character, string, and date/time `toString` runtime output;
- an API/metadata audit confirming that the standard library exposes no public
  function named exactly `format`;
- public API snapshots containing `toString` and prep declarations;
- metadata snapshots retaining `modifier: "prep"` and omitting prep property
  classification;
- signature help and hover distinguishing declared and transformed results;
- semantic-token and syntax-highlighting fixtures for contextual `prep`;
- syntax grammar validation for every shipped editor asset.

### Test-Running Strategy

During implementation:

1. run targeted parser, diagnostics, lowering, runtime, API, metadata, LSP, and
   standard-library tests after each affected layer;
2. rerun any updated golden test until no `.actual.*` files remain;
3. run the fast golden-only set after the feature surfaces converge;
4. build the solution and run the full non-skipped suite on the supported macOS,
   Windows/MSVC, and Linux lanes before the final implementation commit;
5. treat unexpected skips and platform-specific output differences as failures
   to investigate, not as implicit acceptance.

The proposal draft itself is documentation-only and does not require running
the compiler suite.

## Documentation Updates

Documentation changes should land with the implementation after acceptance.
They must follow the repository's audience split. The language guide teaches
ordinary Camp programmers how and when to use the feature. It is not a ledger
of declaration validation, binder ordering, lowering opportunities, or unusual
failure cases; focused compiler diagnostics are the first explanation for
those cases when users encounter them.

The semantic supplements are different. They are the complete, normative
compiler specification. They must contain every syntax, binding, typing,
lifetime, lowering, diagnostic, metadata, and emission rule needed to implement
this proposal without consulting this proposal. They must integrate the new
rules with all related existing behavior, preserve unaffected semantics, and
must not replace details with a reference back to this design document.

### Language Guide

Update `docs/language/06-functions-methods-and-callables.md` to:

- introduce one ordinary prep declaration and explain in programmer-facing
  terms that omitting its buffer produces the prepared array;
- show the common direct call, an explicit reusable buffer, and the named
  `buffer: default` size query;
- explain the practical cost model: an omitted buffer may allocate scoped
  storage, so repeated or large results should normally be retained or written
  into reusable storage;
- state briefly that prep-bearing getters use explicit method-call syntax;
- retain only the user-visible fact that prep is part of callable and interface
  contracts, without declaration-shape matrices or lowering detail.

Do not enumerate the restriction on required parameters following prep,
overload-selector placement, full-signature-first binding order, contextual
parser disambiguation, property-candidate recovery, protocol call counts, or
measure-only lowering in the language guide. Those rules belong in semantic
documentation and focused diagnostics.

Update `docs/language/18-expressions-statements-and-operators-reference.md` to:

- remove `prep` and `prep new` expression forms;
- add `(new)` prepared allocation with one ordinary capture/cleanup example;
- explain the practical rule that the owned result must be captured or cleaned
  up directly, without presenting the complete precedence and invalid-chain
  matrix;
- update interpolation examples from `format` to `toString`.

Update `docs/language/16-the-standard-library-in-practice.md` to:

- present `toString` as the standard prep formatter convention;
- use ordinary calls with omitted buffers in formatting and time examples;
- explain when callers should use `.length` and an explicit reusable buffer;
- remove examples using the old `prep` expression prefix or `format` name.

No language-guide index change is required unless headings are added or moved
in a way that changes the existing index map.

### Semantic Supplements

Update `docs/semantics/14-core-expression-statement-and-access-semantics.md`
comprehensively. Its caller-prepared-results material must be self-contained and
normatively specify:

- removal of the `prep` and `prep new` expression forms, contextual recognition
  of `prep` in complete parameter declarations, and identifier/type-name
  disambiguation without a capitalization rule;
- every prep declaration-shape rule, including array and return-type matching,
  the single mutable copyable-array limit, modifier exclusions, `once`,
  following defaultable or compiler-supplied parameters, and the necessary
  position of an overload selector;
- the complete prep behavioral contract, including stable size and contents,
  bounded writes, sizing-time failures, logical terminator treatment, and the
  absence of an exact call-count or call-order guarantee;
- full-signature-first argument-to-slot binding, selector resolution before the
  prep-omission decision, explicit-slot tracking, and why positional or named
  `default` selects a full call when it binds the prep slot, while a declared
  default on an omitted prep parameter does not prevent transformation;
- full and transformed call modes for every direct and callable invocation
  surface, including defaults, hidden/compiler-supplied slots, method
  references, generic substitution, interface and virtual surfaces, and
  imported API declarations;
- intrinsic scalar versus mutable-array result types, `auto` inference, and the
  point at which ordinary target conversions and existing terminator rules
  apply;
- scoped prepared storage, the exact `(new)` grammar and precedence,
  direct-transformed-result requirement, `within`, `finally delete`, and all
  invalid owner-losing postfix shapes;
- ordinary `.length` binding and the boundary between its language meaning,
  best-effort allocation elision, and the guaranteed explicit one-call
  `buffer: default` form;
- prep getter, setter, indexed-property, nameless-indexer, extension,
  interface, and virtual property ineligibility, including candidate recognition
  before the focused diagnostic and preservation of explicit getter receiver
  conventions;
- the `toString` formatter convention, bare-hole eligibility, explicit
  alternate formatter calls, UTF-8-only interpolation, direct-text paths, and
  preservation of prepared formatter identity through interpolation lowering,
  including that bare-hole discovery remains limited to the prepared formatter
  protocol rather than ordinary allocating methods with the same name;
- receiver and argument evaluation order, capture/reuse across protocol calls,
  the ignored second scalar result, error propagation, allocation arithmetic
  and failure, source provenance, legacy-syntax handling without a compatibility
  mode, and diagnostic ranges.

Also update:

- `docs/semantics/01-binding-analysis-and-lowering-pipeline.md` so body analysis
  owns selected callable/substitutions, argument-slot mapping, call mode,
  property rejection, result type, lifetime facts, and `(new)` legality, while
  lowering consumes rather than rediscovers those facts;
- `docs/semantics/03-conversions-raw-carriers-and-fence-casts.md` where needed to
  make clear that a transformed expression first has its prep array type and
  only then uses existing array/string-family conversions and terminator rules;
- `docs/semantics/04-constof-and-signature-compatibility.md` for unchanged prep
  callable compatibility, overload-selector position, method-reference shape,
  and `constof` substitution across both protocol calls;
- `docs/semantics/05-lifetime-analysis-and-flow-facts.md` for scoped transformed
  arrays, escape/capture/return/async/iterator consequences, and `(new)`
  ownership/cleanup;
- `docs/semantics/06-generics-erasure-and-capabilities.md` for generic prep array
  substitution and reuse of `sizeof`, `typenameof`, `vtableof`, and related
  capability values across sizing and writing;
- `docs/semantics/07-callable-lowering-and-context-ownership.md` for
  declaration-shaped references, callable-value calls, invocation-time
  transformation, and capture ownership;
- `docs/semantics/08-async-resumption-lowering.md` wherever the existing prep
  restrictions or scoped-result escape rules cross async and iterator frames;
- `docs/semantics/09-interface-vtables-and-dynamic-dispatch.md` for repeated
  size/write dispatch through the same statically selected source surface and
  the non-transformation of calls through non-prep surfaces;
- `docs/semantics/10-construction-destruction-and-allocation.md` for `(new)`,
  `within`, direct-owner precedence, and `finally delete`;
- `docs/semantics/11-metadata-api-surface-and-symbols.md` for unchanged prep
  declaration/API shape, absence of synthetic transformed overloads, renamed
  standard APIs, tooling views, and property metadata exclusion;
- `docs/semantics/12-target-capabilities-and-c-emission.md` for target-length
  types, element-size and terminator arithmetic, checked failure, and emitted
  size/allocate/write behavior;
- `docs/semantics/13-diagnostics-source-ranges-and-error-quality.md` for
  declaration, explicit-buffer, `(new)`, transformed-call, interpolation, and
  prep-property diagnostics and provenance.

Only passages affected by this proposal should change in those supplements,
but cross-references and retained rules must remain sufficient to follow the
complete behavior. Before implementation is considered documented, compare
every normative rule from `Prep Declaration Rules` through `Callable, Generic,
Interface, And Metadata Surfaces` and the interpolation rules against the
active semantic supplements. Each rule must be stated in its canonical home or
be covered by a precise cross-reference to an existing normative rule. No
semantic requirement may exist only in this proposal, a test, or a diagnostic.

Update `docs/semantics/index.md` only if the listed subsection summaries change.
Do not create a new semantic supplement when the rules fit the existing
canonical homes.

### Compiler And Tooling Documentation

No command-line documentation change is expected. If LSP/editor documentation
currently enumerates operator, hover, signature-help, or semantic-token
behavior, update `docs/compiler/08-language-server-and-editor-tooling.md` with a
short practical note. Do not place binding algorithms in compiler-user docs.

### LLM Guide

Update `docs/camp-llm-coding-guide.md` compactly:

- omitted prep buffers transform by default;
- explicit `buffer:` calls return the scalar length;
- use `(new)` only for direct owned prepared results and clean them up;
- prep methods are not properties;
- use `toString` for canonical UTF-8 formatting;
- interpolation remains UTF-8 and discovers prepared `toString` methods.

Remove stale `prep call`, `prep new`, and canonical `format` examples rather
than keeping a migration appendix.

### Standard Library API Documentation

Update public doc comments at the declarations being renamed. Regenerate or
update any generated API documentation, completion data, metadata fixtures, and
website-facing API data through their established generators. Do not hand-edit
generated output when the repository provides a generator.

### Historical Proposals And Local Material

Accepted proposals are historical and should remain unchanged. After this
proposal is accepted and implemented, the active language guide and semantic
supplements become canonical for the redesigned behavior.

Machine-specific paths, host names, private test-lane commands, and local-only
maintenance notes must remain outside the repository. The proposal and active
documentation should contain only portable policy and supported-platform test
expectations.

## Compatibility

This is a breaking change to an experimental preview language and standard
library.

Source changes include:

- `prep call` becomes `call`;
- `prep new call` becomes `(new) call`;
- omitted-buffer scalar size queries become `call().length` when the caller
  wants the transformed result length, or `call(buffer: default)` when the
  caller requires an explicit one-call size query;
- every public standard-library `format` declaration and every standard-library
  call to it becomes `toString`;
- prep property access becomes explicit `getX()` method syntax.

The implementation should make these changes as one coordinated compiler,
standard-library, tests, docs, metadata, API, LSP, and editor update. It should
not add legacy aliases or special migration diagnostics.

The native ABI of prep methods does not change. Downstream Camp API headers and
metadata do change by name where `format` becomes `toString`, but prep parameter
representation remains stable.

## Risks And Mitigations

### Compact Calls Can Hide Scoped Allocation

An ordinary-looking omitted-buffer method call can allocate an input-sized
scoped array. Large serialization, external text, loops, recursion, and nested
conversions can create stack pressure.

The property ban, explicit method-call syntax, documentation cost guidance,
`.length` size query, reusable explicit buffers, and ordinary lifetime checks
mitigate the risk. Compiler diagnostics should remain clear when generated
scoped storage cannot appear in a context.

### Declared And Expression Results Differ

Tooling shows a scalar/prep declaration while an omitted-buffer invocation has
an array result. That distinction can confuse signature help and generic error
messages.

The compiler must record call mode explicitly, and tooling must present the
declared contract and expression result as two views of the same call rather
than inventing synthetic overloads.

### Interpolation Can Regress Into Intermediate Allocation

If general transformed-call lowering runs before interpolation composition,
explicit `toString()` holes can allocate and copy intermediate arrays.

Body analysis must preserve prepared formatter parts, and lowering/C-emission
tests must compare the bare and direct-call forms.

### `(new)` Can Lose Ownership If Chaining Is Accepted

Allowing postfix operations after the allocated prepared result can discard the
owning reference.

The direct-result rule and precedence make those forms diagnostics while
retaining capture and `finally delete`.

### Contextual Highlighting Can Drift

Compiler parsing, LSP semantic tokens, and editor grammars may disagree about
whether `prep` is a modifier or identifier.

Shared syntax fixtures should cover both readings in every shipped grammar.

## Less-Obvious Benefits

- The source model has one normal logical result: the prepared array. The scalar
  length is visibly the explicit-buffer protocol result.
- Immediate `.length` creates a recognizable allocation-elision opportunity
  without expanding the language contract.
- The property restriction reinforces Camp's field-like accessor philosophy.
- `toString` is more discoverable to users and tooling than a protocol-specific
  `format` convention.
- Making `prep` contextual returns a useful identifier to programs outside its
  remaining declaration role.
- The unchanged ABI allows the source redesign without a native compatibility
  mechanism.

## Acceptance Criteria

The proposal is ready to move from draft to pending when reviewers agree on:

- the full-signature-first binding order;
- the post-prep declaration restriction;
- overload selection before transformation;
- best-effort `.length` elision without an exact-call-count guarantee;
- `(new)` precedence and direct-owner restriction;
- prep property diagnostics after candidate recognition;
- contextual `prep` grammar;
- `toString` interpolation lookup;
- the complete standard-library rename;
- the implementation, test, documentation, metadata, LSP, and editor surfaces.

After acceptance, implementation is complete when:

- every prep-bearing invocation surface uses the recorded full or transformed
  mode correctly;
- direct explicit-buffer calls remain single scalar calls;
- transformed calls preserve evaluation, dispatch, capabilities, lifetimes,
  allocation, error flow, and existing conversions;
- `(new)` and `finally delete` follow ordinary allocation policy without
  allowing owner-losing chains;
- prep property attempts receive focused diagnostics and no prep property
  metadata is emitted;
- interpolation uses `toString` and preserves direct-to-final-buffer lowering;
- every public standard-library `format` function and its internal/test/docs
  call sites are renamed to `toString`, with no public `format` alias left;
- API, metadata, LSP, and editor surfaces agree with the compiler;
- the language guide teaches ordinary use and practical costs without
  declaration-validation or binding trivia;
- the semantic supplements pass the proposal-to-semantics completeness audit,
  preserve related existing rules, and contain no proposal-only semantic gap;
- all other listed active documentation is updated for its intended audience;
- targeted tests pass during implementation, no `.actual.*` files remain, and
  the full non-skipped supported-platform suite passes before the final
  implementation commit.
