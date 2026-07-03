# Camp Doc Comments And Metadata JSON Supplement

This supplement describes Camp doc comments, the metadata attributes produced
from them, and the compiler-emitted metadata JSON file. It is intended for
people and tools that need to write Camp documentation comments or consume Camp
metadata output.

The metadata system is source-level. It describes the Camp declarations that a
programmer wrote and the public/library surface those declarations expose. It is
not a lowered C ABI dump, and it does not try to document compiler-generated
helper declarations by default.

## Doc Comments

Camp recognizes two documentation comment forms:

```camp
/// A line doc comment.
/// Additional line doc comment text.

/** A block doc comment. */
```

A doc-comment block attaches to the immediately following declaration or
declaration child. Contiguous doc-comment lines form one block. Blank
doc-comment lines are part of the same block and usually become paragraph
breaks. Any non-doc-comment token between the comment and the declaration
breaks the attachment.

The most common form is a line doc comment:

```camp
/// Adds one to the value.
///
/// - value: Input value.
/// @returns The incremented value.
export int addOne(int value);
```

Doc comments may attach to top-level declarations, type members, fields, enum
values, type parameters, ordinary parameters, receiver parameters, and other
children that are representable in the bindable declaration model.

## Lowering To Attributes

Doc comments are a source convenience. During binding, the compiler translates
them into ordinary metadata attributes on the attached declaration or child.
After this translation, the comments themselves are erased.

Plain text lowers to `@summary(...)`.

```camp
/// Represents a reusable buffer.
export struct Buffer
{
}
```

is equivalent, for metadata purposes, to:

```camp
@summary("Represents a reusable buffer.")
export struct Buffer
{
}
```

Explicit doc attributes lower to attributes with the same name:

```camp
/// Searches the list.
/// @remarks The list should usually be sorted first.
/// @returns The matching index, or -1.
export nint find(...);
```

becomes:

```camp
@summary("Searches the list.")
@remarks("The list should usually be sorted first.")
@returns("The matching index, or -1.")
export nint find(...);
```

The compiler currently recognizes these doc-comment attributes:

- `@summary`
- `@remarks`
- `@returns`
- `@example`
- `@see`
- `@deprecated`

Unknown doc-comment attributes are compiler errors. Camp may add linter-level
documentation quality checks later, but unresolved documentation structure that
affects metadata correctness is treated as an error now.

## Child Targets

A doc-comment line beginning with `- name:` targets a child of the attached
declaration. The child target defaults to `@summary`.

```camp
/// Finds a value.
///
/// - T: Element type.
/// - this: Values to search.
/// - value: Value to find.
/// - start: First index to inspect.
export nint find<T: any>(
	const T[] this,
	in T value,
	@range nuint start = 0,
	nuint count = ^0,
	sizeof(T));
```

The compiler attaches those summaries to the type parameter `T`, the receiver
parameter `this`, and the ordinary parameters. A missing child target is a
compiler error.

Child targets can also use an explicit doc attribute:

```camp
/// Represents a state.
/// - Started: @remarks This is the first active state.
export enum State
{
	Started
}
```

Child targets are matched against the children of the declaration they are
attached to. For a struct or class, this includes fields and methods. For an
enum, this includes enum values. For a function, this includes type parameters,
ordinary parameters, and a `this` receiver parameter if present.

## Symbol Links And `symbolof`

Inside ordinary doc-comment text, `[Symbol]` creates a documentation link. The
text content receives a `%s` placeholder, and the linked symbol is emitted as a
`symbolof(...)` metadata expression.

```camp
/// Adds one to [Number].
export int addOne(int value);
```

lowers like this:

```camp
@summary("Adds one to %s.", symbols: [symbolof(Number)])
export int addOne(int value);
```

`symbolof(...)` is only valid in metadata attribute arguments. It is not a
runtime expression. During binding, the compiler resolves each `symbolof`
reference and reports an error if the target is not visible.

When metadata JSON is emitted, resolved symbol links appear in a `symbols`
array:

```json
{
  "name": "summary",
  "content": "Adds one to %s.",
  "symbols": [
    { "ref": "newtype:Docs::Number", "text": "Number" }
  ]
}
```

Consumers should render the `content` string and substitute or associate the
entries in `symbols` with the `%s` placeholders in order. The `ref` value is an
opaque metadata id.

Literal percent signs in doc text are escaped to `%%` so documentation content
can safely use `%s` as the symbol placeholder convention.

## Literal Regions

Inline code spans are literal regions:

```camp
/// Uses `[NotALink]`, `@summary`, `- value:`, and `%s`.
export void literalDocs();
```

Text inside backticks is not parsed for symbol links, doc attributes, child
targets, or percent placeholders.

Fenced code blocks are also literal regions. The fence uses three backticks and
may include an optional language tag.

````camp
/// Example formatter.
/// @example
/// ```camp
/// Console.writeLine("hello");
/// ```
export void example();
````

The `@example` content keeps the fenced block as text. No symbol links or child
targets are parsed inside the fence.

## Metadata Attributes In Camp Source

The attributes generated from doc comments are ordinary metadata attributes.
They can also be written directly:

```camp
@summary("Writes one line.")
@remarks("This uses the current writer.")
export void writeLine(const char[] value);
```

The attribute argument rules used by generated doc attributes are:

- the first positional string argument is the main content;
- named argument `symbols:` may contain `symbolof(...)` references;
- `symbolof(...)` is valid only in metadata attribute arguments;
- metadata attributes are preserved in Camp API output for exported
  declarations;
- generated C and C API headers omit documentation metadata for now.

## Emitting Metadata JSON

The compiler emits metadata JSON with:

```text
campc build library.camp --metadata export
campc build library.camp --metadata public
campc build library.camp --metadata all
campc build library.camp --metadata none
```

The option selects the metadata view:

- `none`: do not emit metadata JSON.
- `export`: emit declarations marked `export`.
- `public`: emit declarations marked `export` or `public`.
- `all`: emit all source-level declarations visible to the compilation.

There is no separate `private` metadata mode. Camp does not use `private` as a
source keyword; `all` is the full source-level view.

For static and shared library builds, metadata defaults to `export`. For
executable, winexe, and plain C-emission builds, metadata defaults to `none`.
The command-line option can override the default.

When emitted, metadata is written as a deliverable named:

```text
<project>_api.json
```

The file is placed beside the other final output artifacts, using the same
output directory rules as library API artifacts. For example, a standard library
build may produce `std_api.json`.

Metadata output cannot be combined with inspection modes such as `--inspect`,
`--inspect-api`, or `--xml`.

## JSON Top-Level Shape

The emitted JSON has this top-level shape:

```json
{
  "format": "camp.metadata",
  "version": 1,
  "module": {
    "name": "Docs",
    "namespace": "Docs"
  },
  "view": {
    "visibility": "export",
    "level": "source",
    "generated": false
  },
  "declarations": [],
  "stubs": []
}
```

`format` is always `camp.metadata`.

`version` is the metadata schema version. Consumers should reject versions they
do not understand.

`module.name` is the logical module/project name used for metadata output.
`module.namespace` is present when the Camp source uses `export as`.

`view.visibility` is `export`, `public`, or `all`, matching the requested
metadata mode.

`view.level` is currently `source`.

`view.generated` is currently `false`; generated helper declarations are not
included in the primary metadata view.

`declarations` contains emitted declarations.

`stubs` is optional. It contains small placeholder records for referenced
declarations that are needed by links or signatures but were not fully emitted
in the selected visibility view.

## Metadata IDs

Every emitted declaration has an `id`.

```json
{
  "id": "struct:Std::List",
  "kind": "struct",
  "name": "List"
}
```

Ids are deterministic and readable, but consumers should treat them as opaque.
They are stable enough to link records inside one metadata file, but tools
should not derive language semantics by parsing the id string.

References to emitted declarations use these ids:

```json
{ "ref": "struct:Std::List", "text": "List" }
```

If a referenced declaration is outside the emitted view, a matching entry may
appear in `stubs`.

## Common Declaration Fields

Most declaration objects may contain:

- `id`: opaque metadata id.
- `kind`: declaration kind when not obvious from the containing array.
- `name`: source-level Camp name.
- `symbol`: canonical flattened symbol, omitted when identical to `name`.
- `visibility`: `export` or `public` for top-level emitted declarations.
- `extern`: `true` for `extern` declarations.
- `metadata`: metadata attributes such as `summary`, `remarks`, and `returns`.

The compiler omits redundant fields where the container already implies them.
For example, items in `fields` do not need `"kind": "field"`, and parameters do
not carry a `visibility` property.

## Metadata Attribute Objects

Metadata attributes have this shape:

```json
{
  "name": "summary",
  "content": "Finds %s values.",
  "symbols": [
    { "ref": "struct:Docs::Widget", "text": "Widget" }
  ]
}
```

`name` is the attribute name without `@`.

`content` is present when the attribute has a string content argument.

`symbols` is present when the attribute has symbol links. The `symbols` entries
correspond to `%s` placeholders in `content`.

Other attribute arguments may appear as JSON values or arrays when future
metadata attributes require them.

## Type Declarations

Class, struct, interface, enum, newtype, and params declarations appear as
declaration objects.

Classes and structs may contain:

- `modifier`: `abstract`, `virtual`, `sealed`, or another current language
  modifier when present.
- `baseTypes`: source-level base classes or interfaces.
- `interfaces`: implemented interface metadata.
- `fields`: field metadata.
- `functions`: member function metadata.
- `typeParameters`: generic type parameters.

`export` metadata intentionally omits fields of classes. In the `public` view,
fields of `export` and `public` classes are included. In the `all` view, class
fields are included for all classes. Struct fields are emitted when the struct
itself is emitted.

Interface implementation metadata looks like this:

```json
{
  "interfaces": [
    {
      "type": "Drawable",
      "ref": "struct:Docs::Drawable",
      "symbol": "Shape_Drawable"
    }
  ]
}
```

`symbol` is present when an exported interface vtable symbol is available for
the class or struct implementation.

Enums contain `values`. Enum values include their computed numeric `value`.
The value is the Camp-computed value after applying explicit initializers,
auto-increment, and the enum's underlying type checks.

```json
{
  "name": "Mode",
  "values": [
    { "name": "First", "type": "Mode", "value": 4 },
    { "name": "Second", "type": "Mode", "value": 5 }
  ]
}
```

Value newtypes contain `underlyingType`.

Callable newtypes omit `underlyingType` and instead contain:

- `callableType`: `fn`, `delegate`, `iter`, `once`, `async`, or `async iter`.
- `callspec`: target calling convention when present.
- `returnType`: return type for `fn`/`delegate`/`once`/`async`, or yielded type
  for `iter`/`async iter`.
- `parameters`: callable slot parameters, when present.

Example:

```json
{
  "kind": "newtype",
  "name": "Callback",
  "callableType": "fn",
  "callspec": "_cdecl",
  "returnType": "int",
  "parameters": [
    { "name": "value", "type": "int" }
  ]
}
```

## Function And Method Metadata

Functions and methods may contain:

- `modifier`: `abstract`, `virtual`, `override`, `sealed`, `static`,
  `constructor`, or `destructor` when present.
- `iterator`: `struct` or `class` for generator declarations.
- `async`: `true` for async functions.
- `upon` scheduler parameters in the ordinary `parameters` array.
- `callspec`: target calling convention when present.
- `returnType`: source-level return type.
- `ascription`: callable newtype ascription when present.
- `typeParameters`: generic type parameters.
- `parameters`: function parameters.
- property companion fields.

Async metadata is source-level. It describes the declaration the programmer
wrote, including `async` and any `upon` parameter, but it does not expose
generated async frames, resume helpers, completion helper functions, scheduler
posting thunks, postponed-call context types, or lambda capture-context
implementation details. Async callable newtypes use `"callableType": "async"`
and otherwise follow the same callable-newtype metadata shape as `fn`,
`delegate`, `once`, and `iter`.

Type-bearing fields in metadata use source-level Camp spelling where a source
type was written. This matters for features whose source contract is more
specific than the lowered ABI type. For example, a receiver-preserving method
has `"returnType": "this"`, and a class-relative factory may have
`"returnType": "classtype*"`.

Raw carriers and target-defined specifiers are likewise preserved as source
spelling. A raw function-pointer type such as `fn*` or `fn* _far` remains
visible as that type string in metadata, and concrete callable types preserve
their callspecs and target specifiers. Metadata consumers should treat this as
the Camp source contract; the C emitter may format the same contract using a
target-specific declarator shape.

Dependent constness is also preserved in source spelling. A type such as
`constof(source) char*` remains `"constof(source) char*"` in metadata so tools
can recover the anchor relationship. C and ABI output erase `constof(anchor)` to
ordinary `const`, but metadata describes the Camp source contract, not the
erased C view.

For callable signatures, `constof(anchor)` anchors are source-level names in the
metadata view. Consumers comparing two callable signatures should resolve those
anchor names against the containing parameter list or receiver instead of
comparing raw text only; a declaration may rename `source` to `buffer` while
preserving the same positional dependent-const contract.

Property-eligible methods are identified with `propertyName`.

```json
{
  "name": "getLength",
  "returnType": "nuint",
  "propertyName": "Length"
}
```

Indexer properties also include:

- `propertyIndexer`: `true` for nameless indexers.
- `propertyIndexParams`: parameter names that participate in indexing.
- `propertyValueParam`: setter value parameter name.

The metadata intentionally does not emit `property: true` or
`propertyAccessor`. A method with `propertyName` is property-eligible.

Parameters may contain:

- `name`
- `modifier`: `in`, `out`, `within`, `upon`, or other current parameter
  modifiers.
- `type`
- `capability`: `sizeof`, `typenameof`, or another special capability marker
  for explicit runtime generic support parameters.
- `targetType`: source type named by a special capability parameter.
- `defaultValue`: source expression text for a default value.
- `overload`: `true` for overload selector parameters.
- `interfaceType`: for `vtableof` parameters.
- `metadata`

Default values are serialized as source text. They are not evaluated for
metadata output.

For async scheduler parameters, metadata preserves the declared `upon` parameter
shape. The bare shorthand:

```camp
async void run(upon scheduler)
```

is represented after binding as the source-level scheduler parameter selected by
the compiler, with the `upon` modifier and any default value preserved in the
same parameter record style as other parameters. Tools should still treat this
as an async scheduler slot, not as an ordinary user payload parameter.

For special capability parameters, `type` is the ordinary ABI-carried value
type, while `targetType` names the source type the capability describes. For
example:

```json
{
  "name": "typenameof_T",
  "capability": "typenameof",
  "type": "string",
  "targetType": "T"
}
```

Default values that use the type-name intrinsic are written using the current
source spelling, such as `"defaultValue": "typenameof(classtype)"`.

Generic type parameters may contain:

- `name`
- `constraint`
- `metadata`

## Inline Constant Metadata

Inline constants are emitted as variable or field records with:

- `inline`: `true`
- `value`: the compiler-computed constant value

Ordinary variables, including `const` variables, do not include a computed
`value` property. Enum values are the other declaration child that includes a
computed `value`.

Example:

```json
{
  "kind": "variable",
  "name": "MAX_PLAYERS",
  "type": "uint",
  "inline": true,
  "value": 9
}
```

Type-scoped inline constants appear in the owning type's `fields` array because
they are source-level members of that type. They also carry `static: true`,
because they have no per-instance storage.

```json
{
  "kind": "struct",
  "name": "Limits",
  "fields": [
    {
      "name": "DEFAULT_CAPACITY",
      "symbol": "Limits_DEFAULT_CAPACITY",
      "type": "uint",
      "inline": true,
      "value": 16,
      "static": true
    }
  ]
}
```

## Alias Metadata

Aliases contain:

- `target`: source-level target name.
- `resolvedTarget`: resolved target name when binding produced one.
- `targetKind`: when the compiler can determine it.
- `targetRef`: id of the emitted or stubbed target declaration, when available.

`targetKind` may be:

- `type`
- `function`
- `method`
- `newtype`
- `alias`
- `callspec`
- `typespec`
- `primitive`

Example:

```json
{
  "kind": "alias",
  "name": "WidgetAlias",
  "target": "Widget",
  "resolvedTarget": "Widget",
  "targetKind": "type",
  "targetRef": "struct:Docs::Widget"
}
```

If the resolver cannot cheaply determine `targetKind` or `targetRef`, the
compiler omits those properties instead of blocking metadata emission.

## Consumer Guidance

Metadata consumers should:

- check `format` and `version` first;
- treat `id` and `ref` as opaque ids;
- tolerate missing optional properties;
- use `kind` only where it is present;
- use container names to infer obvious child kinds such as fields, parameters,
  and enum values;
- render metadata attributes by `name`, `content`, and optional `symbols`;
- resolve `symbols` by `ref` against `declarations` and `stubs`;
- avoid assuming generated C names unless the `symbol` property is present.

Consumers should not expect metadata JSON to include lowered params components,
hidden receiver/context parameters, generated iterator state types, generated
lifecycle helpers, generated interface thunks, hidden vtables, function bodies,
local declarations, or inactive conditional branches.

## Authoring Guidance

Prefer `///` line doc comments for ordinary documentation.

Use plain text for summaries:

```camp
/// Represents a player.
export struct Player
{
}
```

Use explicit attributes for longer sections:

```camp
/// Parses an integer.
/// @remarks Leading and trailing whitespace are ignored.
/// @returns True when parsing succeeds.
export bool tryParse(const char[] this, out int result);
```

Use child targets for parameters and members:

```camp
/// Copies values.
/// - this: Source values.
/// - dest: Destination values.
export void copyTo<T: copyable>(const T[] this, T[] dest, sizeof(T));
```

Use `[Symbol]` links when the symbol is visible to the declaration:

```camp
/// Creates a [List] from these values.
export List<T>* copyList<T: copyable>(const T[] this, sizeof(T));
```

Use backticks when text must stay literal:

```camp
/// The token `@summary` is shown literally here.
```

Use fenced examples for code:

````camp
/// Example.
/// @example
/// ```camp
/// Console.writeLine("hello");
/// ```
export void example();
````
