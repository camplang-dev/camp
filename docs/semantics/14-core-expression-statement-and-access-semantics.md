# Core Expression, Statement, And Access Semantics

This supplement describes source forms that are not large enough to deserve
their own lowering family, but are still semantically important enough that a
compatible compiler must implement them exactly. The language guide introduces
these forms as everyday syntax. This document specifies the binding and
lowering contracts behind them.

The rules here connect body analysis to the larger supplements on expanded
forms, lifetimes, callables, construction, metadata, and C emission. Do not
treat them as parser conveniences: most of these forms affect overload
resolution, lifetime facts, generated temporaries, metadata, or ABI-visible
calls.

## Body-Analysis Ownership

Body analysis owns the source legality of these forms. Lowering may rewrite
them, but it should consume decisions already made by analysis rather than
rediscovering them:

- property and indexer access must resolve to a concrete accessor call;
- `@index` and `@range` arguments must be validated against the selected
  callable parameter list;
- omitted trailing `out` result binding must select exactly one callable shape;
- intentional discard targets must be recognized as write-only storage;
- source transfer statements must be checked before generated cleanup gotos
  obscure the original control flow.

These decisions need source ranges. A lowered temporary, generated call, or
generated label should keep provenance back to the property access, range
argument, declaration target, discard target, or transfer statement that caused
it.

## Property Accessor Binding

Property access is syntactic sugar for method calls. A compatible compiler must
bind the accessor during body analysis and lower the access to a call that
preserves receiver, argument, generic, lifetime, `within`, `thrown`, and
`constof` behavior.

Getter candidates are methods named `getX`, where `X` is the property name.
They must return a non-`void` result. A getter-compatible method that omits an
explicit receiver uses an implicit `const this` receiver. This is why a `get`
method can usually be called through a const receiver even when an ordinary
method with the same omitted receiver would not be.

Setter candidates are methods named `setX`, where `X` is the property name.
The last ordinary source parameter is the assigned value. Ordinary parameters
exclude `this`, `within`, `sizeof`, `typenameof`, `vtableof`, `out`, and
`thrown` parameters. Any ordinary parameters before the value parameter are
indexer parameters. If the value parameter is an expanded form, its trailing ABI
components still represent one source value parameter.

Accessor lookup accepts both the overload-family invoker name and a concrete
overload callable name. For an overload family `setElement(@index nuint index,
overload string value)`, the named property surface `Element` binds through the
family invoker `setElement`, while the concrete property surface
`ElementString` binds through the concrete callable name `setElementString`.

The nameless `get` and `set` forms are indexer accessors. In metadata these are
recorded as property indexers rather than as a property with an empty source
name.

```camp
struct Buffer
{
	byte* data;
	nuint length;

	byte get(@index nuint index);
	void set(@index nuint index, byte value);
}
```

The expression `buffer[index]` binds to `get(index)`. The assignment
`buffer[index] = value` binds to `set(index, value)`.

When a setter overload selector is the value parameter, the assigned expression
is the logical selector argument. Selection is still based on the expression's
independent static type. The compiler must reject target-typed-only selector
expressions such as `null`, `default`, aggregate initializers, and untyped
lambdas unless the caller selects a concrete accessor surface or supplies an
explicit cast.

```camp
json.Element[0] = true;          // selects setElementBool
json.Element[1] = null;          // invalid: no independent selector type
json.Element[1] = (string)null;  // selects setElementString
json.ElementString[1] = null;    // selects the concrete accessor
```

If the setter has `@index` or `@range` parameters before a late selector,
analysis expands those arguments before overload selection so the selector's
callable position is compared against the same source parameter shape used by
ordinary calls.

## Property Assignment Lowering

Property assignment is a call, not direct storage mutation. Lowering must
preserve source expression semantics:

- the receiver, indexer arguments, and assigned value are evaluated once;
- a statement-form assignment may lower directly to the setter call;
- an expression-form assignment evaluates the setter and produces the assigned
  value as the assignment expression's result;
- lifetime and constness checks are applied to the setter call surface, not to
  a nonexistent field.

If preserving the assignment value would duplicate a side effect, lowering must
materialize the assigned value in a generated temporary, pass that temporary to
the setter, and use the same temporary as the expression result.

Property access does not make a member field visible. If a real field/member
access is chosen, it remains field/member access. If a property accessor is
chosen, all source behavior follows the accessor function.

## Index-Aware Parameters

`@index` marks an integral parameter as accepting ordinary index syntax and
from-end `^` syntax. The marker belongs to the parameter in the callable
surface, not to the caller's expression.

Validation rules:

- `@index` may be used only on an integral callable parameter;
- a `^` default value is valid on an `@index` parameter;
- caller `^expr` syntax requires a receiver with an accessible length;
- the from-end argument lowers to `length - expr`.

The receiver length may come from fixed-array length, `.length`, `.Length`, or
an accessible `getLength()` method/property-compatible function. If no length
surface is available, the diagnostic belongs on the from-end syntax or the
default that requires it.

## Range-Aware Parameters

`@range` marks the first parameter of an index/count pair. The marked parameter
and the following count parameter must both be integral.

When the caller supplies a range argument, analysis expands it into two
arguments:

- start index;
- element count.

The source range boundaries use the receiver length:

- omitted start defaults to `0`;
- omitted end defaults to `length`;
- `^expr` lowers to `length - expr`;
- boundaries are clamped to the closed interval from `0` to `length`;
- count is computed from the clamped end and clamped start.

The generated count must not be negative. On an unsigned target this is still a
semantic rule: lowering should compute the count in a way that represents
`max(end - start, 0)` rather than relying on unsigned wraparound.

The count parameter may use a `^` default only when paired with a preceding
`@range` parameter. This is how a callable can support defaults such as "from a
start index through the end of the receiver".

## Accessors, Ranges, And Generic Arrays

Accessor and range lowering composes with generic erased arrays. A range over
`T[]` still needs the element stride when the lowered form computes element
addresses, copies values, slices, or iterates. `@index` and `@range` do not
transport `sizeof(T)`; a generic callable that needs element stride must declare
or receive the capability described in
[Generics, Erasure, And Capabilities](06-generics-erasure-and-capabilities.md).

The array and string syntax in the language guide is a source convenience over
these same rules. Lowering must eventually produce the component-level pointer,
length, and stride operations described in
[Expanded Forms And ABI Shapes](02-expanded-forms-and-abi-shapes.md).

## Interpolated Strings And Textual Composition

Interpolated strings and textual `+` composition are source forms for building
formatted text without implicit allocation. Runtime text composition produces a
formatter delegate value. Constant text remains ordinary string-like constant
text.

### Interpolated String Syntax

An interpolated string begins with adjacent `$"` characters and ends with the
next unescaped double quote that belongs to the outer literal. It contains an
ordered sequence of literal text segments and interpolation holes. Each hole
begins with `{` and ends with its corresponding `}`.

Literal text uses the same escapes as ordinary double-quoted Camp strings. In
literal text, `{{` represents one literal `{` and `}}` represents one literal
`}`. A single unmatched `}` is a source error.

Within a hole, ordinary Camp expression tokens and trivia are permitted.
Balanced delimiters belonging to nested expressions do not close the hole.
Braces and quotes inside nested string, character, comment, initializer, and
lambda syntax follow their ordinary lexical rules. The closing `}` is not part
of the contained expression.

These forms are invalid:

- an empty or trivia-only hole;
- an unmatched single `}`;
- an unterminated hole;
- an unterminated interpolated string;
- a physical CR, LF, or CRLF before the closing outer quote.

Formatting suffixes such as `{value:pattern}` and alignment suffixes such as
`{value,10}` have no special meaning. A colon or comma is accepted only when it
is valid in the contained Camp expression.

The semantic expression model is one interpolated string expression with an
ordered list of text and expression segments. Diagnostics should retain source
ranges for the whole expression, each segment, each brace pair, and each
contained expression so later binding and lowering errors can point at the
responsible hole.

### Constant Text

Before runtime formatter inference, the compiler evaluates whether the
interpolated string is fully constant text. An interpolation is constant text
when every literal segment is constant text and every hole is a compile-time
string-like or character constant. Inline string constants participate:

```camp
inline string PREFIX = "Camp";
inline string TITLE = $"{PREFIX} compiler";
```

User `format` methods are not executed during constant evaluation. Numeric,
enum, newtype, aggregate, and other non-text constants therefore prevent
constant-text folding unless they are already represented as string-like or
character constants.

With `auto`, constant text infers as the equivalent plain string literal. An
explicit `string`, `wstring`, `astring`, const character pointer, counted text,
or fixed character storage target is valid only when the entire interpolation is
constant text. An explicit formatter target remains a formatter target even when
all segments are constant; the compiler may lower it as a constant-backed
formatter.

Constant textual `+` composition follows the same rule:

```camp
auto title = "Camp " + "compiler"; // string
```

### Formatter Targets

A runtime interpolated string or runtime textual composition has a formatter
target. The target must be either an anonymous `delegate` type or a callable
newtype whose underlying callable kind is `delegate`.

The callable shape must have:

1. exactly one non-`this` parameter;
2. a mutable array parameter whose element type, after ordinary qualification
   handling, is `char`, `wchar`, or `achar`;
3. a return type exactly equal to that array parameter's expanded length
   component type, including any target specs;
4. no `thrown` parameter;
5. reusable delegate behavior rather than `fn`, `once`, `async`, or iterator
   behavior.

The standard UTF-8 formatter type is ordinary standard-library source, not a
compiler-owned symbol:

```camp
public newtype delegate nuint StringFormatter(
	const this,
	char[] buffer = default);
```

The buffer default belongs on the delegate declaration. A `format` overload
selector does not declare that default value.

The return type must match the expanded array length component exactly.
Physical width equality is not sufficient. For example, `uint` is not a match
for `nuint` merely because both are 32-bit on one target. Target specs also
remain semantically distinct.

### Formatter Contract

Every eligible formatter follows this contract:

- The return value is the complete required buffer size in array elements,
  including one null terminator.
- If the provided buffer is large enough, the formatter writes the complete
  formatted content followed by the null terminator.
- If the provided buffer is too small, the formatter must not write outside the
  buffer. Its returned contents are otherwise unspecified, except that if it
  writes anything, the written result must be null-terminated within the buffer.
- A return value of zero is permitted and means the formatter contributes no
  content and no terminator.
- The formatter has no `thrown` channel.
- Size-query and write calls over unchanged captured state must agree on the
  required size and formatted content.

The compiler checks the callable shape, not the full behavioral contract.
Declaring an eligible formatter target or `format` method asserts that the
implementation obeys the contract.

### Formattable Values

A non-formatter interpolation component is formattable when ordinary instance
member lookup finds an eligible method named exactly `format`.
Receiver-style extension functions, inherited methods, virtual methods, and
interface members participate through ordinary Camp lookup and dispatch rules.

An eligible `format` method:

1. has exactly one non-`this` parameter;
2. uses that buffer parameter as its `overload` selector;
3. accepts the mutable character-array type required by the selected formatter;
4. returns the exact length-component type of that array;
5. has no `thrown` slot;
6. has receiver and lifetime requirements compatible with binding a method
   reference into the composed formatter's scoped delegate;
7. is ascribed to the required callable newtype when the formatter target is a
   named callable newtype.

Example:

```camp
public struct Status
{
	bool enabled;
}

public nuint format(
	in Status this,
	overload char[] buffer) : StringFormatter
{
	const char[] text = this.enabled ? "enabled" : "disabled";
	nuint required = text.length + (nuint)1;

	if (buffer.length >= required)
	{
		for (nuint index = 0; index < text.length; index++)
			buffer[index] = text[index];

		buffer[required - (nuint)1] = '\0';
	}

	return required;
}
```

When the target is an anonymous delegate, callable ascription cannot name the
target. In that case an un-ascribed `format` method is eligible when its bound
method reference is implicitly compatible with the anonymous delegate shape.

If an interpolation component already resolves to the required formatter type,
it is incorporated directly. The compiler does not perform `format` lookup on
that value. For an anonymous target, ordinary implicit callable compatibility
determines whether the component can be incorporated directly.

### Formatter Inference And Explicit Targets

When runtime formatting is required and no explicit formatter target is known,
the first runtime interpolation component establishes the formatter target:

1. Analyze the first runtime component once to determine its independent static
   type.
2. If it already resolves to an eligible formatter delegate or callable newtype,
   infer that type.
3. Otherwise, perform ordinary instance lookup for `format`.
4. For automatic inference, retain only candidates whose buffer parameter is
   mutable `char[]` and whose return type is that array's exact length-component
   type.
5. If exactly one candidate remains, infer its ascribed callable newtype, or an
   anonymous context-carrying delegate when it has no callable ascription.
6. If no candidate remains, report that the first runtime component does not
   establish a UTF-8 formatter type.
7. If several candidates remain, report ambiguity and require an explicit
   formatter target.
8. Bind every remaining runtime component against the inferred target.

Automatic inference considers UTF-8 `char[]` targets only. `wchar[]` and
`achar[]` formatter families require an explicit target.

An explicit formatter target may be supplied by a declaration, assignment,
return context, cast, selected ordinary parameter, or selected overload
parameter:

```camp
StringFormatter message = $"Status: {status}";
LibraryFormatter record = (LibraryFormatter)$"Record: {recordId}";
```

For a named callable newtype target, each non-direct component must bind a
`format` method ascribed to that exact newtype. Matching the raw callable
signature is not enough to cross a nominal callable-newtype boundary. For an
anonymous delegate target, each component must be structurally and implicitly
compatible with the anonymous signature.

The target's array element type determines the code-unit representation of
literal segments and the required buffer overload for every formatted value.
The compiler does not implicitly transcode between `char`, `wchar`, and
`achar` formatter families.

An interpolated literal with no holes behaves as an ordinary string literal
under `auto`:

```camp
auto text = $"ready"; // string
```

With an explicit formatter target, literal-only and empty interpolations produce
formatters. An empty formatter still requires a one-element buffer for its
terminator.

### Runtime Evaluation And Lowering

Runtime interpolation and runtime textual composition evaluate every dynamic
component exactly once, from left to right, when the source expression is
evaluated. The evaluated results are captured according to ordinary Camp
callable, receiver, and lifetime rules. Character formatting is deferred until
the produced formatter is invoked.

For:

```camp
auto message = $"{first()} / {second()}";
```

`first()` runs before `second()`. If `first()` exits through a `thrown` path,
`second()` is not evaluated and no formatter value is produced. Formatter
invocation itself has no `thrown` channel.

Lowering produces an ordinary callable/context pair compatible with the
selected formatter target. Interpolation nodes must be gone before final C
expression emission.

The composite formatter's size pass:

- starts with one code unit for the final null terminator;
- adds literal segment code-unit counts;
- calls each dynamic component formatter for its required size;
- adds `required - 1` for each dynamic component whose reported size is
  nonzero.

Conceptually:

```text
required = 1
required += literal code units
required += max(component required - 1, 0)
```

The arithmetic uses the formatter target's exact array-length type and carrier
domain. Compile-time-known unrepresentable totals are errors. Runtime overflow
must fail before allocation or buffer writing through the target's ordinary
bounds-failure mechanism; interpolation does not add a `thrown` result.

When the output buffer is large enough, the write pass writes literal and
dynamic segments in source order. Each dynamic formatter receives a slice
beginning at the current output offset. After a nonzero component, the offset
advances by `required - 1`, so the next segment overwrites the intermediate
terminator. The composite writes one final terminator.

When the output buffer is too small, the composite follows the same
insufficient-buffer contract as ordinary formatters: it must not write outside
the provided buffer, and the returned contents are otherwise unspecified except
for null termination if anything is written.

Runtime interpolation is scoped unless existing callable and capture rules prove
a stronger valid lifetime. Returning, storing, or otherwise escaping a composed
formatter is governed by ordinary delegate-context ownership and lifetime
diagnostics. A scoped formatter may cross an async suspension only when existing
async-frame lifetime rules allow the captured state to remain in the frame
without escaping the async method.

### Textual `+`

Binary `+` becomes textual composition when either operand is a textual anchor:

- a string literal or primitive string value;
- a compatible counted character view;
- an interpolated string;
- a compatible formatter delegate.

Textual composition has the same target inference, explicit-target binding,
component formatting, evaluation, lifetime, and lowering semantics as an
equivalent interpolated string. Ordinary numeric `+` is unchanged when neither
operand is textual. `char + char` and other numeric operations remain arithmetic
unless a textual anchor makes that part of the expression a formatter
composition.

`+` remains left-associative:

```camp
"Total: " + 1 + 2;     // formats Total: 12
1 + 2 + " total";      // formats 3 total
"Total: " + (1 + 2);   // formats Total: 3
```

Assigning runtime textual composition directly to a primitive string is invalid
because it would require hidden allocation:

```camp
string name = getName();
string message = "Hello, " + name; // ERROR
```

Materialization must be explicit:

```camp
string message = ("Hello, " + name).copyString() finally delete;
```

Textual `+=` has no formatter-composition semantics. The compiler must
recognize attempted textual `+=` and report a direct diagnostic explaining that
runtime text composition produces a formatter rather than mutating or allocating
a string.

### Overload Selection

When an interpolated string or textual composition is supplied as the selector
argument of an overload family, the compiler first counts selector candidates
whose selector type is an eligible formatter target:

- If exactly one formatter-shaped selector candidate exists, that candidate
  supplies the explicit formatter target. Other non-formatter overloads do not
  make the call ambiguous.
- If more than one formatter-shaped selector candidate exists, the call is
  ambiguous. The compiler reports the formatter-target ambiguity before using
  any interpolation hole content.
- If no formatter-shaped selector exists, ordinary overload failure rules apply.

Interpolation holes and textual-composition components must not be used to rank
or choose between distinct formatter families. A cast or a concrete flattened
overload name can supply the target explicitly:

```camp
write((StringFormatter)$"Record: {recordId}");
writeStringFormatter($"Record: {recordId}");
```

### Diagnostics, Metadata, And API Headers

Diagnostics should prefer the responsible hole, component expression, textual
composition expression, or selected argument range rather than a lowered helper.
Required diagnostic classes include:

- malformed interpolation syntax;
- runtime interpolation assigned to a primitive string target;
- no formatter candidate for the first runtime component;
- multiple formatter candidates for the first runtime component;
- incompatible explicit formatter target shape;
- component `format` methods with the wrong buffer type, return type, callable
  kind, thrown slot, receiver, lifetime, or callable ascription;
- direct formatter components incompatible with the target;
- multiple formatter-shaped overload selector candidates;
- attempted textual `+=`.

Interpolation syntax and textual composition are source expressions, not API
surface declarations. Metadata and API headers record ordinary resolved types,
callable newtypes, default values, selected functions, and formatter method
dependencies. They must not invent a dependency on the standard library
`StringFormatter`; that dependency appears only when source code actually uses
the standard-library type.

## Source Capture Default Arguments

Source capture is a default-argument feature for APIs that need caller
information without forcing ordinary callers to write it by hand. The compiler
recognizes these intrinsic forms only in parameter default value expressions:

```camp
caller(sourceline)
caller(sourcefile)
caller(propertyname)
caller(functionname)
caller(qualifiedname)
sourceof(argumentName)
```

`caller` is contextual. It is treated as an intrinsic only when used in this
call shape inside a default parameter expression. Other uses of the name
`caller` are ordinary source lookup.

The `caller(...)` selector is not an expression. It must be exactly one of the
listed selector names. `sourceof(...)` accepts exactly one unqualified parameter
name from the same signature; arbitrary expressions are invalid.

During direct-call default insertion, after overload resolution has selected a
concrete callable and bound supplied arguments to parameters, lowering replaces
source-capture defaults with ordinary literal expressions:

- `caller(sourceline)` becomes a `uint` literal for the 1-based line of the
  source statement containing the call.
- `caller(sourcefile)` becomes a string literal for the caller source file.
- `caller(functionname)` becomes the visible source function or member name of
  the caller. Instance members, static members, and out-of-scope members all use
  the visible function name. Overload suffixes are included. Constructors use
  `create`; destructors use `destroy`.
- `caller(qualifiedname)` becomes `[Namespace::][Type.]functionName`. The
  namespace uses `::`, the type/member separator uses `.`, overload suffixes are
  included, and constructors/destructors use `create`/`destroy`.
- `caller(propertyname)` is supplied only when the call appears inside a getter
  or setter body that Camp recognizes as property-accessible. The value is the
  property accessor name, without the `get` or `set` prefix. Outside such a
  body, this default is not supplied, so the caller must pass the argument
  explicitly.
- `sourceof(argumentName)` becomes a string literal containing the normalized
  source text that the caller explicitly wrote for that argument. If the
  referenced argument was also omitted and supplied by a default, the value is
  the empty string.

Source text capture uses the caller-written argument syntax before property,
index, range, `out`, and expanded-form lowering. It removes comments, collapses
whitespace outside literals, trims leading/trailing whitespace, preserves
literal spelling, and keeps whitespace where required to avoid merging adjacent
identifier or number tokens.

`caller(sourcefile)` uses the compiler request's sourcefile path policy. The
default policy is relative. In relative mode, files are relative to the active
`.campbuild` directory when a build file is the request entry point, or to the
request working directory for loose source-file builds. One or more
`--sourcefile-root` values replace that default root; the longest matching root
is selected. If a source file is outside every selected root, the compiler
diagnoses the omitted argument instead of inventing a path. If two distinct
source files would produce the same relative sourcefile string, the compiler
diagnoses the collision. `--sourcefile-paths absolute` emits absolute paths and
ignores `--sourcefile-root`.

## Omitted Trailing `out` Result Binding

Camp allows certain calls with trailing `out` parameters to bind as values at
the call site. This is not tuple return. It is caller-storage synthesis for
trailing `out` slots whose values are immediately consumed by a supported
source form.

The compiler may synthesize omitted trailing `out` storage when the call is
used by:

- a declaration target;
- a deconstruction declaration target;
- a supported immediate member/component selection over the call result.

The target callable must be unambiguous after overload resolution and generic
inference. If more than one callable could explain the omitted `out` storage,
the compiler must diagnose rather than guessing.

Lowering creates local storage for each omitted trailing `out` slot, supplies
that storage to the call, and rewrites the surrounding source form to read the
generated storage. The generated locals inherit the slot types, lifetimes,
`constof` results, and cleanup requirements of the callee signature.

`thrown` parameters are not ordinary `out` results. They participate in the
error path described in the error/cleanup and expanded-forms supplements.

## Intentional Discard

`_` is an intentional discard target, not a readable local variable. It may
appear where the language expects write-only storage, such as an assignment
target, an `out` argument target, or a catch/deconstruction target that ignores
one produced value.

Each discard use lowers to fresh generated storage. Generated names such as
`__discard0` are implementation details; they must not become source-visible
lookup names, metadata names, or reusable locals.

Diagnostics should reject attempts to read `_` or declare an ordinary variable
whose meaning would conflict with the discard target. A discard target should
still be type-checked: the value written into it must be assignable to the slot
shape that the surrounding operation requires.

## Labels, `goto`, And Cleanup

Labels and source `goto` are low-level control-flow tools. They exist inside a
function body and must not be confused with compiler-generated labels used to
lower cleanup or state machines.

`return`, `break`, and `continue` are structured transfers. When they leave a
scope that owns pending cleanup, lowering must run that cleanup on the way out.
Lowering stores result/transfer state where needed and jumps through generated
cleanup labels.

Source `goto` is different. A source `goto` is not wrapped with pending cleanup
by the `finally` lowering path. Therefore a `goto` that exits a `try`/`finally`
region can bypass the `finally` body. A compiler that wants to warn about this
should do so during flow analysis, while the source structure is still visible.
It must not silently rewrite source `goto` into structured cleanup transfer.

## Conditions And Truth Values

Conditions use `bool`. The compiler should not treat integers, pointers,
enums, optionals, or raw carriers as implicitly truthy merely because the C
emitter could spell such a condition. If a conversion is required, it must be
an ordinary accepted Camp conversion at the source expression.

This rule is intentionally source-level. C emission may lower `bool` to the
target's C spelling, but it should not reopen truthiness decisions during
emission.

## Aggregate Initializers As Arguments

Aggregate initializers may be used as call arguments only when the target
parameter gives the compiler a safe storage rule. For an `in T` parameter, the
compiler materializes a temporary `T`, initializes it from the aggregate, and
passes the temporary by address. For a `const T*` parameter, the compiler
materializes a temporary `const T`, initializes it from the aggregate, and
passes its address.

An aggregate initializer is not a mutable lvalue. A call such as
`mutate({ ... })` where `mutate` expects `T*` must be rejected during analysis
with a diagnostic that tells the programmer to initialize a local and pass its
address. Lowering must never leave a raw C aggregate initializer in argument
position, because that is not portable to all supported C targets.

## Metadata And API

Metadata records property accessor facts structurally:

- getter or setter accessor kind;
- property name when present;
- indexer marker for nameless accessors;
- index parameter names;
- setter value parameter name.

The metadata model should preserve source accessor declarations rather than
inventing field declarations. `@index` and `@range` remain parameter attributes
on the source callable surface. Omitted `out` locals, range temporaries,
discard locals, and cleanup labels are lowering artifacts and should not appear
as ordinary source metadata declarations.

For overloaded accessors, metadata records the concrete accessor property name.
For example, the concrete callable `setElementString` records property name
`ElementString`, while the source function name remains `setElement` and the
selector parameter remains marked as `overload`.

## Diagnostics

Important diagnostic categories include:

- property exists but the receiver cannot call the accessor;
- property setter assignment has no compatible setter;
- property setter overload selection fails because the assigned value has no
  independent selector type;
- `@index` or `@range` on a non-integral parameter;
- `@range` not followed by an integral count parameter;
- range syntax used against a parameter that is not `@range`;
- from-end syntax used without an accessible receiver length;
- omitted trailing `out` result binding is ambiguous or unsupported in the
  surrounding form;
- attempt to read an intentional discard target;
- aggregate initializer passed directly to a mutable pointer parameter;
- source `goto` crosses cleanup in a way the compiler diagnoses.

Diagnostics should point at the source syntax that introduced the special
meaning: the property access, assignment target, attribute, range expression,
from-end expression, omitted call result, discard target, aggregate
initializer, or source `goto`.

## Test Surface

Changes here should cover:

- getter and setter access, including const receiver behavior;
- property assignment in statement and expression positions;
- nameless indexers and named indexed properties;
- `@index`, `@range`, from-end syntax, omitted range bounds, and defaults;
- receiver length lookup through fixed arrays, `.length`, `.Length`, and
  `getLength()`;
- generic array access that requires `sizeof(T)`;
- omitted trailing `out` declaration and deconstruction binding;
- intentional discard lowering and invalid discard reads;
- source `goto` interaction with `try`/`finally` cleanup.

## Implementation Anchors

Primary implementation points include:

- `BindableNodeAnalyzer.MethodBody.Semantics.cs` for property/accessor binding,
  receiver compatibility, and length lookup;
- `BindableNodeAnalyzer.Lowering.Expressions.cs` for property getter/setter,
  assignment, and index lowering;
- `BindableNodeAnalyzer.Lowering.Slices.cs` for `@index`, `@range`,
  from-end, and range argument lowering;
- `BindableNodeAnalyzer.MethodBody.cs` for omitted trailing `out` result
  binding, discard recognition, delete/transfer analysis, and diagnostics;
- `BindableNodeAnalyzer.Lowering.Operators.cs` for discard target lowering;
- `BindableNodeAnalyzer.Lowering.Exceptions.cs` for cleanup transfer rewriting;
- `MetadataJsonSerializer.cs` for property metadata fields.
