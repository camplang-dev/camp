# Late Overload Selectors

## Status

Accepted and implementation-complete.

Active language guidance now lives in the language guide, compiler-writer
semantics live in the semantic supplements, and command-line/tooling behavior
lives in the compiler docs. This proposal is retained as the accepted design
record.

## Summary

Camp currently allows a function or method overload family to use one
`overload` selector parameter. That selector must be the first formal
non-`this` parameter. This proposal keeps the same overload model, but allows
the single selector parameter to appear after stable ordinary parameters.

The motivating shape is a key/index first API where the key identifies a slot
and the following value or output parameter selects the concrete overload:

```camp
class JsonArray
{
	bool tryGet(const this, @index nuint index, overload out int value);
	bool tryGet(const this, @index nuint index, overload out bool value);
	bool tryGet(const this, @index nuint index, overload out double value);

	void setElement(@index nuint index, overload int value);
	void setElement(@index nuint index, overload bool value);
	void setElement(@index nuint index, overload double value);
}
```

The overload selector still contributes the callable name and ABI-visible
symbol suffix:

```text
JsonArray.tryGetInt
JsonArray.tryGetBool
JsonArray.tryGetDouble
JsonArray.setElementInt
JsonArray.setElementBool
JsonArray.setElementDouble
```

Callers can use either the overload-family invoker or the concrete full
callable name:

```camp
if (json.tryGet(0, out bool enabled))
{
}

json.setElement(0, true);
json.setElementBool(0, true);
```

For property setters, the assigned value participates as the logical setter
value argument:

```camp
json.Element[0] = 42;
json.Element[1] = true;
json.Element[2] = 1.5;
```

## Goals

- Preserve Camp's deliberately small overload system: one source-level invoker,
  one explicit selector parameter, and one concrete ABI-visible callable per
  selector type.
- Support key/index-first APIs where the overload selector is naturally the
  value being written or the typed `out` slot being read.
- Make indexed property setters work naturally with typed values.
- Keep overload resolution selector-driven, not a broad C++/C# style candidate
  ranking system.
- Keep metadata and API headers source-shaped and transparent.
- Require overload families to have a simple, stable callable shape before the
  selector so call-site diagnostics stay understandable.

## Non-Goals

- Do not allow more than one overload selector.
- Do not allow `this`, `thrown`, constructors, or destructors to use
  `overload`.
- Do not allow default values on overload selector parameters.
- Do not infer a selector type from an otherwise target-typed expression such
  as `null`, `default`, an aggregate initializer, or an untyped lambda.
- Do not change overload ABI naming.
- Do not make overload family names into callable values.
- Do not add overload selection by arbitrary best-conversion ranking.

## Intended Usage

### Structured Data And Property Bags

Dictionary-like data often has a stable key first and a type-specific value
second:

```camp
class PropertyBag
{
	bool tryGet(const this, const char[] key, overload out int value);
	bool tryGet(const this, const char[] key, overload out bool value);
	bool tryGet(const this, const char[] key, overload out string value);

	void set(const char[] key, overload int value);
	void set(const char[] key, overload bool value);
	void set(const char[] key, overload string value);
}

bag.set("retryCount", 3);
bag.set("enabled", true);

if (bag.tryGet("title", out string title))
{
}
```

### Indexed Dynamic Containers

JSON arrays and document trees are the strongest indexed-property use case:

```camp
class JsonArray
{
	void setElement(@index nuint index, overload int value);
	void setElement(@index nuint index, overload bool value);
	void setElement(@index nuint index, overload double value);
	void setElement(@index nuint index, overload string value);
}

json.Element[0] = 10;
json.Element[1] = false;
json.Element[2] = "ready";
```

When the assigned expression has no independent selector type, callers can
select the concrete accessor explicitly:

```camp
json.setElementString(0, null);
json.Element[0] = (string)null;
json.ElementString[0] = null;
```

The last form follows from the existing property-accessor model: if
`setElementString` is the concrete full callable name, `ElementString` is the
concrete property surface for that accessor.

### Native And Graphics Wrappers

Late selectors are useful for wrappers over APIs that conceptually set a named
or indexed option:

```camp
void setOption(Socket this, int option, overload int value);
void setOption(Socket this, int option, overload bool value);

void setUniform(Shader this, const char[] name, overload int value);
void setUniform(Shader this, const char[] name, overload float value);
void setUniform(Shader this, const char[] name, overload Matrix4 value);
```

These should stay small families. If entries differ semantically rather than
only by value type, distinct function names remain clearer.

## Semantic Rules

### Declaration Rules

A function or method may have at most one `overload` selector parameter. The
selector may appear after zero or more ordinary callable parameters.

The existing selector restrictions remain:

- `this` cannot be an overload selector;
- `thrown` cannot be an overload selector;
- constructors and destructors cannot use `overload`;
- an overload selector cannot declare a default value;
- the selector type must contribute a method-symbol type fragment.

Camp's ordinary default-parameter ordering rule means a parameter with a
default value may not be followed by a parameter without a default value. Since
the overload selector cannot have a default value, parameters before the
selector cannot have defaults:

```camp
void write(int indent = 0, overload int value);  // invalid
void write(int indent = 0, overload bool value); // invalid
```

Parameters after the selector may use defaults normally:

```camp
void setElement(@index nuint index, overload string value, bool escape = true);
```

### Overload Family Shape

Every entry in an overload family must agree on the selector's callable
position and selector parameter name:

```camp
void put(@index nuint index, overload int value);
void put(@index nuint index, overload bool value); // valid
```

This is invalid because the selector appears in different callable positions:

```camp
void put(@index nuint index, overload int value);
void put(overload bool value, @index nuint index);
```

Every callable parameter before the selector must be structurally identical
across the family. Structural identity includes at least:

- parameter name;
- parameter modifier;
- resolved source type after declaration binding;
- parameter attributes that affect calling semantics, including `@index` and
  `@range`;
- default-value absence;
- generated source-parameter grouping for expanded forms.

This is invalid because the pre-selector attributes differ:

```camp
void setElement(@index nuint index, overload int value);
void setElement(nuint index, overload bool value);
```

This is invalid because the pre-selector source types differ:

```camp
void setElement(@index nuint index, overload int value);
void setElement(@index int index, overload bool value);
```

Parameters after the selector continue to follow the existing overload and
callability rules. They need not be identical unless another existing rule
requires compatibility for virtual dispatch, interface implementation, callable
ascription, or property access.

### Call Selection

Overload selection uses the selector argument corresponding to the selector
parameter's callable position. If the selector argument is named, the name may
identify the selector independently of positional order:

```camp
json.setElement(value: true, index: 1);
```

If the selector argument is missing, the call is invalid:

```camp
json.setElement(index: 1); // error
```

If the selector expression has no independent static type, the call is invalid.
The caller should use a cast or the concrete full callable name:

```camp
json.Element[0] = null;         // error
json.Element[0] = (string)null; // valid
json.ElementString[0] = null;   // valid
```

Lambdas remain unable to select overloads without an explicit target:

```camp
callbacks.add("changed", value => value > 0); // error
callbacks.add("changed", (fn bool(int))(value => value > 0));
```

### `out` Selectors

`overload out` remains valid:

```camp
bool tryGet(@index nuint index, overload out int value);
bool tryGet(@index nuint index, overload out bool value);
```

The caller must provide an explicit `out` argument:

```camp
json.tryGet(0, out bool enabled); // valid
json.tryGet(0, enabled);          // error
```

The selector type must come from an explicit output slot type. `out auto` does
not select an overload by itself:

```camp
json.tryGet(0, out auto value); // error
json.tryGet(0, out bool value); // valid
```

Typed discard syntax should be allowed, while untyped discard syntax should not
select an overload:

```camp
json.tryGet(0, out bool _); // valid
json.tryGet(0, out _);      // error
```

### Property Access

Camp property syntax remains method-call syntax. A late selector changes only
which logical argument selects the concrete accessor.

For setters, the assigned value is the logical final argument. Therefore this:

```camp
json.Element[0] = true;
```

binds like this:

```camp
json.setElement(0, true);
```

and selects `setElementBool`.

Concrete overload accessor names may be used as concrete properties:

```camp
json.ElementString[0] = null;
```

This binds to `setElementString`, not to the `setElement` overload family.

Getter overloads with late selectors are allowed when they naturally follow the
same rules, but they should be used sparingly:

```camp
int getElement(@index nuint index, overload int fallback);
string getElement(@index nuint index, overload string fallback);

auto value = json.Element[0, "fallback"];
```

For common APIs, direct try-style methods are usually clearer than getter
properties with fallback selector arguments:

```camp
json.tryGet(0, out string value);
```

### `@index` And `@range`

`@index` and `@range` belong to callable parameters and remain part of the
source callable surface.

Late selector overload families must keep all pre-selector `@index` and
`@range` shapes identical. This keeps from-end index lowering and range
expansion independent of which overload entry is selected:

```camp
void replace(@range nuint start, nuint count, overload string value);
void replace(@range nuint start, nuint count, overload byte[] value);
```

Range expansion may be performed after family-shape validation because every
candidate sees the same pre-selector range surface.

### Generics And `constof`

The selector type must still contribute a method-symbol type fragment. A
generic selector constrained only to an unfragmentable type remains invalid:

```camp
void set<T: any>(@index nuint index, overload T value); // invalid
```

Generic parameters before the selector are allowed when the pre-selector shape
is identical across the overload family:

```camp
void put<T: any>(T[] items, overload int value, sizeof(T));
void put<T: any>(T[] items, overload bool value, sizeof(T));
```

Generic inference from pre-selector parameters should run as part of normal
argument checking after the selector chooses a concrete entry. The compiler
should not introduce candidate ranking where generic inference across all
overload entries competes with selector selection.

`constof` follows the existing substitution rules. Selector selection uses the
selector argument's declared or independent type. After selection, ordinary
`constof` substitution checks whether the chosen callable can accept the
actual arguments:

```camp
void get(const byte[] source, @index nuint index, overload out constof(source) byte* value);
void get(const byte[] source, @index nuint index, overload out uint value);
```

### Callable Values

An overload family name remains not-a-callable-value:

```camp
fn void(JsonArray*, nuint, int) setter = JsonArray.setElementInt; // valid
fn void(JsonArray*, nuint, int) setter = JsonArray.setElement;    // error
```

The concrete full callable name or an explicit wrapper must be used.

### Interfaces, Virtual Methods, And Overrides

Interface, virtual, override, and callable-ascription rules must preserve
overload family category, selector position, selector name, and pre-selector
shape.

```camp
interface IJsonArray
{
	bool tryGet(const this, @index nuint index, overload out int value);
	bool tryGet(const this, @index nuint index, overload out bool value);
}

class JsonArray : IJsonArray
{
	bool tryGet(const this, @index nuint index, overload out int value): IJsonArray;
	bool tryGet(const this, @index nuint index, overload out bool value): IJsonArray;
}
```

The vtable and interface dispatch surfaces use the concrete full callable
names, such as `tryGetInt` and `tryGetBool`. A derived or implementing method
whose selector is in a different callable position is not compatible.

### ABI Names

The selector's source type contributes the same flattened type fragment it
does today. The selector's position does not affect the suffix.

```camp
export void setElement(@index nuint index, overload bool value);
```

The concrete callable name remains:

```text
setElementBool
```

For a member function, the generated native symbol remains type-qualified:

```text
JsonArray_setElementBool
```

Existing first-selector overload names are unchanged.

### API Headers And Metadata

API headers preserve the source declaration:

```camp
export bool tryGet(const this, @index nuint index, overload out int value);
```

Metadata already records overload state on parameters. That representation must
continue to preserve parameter order and must be treated as position-independent
by metadata consumers.

Metadata must also expose enough callable identity for consumers to distinguish
the overload-family invoker from the concrete callable entry. If the current
metadata `name` field remains the source invoker name, add an explicit concrete
callable-name field. If metadata changes `name` to the concrete callable name,
add an explicit invoker/family field. The important requirement is that both
facts are available without reconstructing names from ABI symbols.

One acceptable shape is:

```json
{
  "name": "setElement",
  "callableName": "setElementInt",
  "parameters": [
    { "name": "index", "type": "nuint" },
    { "name": "value", "type": "int", "overload": true }
  ],
  "propertyName": "ElementInt",
  "propertyIndexParams": ["index"],
  "propertyValueParam": "value"
}
```

Property metadata continues to identify the accessor kind, property name,
index parameters, and setter value parameter. For concrete overload accessors,
the property name should be derived from the concrete callable name, so
`setElementInt` produces `propertyName: "ElementInt"`. The overload-family
invoker name should remain available separately through metadata callable
identity.

Export projections should continue to require the full unique callable name for
overloaded members:

```camp
export JsonArray { setElementInt, setElementBool };
```

Projection by overload signature is outside this proposal.

## Diagnostics

Declaration diagnostics should identify malformed overload families at the
declaration site:

```text
Overload family `setElement` must use the same selector parameter position.
```

```text
Overload family `setElement` must use identical parameters before the overload selector.
```

```text
`overload value` is invalid because 'T' does not contribute a method-symbol type fragment.
```

Call-site diagnostics should point at the selector argument, assigned value, or
property access that caused the failure:

```text
`setElement` is an overload family. The selector argument is missing.
```

```text
Cannot select overload `setElement` because the selector expression has no independent static type. Add an explicit cast.
```

```text
Out overload selectors require an explicit 'out' argument.
```

For property assignment, the assigned value is the best diagnostic range when
the RHS cannot select a concrete setter.

## Current Compiler Impact

The current parser and bindable-node builder already support most of the
syntax mechanically:

- `CampParser.ParameterDeclaratorKeywords` includes `overload`.
- `BindableNodeBuilder` sets `ParameterDefinition.IsOverloadSelector` from the
  parameter declarator keyword, independent of position.
- `MetadataJsonSerializer` writes `"overload": true` on parameters.
- `BindableNodeCodeSerializer` prints `overload` on parameters.

The main semantic restriction is in
`src/Camp.Compiler/BindableNodeAnalyzer.Overloads.cs`.

Likely implementation changes:

- `AnalyzeOverloadDeclaration` should remove the diagnostic that says
  ``overload`` may appear only on the first non-`this` formal parameter.
- The same method should continue computing the selector fragment from the
  selected parameter's resolved source type.
- `ValidateOverloadFamily` should grow a real family-shape validation step:
  selector name, selector callable index, and structural equality of
  pre-selector callable parameters.
- `GetCallableOverloadSelectorIndex` already scans the callable parameter list
  and can represent non-first selectors. It should become the single shared
  helper for selector callable index.
- `GetSelectorArgumentIndex` already supports named selector arguments. It
  should remain the entry point for mapping a call's source arguments to the
  selector argument.
- `TrySelectOverload` should keep analyzing only the selector argument for
  selection, then allow ordinary argument analysis to finish against the chosen
  function.
- `TryAnalyzePropertySetter` in
  `BindableNodeAnalyzer.MethodBody.Semantics.cs` already appends the assigned
  value to the logical argument list before overload selection. That is the
  right shape for indexed setter selection, but tests should confirm that late
  selector positions select before target typing the assigned value.
- `TryAnalyzePropertyIndexer` should be checked for getter overloads with late
  selectors and range/index arguments.
- `LookupPropertyGetters` and `LookupPropertySetters` delegate to type-function
  lookup, which already checks source names and callable names. Tests should
  verify that concrete overload property access such as `ElementString` binds
  through that path.
- Override, virtual, interface, and callable-ascription compatibility checks in
  declaration validation should compare selector position and pre-selector
  shape, not only overload spelling/category.
- C emission should not require ABI naming changes, but call lowering and
  property lowering tests should verify that selected concrete callable names
  are used.

## Recommended Refactoring

This feature should not be implemented by sprinkling selector-position checks
through unrelated analyzer code. Add a small overload-family shape helper close
to the current overload analyzer.

Recommended internal model:

```text
OverloadSelectorShape
  selectorName
  selectorCallableIndex
  selectorModifier
  selectorResolvedType
  selectorFragment
  preSelectorParameters[]
```

The helper should provide:

- find selector;
- find selector callable index;
- build the source callable name fragment;
- compare pre-selector parameter shape;
- format shape mismatch diagnostics.

Pre-selector comparison should use semantic facts, not raw source text. It
should compare resolved types and parameter semantics. It should avoid ad hoc
string manipulation except for already-canonical resolved type names and
formatted diagnostic text.

This refactoring gives one place for overload-family validation, call
selection, override matching, interface matching, and LSP display code to ask
about selector position. It also reduces the risk of a future feature assuming
the selector is parameter zero again.

## Test Surface

Use the smallest number of additional tests that covers each semantic goal and
corner case. Existing overload tests should be adjusted only where they assert
the old non-first-selector diagnostic.

### Positive Lowering And C Compile Tests

Add one focused lowering test for indexed setter selection:

```camp
class JsonArray
{
	void setElement(@index nuint index, overload int value) {}
	void setElement(@index nuint index, overload bool value) {}
	void setElement(@index nuint index, overload string value) {}
}

void main(JsonArray* json)
{
	json.setElement(0, true);
	json.Element[1] = 3;
	json.ElementString[2] = null;
}
```

Expected lowering should show concrete calls:

```text
JsonArray_setElementBool(json, 0, true)
JsonArray_setElementInt(json, 1, 3)
JsonArray_setElementString(json, 2, null)
```

Add one runtime or C-compile test for `overload out` after an index/key:

```camp
bool tryGet(@index nuint index, overload out int value)
{
	value = 7;
	return true;
}

bool tryGet(@index nuint index, overload out bool value)
{
	value = true;
	return true;
}

export int main()
{
	tryGet(0, out int number);
	tryGet(1, out bool enabled);
	return enabled ? number - 7 : 1;
}
```

This covers the key runtime use case and confirms that out-selector selection
is not tied to the first argument.

### Declaration Diagnostics

Extend the existing overload-invalid diagnostic test or add one targeted case
covering:

- two selector parameters;
- selector positions differ across a family;
- selector names differ across a family;
- pre-selector types differ;
- pre-selector `@index` or `@range` attributes differ;
- selector default value remains invalid;
- selector type with no method-symbol fragment remains invalid;
- constructor/destructor selector remains invalid.

The old diagnostic for "first non-this formal parameter" should be removed.

### Call-Site Diagnostics

Add focused diagnostics for:

- missing late selector argument;
- `out auto` selector;
- untyped discard `out _`;
- RHS `null` or `default` in overloaded indexed property assignment;
- untyped lambda as a late selector;
- valid casted RHS selecting the concrete setter.

The diagnostics should assert useful source ranges where the existing golden
test framework supports them.

### Properties, Ranges, And Named Arguments

Add one lowering or semantic test covering:

- named arguments selecting the late selector:

```camp
json.setElement(value: true, index: 1);
```

- from-end index syntax:

```camp
json.Element[^1] = false;
```

- a `@range` pre-selector pair:

```camp
json.replace[0..^1] = "text";
```

Only include the `@range` case if the current property/range syntax can express
the example clearly without introducing unrelated syntax churn.

### Interfaces, Virtuals, And API Output

Add one test that combines interface or virtual dispatch with late selectors:

```camp
interface Writer
{
	void write(int level, overload int value);
	void write(int level, overload string value);
}
```

Verify vtable/API output uses `writeInt` and `writeString`, and that an
implementation with the selector in a different position is rejected.

Add metadata coverage for a late-selector property setter. Verify:

- parameter order is preserved;
- the late selector parameter has `"overload": true`;
- property metadata records the concrete overload property name, index
  parameter, and setter value parameter;
- callable name/invoker name remain distinct.

### LSP Tests

Update language-service/LSP tests for:

- completion labels and collapsed overload details for late-selector methods;
- signature help with active parameter indexes where the selector is not first;
- hover/signature display preserving `overload` in its late position;
- override completion snippets preserving the late selector position.

## Extras And Editor Tooling

Syntax highlighting should not need a grammar change. The existing Sublime,
micro, and VS Code TextMate grammars already highlight `overload` as a keyword
wherever it appears as a parameter modifier:

- `extras/Camp.sublime-syntax`;
- `extras/camp.yaml`;
- `extras/vscode-camp/syntaxes/camp.tmLanguage.json`.

Implementation should still smoke-test the syntax files with examples where
`overload` follows `@index` and `out`:

```camp
bool tryGet(@index nuint index, overload out int value);
```

The VS Code extension mostly delegates semantics to `camp-lsp`. Extension
changes should be unnecessary unless display logic outside the LSP assumes the
selector is first. The `.vsix` packaging should be regenerated only when the
extension source or grammar changes.

## LSP And Language Service

The LSP should expose the same semantics as the compiler:

- diagnostics for invalid family shape;
- hover and signature help showing the selector in its declared position;
- completion collapse by invoker name still counting all overload entries;
- override snippets preserving parameter order and selector placement;
- property completion surfacing concrete accessor properties such as
  `ElementString` when the concrete accessor is visible;
- definition/hover on concrete property surfaces resolving to the concrete
  accessor declaration.

Most work should be in `CampSymbolService` and the shared language-service
analysis paths, not protocol handling in `src/camp-lsp/Program.cs`. The LSP
server should consume compiler facts and return them through existing hover,
completion, signature-help, and diagnostic endpoints.

## Documentation Updates

Follow the documentation contributor guide:

- The language guide should receive a focused, reader-facing update in
  `docs/language/06-functions-methods-and-callables.md`. The overload section
  should explain that the selector can appear after stable parameters and show
  one compact key/index-first example. Avoid implementation detail.
- The property-accessor section in the same language guide file should get a
  short note that overloaded setters can select by the assigned value, including
  indexed setters.
- `docs/semantics/14-core-expression-statement-and-access-semantics.md` should
  comprehensively specify property setter binding with late overload selectors,
  target-typed RHS rejection, concrete overload property surfaces such as
  `ElementString`, and diagnostics.
- `docs/semantics/11-metadata-api-surface-and-symbols.md` should specify that
  overload selector position is preserved in metadata and that metadata
  consumers must not assume the selector is the first callable parameter.
- The overload/callable compatibility material in
  `docs/semantics/04-constof-and-signature-compatibility.md` should mention
  selector position and pre-selector shape where callable/interface
  compatibility is discussed.
- `docs/camp-llm-coding-guide.md` should receive a compact reminder that
  overload selectors may appear after stable key/index parameters, while
  selector expressions still need independent types.

No compiler command-line documentation changes are expected because this is a
language semantics change, not a CLI feature.

## Implementation Completion Criteria

The feature is complete when:

- non-first overload selectors compile for ordinary functions, instance
  methods, static methods, static extensions, interface methods, and virtual
  methods where the family shape is valid;
- invalid family shapes produce declaration diagnostics before confusing
  call-site failures;
- indexed property setters select concrete overloads from assigned value type;
- concrete overload accessor property names such as `ElementString` bind to the
  concrete accessor;
- `out` selector calls work after pre-selector parameters and reject `out auto`
  and untyped `out _`;
- weak target-typed selector expressions reject with useful diagnostics;
- ABI names, API headers, and metadata preserve existing naming conventions;
- LSP hover, completion, signature help, and diagnostics agree with compiler
  semantics;
- Sublime, micro, and VS Code syntax highlighting continue to identify
  `overload` in late selector declarations;
- language, semantics, and LLM guide updates are complete and consistent with
  the contributor guide;
- targeted tests pass during implementation and the full suite passes before
  committing the final compiler change.
