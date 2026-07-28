# Interpolated Strings And Formatter Composition

Status: Pending  
Proposal date: 2026-07-27  
Last updated date: 2026-07-27

## Summary

This proposal adds C#-style interpolated strings to Camp:

```camp
auto message = $"Player {playerNumber}: {playerName}";
Console.writeLine($"Completed {completedCount} of {totalCount}");
```

An interpolated string is not inherently a `string` and does not inherently
allocate. When runtime formatting is required, it produces a scoped delegate
that writes the completed, null-terminated text into a caller-provided
character buffer. The delegate's return value is the complete required buffer
size, including the null terminator.

The formatter delegate type is normally inferred from the first interpolation
expression. An explicit target may instead select any compatible formatter
delegate or callable newtype:

```camp
StringFormatter message = $"Player {playerNumber}: {playerName}";
LibraryFormatter record = $"Record {recordId}";
```

Camp does not hard-code `StringFormatter` or any other library formatter type.
It recognizes a formatter protocol expressed through ordinary delegate
signatures, instance methods named `format`, overload selectors, and callable
ascription. The standard library supplies `StringFormatter` as its UTF-8
formatter type.

The same formatter-composition behavior is available through `+`, while
constant string concatenation remains an ordinary compile-time string
operation:

```camp
auto constantText = "one" + "two";       // string, folded to "onetwo"
auto dynamicText = "Player " + playerNumber;
```

## Motivation

Native programs constantly assemble text from a mixture of literal phrases and
non-text values:

- console output and diagnostic messages;
- log records;
- UI labels;
- file names and paths;
- protocol commands and request text;
- generated source, configuration, and markup;
- assertions and test failure messages.

Camp can already format individual primitive and standard-library values into
caller-provided buffers. Without language composition, however, callers must
manually calculate storage, allocate or initialize a buffer, format each value,
copy literal segments, manage offsets, and preserve the final null terminator.
Alternatively, an API can accumulate several separate writes, but that is not
equivalent to constructing one reusable piece of formatted text.

The common source intent is much smaller:

```camp
Console.writeLine($"File {path} contains {byteCount} bytes");
```

This proposal makes that intent direct without requiring hidden string
allocation. It preserves the properties expected of native formatting APIs:

- the caller can query the exact required buffer size;
- fixed or stack storage can be used when appropriate;
- allocation remains explicit when an owned string is required;
- formatting can be passed directly to console, stream, logging, or native
  library APIs;
- the formatter ABI remains an ordinary Camp delegate ABI;
- target-specific pointer and natural-integer widths remain visible in the
  formatter signature.

The design also permits a library to define its own formatter type. A
no-standard-library program or a specialized native library does not need to
adopt a compiler-owned or standard-library-owned nominal type merely to use
interpolation syntax.

## Goals

- Add compact `$"..."` syntax for composing text and formatted values.
- Avoid implicit allocation for runtime interpolation.
- Preserve exact required-buffer sizing, including null termination.
- Evaluate every interpolation expression exactly once and in source order.
- Infer a useful formatter type without hard-coding a standard-library symbol.
- Allow explicit formatter targets for other libraries and character types.
- Reuse Camp's existing overload, callable-ascription, delegate, target-spec,
  lifetime, and async-frame rules.
- Fold constant text and inline string constants at compile time.
- Give textual `+` composition the same runtime semantics as interpolation.
- Keep overload resolution deterministic and prevent interpolation contents
  from choosing among formatter overloads.
- Produce focused diagnostics for missing, ambiguous, and incompatible
  formatter methods.

## Non-Goals

This proposal does not add:

- multiline string literals;
- alignment or format-specifier clauses inside interpolation holes;
- implicit allocation of a `string`, `wstring`, or `astring`;
- a compiler-owned formatter or general sequence type;
- a compiler-owned standard-library namespace or formatter name;
- arbitrary compile-time execution of user `format` methods;
- general target-typed overload resolution for lambdas, `default`, `null`, or
  other weakly typed expressions;
- implicit conversion between `char`, `achar`, and `wchar` formatter families;
- formatter-composition semantics for textual `+=`; attempted textual `+=`
  receives a specific diagnostic rather than a generic arithmetic error;
- a requirement that the initial standard library provide wide-character or
  system-code-page formatter newtypes.

## Everyday Use

### Basic Interpolation

An interpolation expression is written inside braces in a `$"..."` literal:

```camp
auto greeting = $"Hello, {name}.";
auto progress = $"Processed {completedCount} of {totalCount}.";
auto indexed = $"Selected value: {values[selectedIndex]}";
```

Ordinary Camp expressions are allowed inside the braces. The expression is
evaluated when the interpolated string expression is evaluated. Its text is
written later when the resulting formatter is invoked.

### Escapes And Braces

Interpolated strings use the same character escapes as ordinary double-quoted
Camp strings:

```camp
auto message = $"Name: {name}\nStatus: {status}";
```

Write doubled braces for literal braces:

```camp
auto message = $"Set {{name}} to {value}";
```

An interpolated string ends at the closing double quote and cannot contain a
physical newline. Split long or multi-line text with `+`:

```camp
StringFormatter message =
	$"Line 1: {firstValue}\n" +
	$"Line 2: {secondValue}\n";
```

### Writing And Materializing

APIs can accept formatter values directly:

```camp
Console.write($"Loading {path}...");
Console.writeLine($"Loaded {byteCount} bytes");
```

Creating an owned string remains explicit:

```camp
string label = $"Player {playerNumber}".copyString();
```

The standard-library helper determines the required size, allocates that exact
size, invokes the formatter, and returns the resulting null-terminated string.

### Constant Text

Text that is completely known at compile time remains an ordinary string:

```camp
auto name = $"Camp";                  // string
auto joined = "one" + "two";         // string: "onetwo"

inline string PRODUCT = "Camp";
inline string HEADING = $"Using {PRODUCT}";
```

This makes it possible to split or interpolate constant text without changing
its type or introducing runtime formatting.

### Explicit Formatter Types

Most programs use inference:

```camp
auto message = $"Total: {total}";
```

State the type when a particular library or character representation is
required:

```camp
StringFormatter message = $"Total: {total}";
LibraryFormatter record = $"Record: {recordId}";
```

Each inserted value must support the selected formatter type. A diagnostic
identifies the interpolation expression that does not.

## Lexical And Syntactic Form

An interpolated string begins with adjacent `$"` characters and ends with the
next unescaped double quote that belongs to the outer literal.

Its contents alternate between:

- literal text segments;
- interpolation holes beginning with `{` and ending with the corresponding
  `}`.

Within literal text:

- ordinary Camp double-quoted-string escapes retain their ordinary meanings;
- `{{` represents one literal `{`;
- `}}` represents one literal `}`;
- an unmatched single `}` is an error;
- a physical CR, LF, or CRLF ends the token with an unterminated-string
  diagnostic.

Within a hole:

- ordinary Camp expression tokens and trivia are permitted;
- balanced braces belonging to nested expressions do not close the hole;
- braces and quotes inside nested string, character, comment, initializer, and
  lambda syntax follow their ordinary lexical rules;
- an empty or trivia-only hole is an error;
- the closing `}` is not part of the contained expression.

Formatting suffixes such as `{value:pattern}` and alignment suffixes such as
`{value,10}` are not part of this proposal. A colon or comma is accepted only
when it is valid in the contained Camp expression.

The parser should represent an interpolated string as one expression with an
ordered list of literal and expression segments. It should retain individual
source ranges for the entire literal, every segment, every brace pair, and
every contained expression so later diagnostics can point at the responsible
hole.

## Formatter Protocol

### Eligible Formatter Target

A runtime interpolated string has a formatter delegate target. The target must
be either:

- an anonymous `delegate` type; or
- a callable newtype whose underlying callable family is `delegate`.

The callable shape must have:

1. exactly one non-`this` parameter;
2. a mutable array parameter whose element type, after ordinary qualification
   rules, is `char`, `achar`, or `wchar`;
3. a return type equal to the length-component type of that array, including
   its target typespec;
4. no `thrown` parameter or result;
5. reusable delegate behavior rather than `fn`, `once`, `async`, or iterator
   behavior.

For an ordinary UTF-8 buffer, the return type is `nuint`:

```camp
public newtype delegate nuint StringFormatter(
	const this,
	char[] buffer = default);
```

For a target-specific array carrier:

```camp
char[] _near buffer
```

the expanded length component is `nuint _near`, so a compatible formatter
returns `nuint _near`:

```camp
public newtype delegate nuint _near NearStringFormatter(
	const this,
	char[] _near buffer = default);
```

Physical width equality is not sufficient. `uint` is not interchangeable with
`nuint` merely because both happen to be 32-bit on one target, and two
typespecs remain distinct even when they have the same configured width.

The buffer default belongs to the delegate declaration. A `format` overload
selector cannot declare a default value under Camp's ordinary overload rules.

### Required-Size Contract

Every eligible formatter obeys this semantic contract:

- The return value is the complete required buffer size in array elements,
  including one null terminator.
- If the provided buffer is large enough, the formatter writes the complete
  formatted content followed by the null terminator.
- If the buffer is too small, its contents on return are unspecified. The
  formatter must not write outside the buffer. If it writes anything, the
  written result must be null-terminated within the buffer.
- A return value of zero is permitted and means that the formatter contributes
  no content and no terminator.
- The formatter has no `thrown` channel.
- Size-query and buffer-writing calls over unchanged captured state must agree
  on the required size and formatted content.

The compiler cannot prove that a user implementation follows the size and
buffer-writing requirements. Declaring an eligible `format` method or
formatter target asserts that the implementation follows this language
contract, just as implementing another callable protocol asserts its behavioral
requirements.

### Formattable Values

A non-formatter interpolation value is formattable when ordinary instance
member lookup finds an eligible method with the exact, case-sensitive source
name `format`.

Receiver-style extension functions participate in this lookup, as do inherited,
virtual, and interface members through their ordinary Camp lookup and dispatch
rules.

An eligible `format` method:

1. has exactly one non-`this` parameter;
2. uses that buffer parameter as its `overload` selector;
3. accepts the mutable character-array type required by the formatter;
4. returns the exact length-component type of that array;
5. has no `thrown` slot;
6. has receiver and lifetime requirements compatible with binding a method
   reference into the interpolation's scoped delegate;
7. is ascribed to the required callable newtype when the formatter target is a
   named callable newtype.

For example:

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

The value then works naturally in interpolation:

```camp
Status status = { .enabled = true };
Console.writeLine($"Current status: {status}");
```

When the target is an anonymous delegate type, callable ascription cannot name
that anonymous type. In that case an un-ascribed `format` method is eligible
when its bound method reference is implicitly compatible with the target's
anonymous delegate signature.

### Direct Formatter Components

An interpolation expression that already resolves to the required formatter
type is incorporated directly:

```camp
StringFormatter detail = getDetailFormatter();
StringFormatter message = $"Result: {detail}";
```

The compiler does not perform `format` lookup on such a value. For an anonymous
target, ordinary implicit callable compatibility determines whether the
component can be incorporated directly.

## Type Inference

### Constant-Text Prepass

Before runtime formatter inference, the compiler attempts constant-text
evaluation.

An interpolated string is constant text when:

- every literal segment is an ordinary constant string segment; and
- every interpolation is a compile-time string-like or character constant
  whose text is defined without executing a user function.

References to inline string constants participate:

```camp
inline string PREFIX = "Camp";
inline string TITLE = $"{PREFIX} compiler";
```

User-defined `format` methods are not executed by constant evaluation. A
numeric, enum, newtype, or aggregate constant that would require a runtime
formatter therefore prevents constant-text folding unless a future language
rule defines its compile-time textual representation.

When `auto` is the target, constant text has the type that the equivalent plain
string literal would infer. An explicit `string`, `wstring`, or `astring`
target is permitted only when the entire interpolation is constant text. An
explicit formatter target remains that formatter type even if all segments
could otherwise be folded; the compiler may still use a constant-backed
formatter implementation.

### `auto` Inference

When runtime formatting is required and the target is `auto`, the first
interpolation expression establishes the formatter type.

The compiler applies these rules:

1. Analyze the first interpolation expression once to determine its independent
   static type.
2. If it already resolves to an eligible formatter delegate or callable
   newtype, infer that type.
3. Otherwise, perform ordinary instance lookup for `format`.
4. For automatic inference, retain only candidates whose single non-`this`
   overload-selector parameter is mutable `char[]`, with any applicable array
   carrier typespec, and whose return type is that array's exact
   length-component type.
5. If exactly one candidate remains:
   - infer its ascribed callable newtype when it has one;
   - otherwise infer an anonymous context-carrying delegate with the same
     return and buffer parameter types.
6. If no candidate remains, report that the first interpolation does not
   establish a UTF-8 formatter type.
7. If several candidates remain, report ambiguity and require an explicit
   formatter target.
8. Bind every remaining interpolation against the inferred target.

Automatic inference considers `char[]` only. A formatter using `achar[]` or
`wchar[]` requires an explicit target so that encoding is never inferred from
an incidental visible method.

The first interpolation is intentionally significant:

```camp
auto message = $"{recordId}: {status}";
```

If `recordId.format` establishes `LibraryFormatter`, `status` must either
resolve directly to `LibraryFormatter` or provide a `format` method ascribed to
`LibraryFormatter`.

Reordering values may change inference when several formatter ecosystems are
visible. State the target explicitly when the library contract matters:

```camp
LibraryFormatter message = $"{status}: {recordId}";
```

### Explicit Target

An explicitly typed initializer, cast, return context, or already selected
ordinary parameter can supply the formatter target:

```camp
StringFormatter message = $"Status: {status}";
LibraryFormatter record = (LibraryFormatter)$"Record: {recordId}";
```

For a named callable newtype target, every non-direct interpolation component
must bind a `format` method ascribed to that exact newtype. Merely having the
same raw callable signature is not enough to cross a nominal callable-newtype
boundary.

For an anonymous delegate target, every component must be structurally and
implicitly compatible with the anonymous signature.

The target's array element type determines the code-unit representation of
literal segments and the buffer overload required from every formatted value.
The compiler does not transcode components implicitly.

### Empty And Literal-Only Interpolation

With `auto`, an interpolated literal containing no holes infers exactly as an
ordinary string literal:

```camp
auto message = $"ready"; // string
```

With an explicit formatter target, the compiler produces a formatter backed by
the literal text:

```camp
StringFormatter message = $"ready";
```

An empty formatter still requires a one-element buffer for its terminator.

## Formatter Composition Semantics

### Evaluation

Interpolation expressions are evaluated:

- when the interpolated string expression is evaluated;
- exactly once each;
- from left to right.

Their evaluated results are captured according to ordinary Camp capture and
lifetime rules. Formatting those results into characters is deferred until
the produced formatter is invoked.

For:

```camp
auto message = $"{first()} / {second()}";
```

`first()` runs before `second()`. If `first()` exits through a `thrown` path,
`second()` is not evaluated and no formatter value is produced. Formatter
invocation itself has no `thrown` channel.

### Required Size

A composite formatter always reports at least one element for its own final
null terminator.

For each dynamic component:

- a component result of zero is skipped;
- a component result greater than zero contributes `result - 1` content
  elements because the component's terminator is shared with the composite.

Literal segments contribute their encoded code-unit counts without a
terminator. Conceptually:

```text
required = 1
required += literal code units
required += max(component required - 1, 0)
```

The arithmetic uses the formatter target's exact array-length type and carrier
domain. It must not silently wrap. A compile-time-known unrepresentable total
is diagnosed. A runtime overflow fails before allocation or buffer writing
through the target's ordinary bounds-failure mechanism; interpolation does not
add a `thrown` result for a size that cannot describe any valid buffer in that
carrier domain.

### Writing

When the output buffer is large enough, lowering writes segments in source
order. Each dynamic formatter receives a slice beginning at the current output
offset with sufficient space for that component's content and terminator.
After a nonzero component, the offset advances by `required - 1`, allowing the
next segment to overwrite the intermediate terminator. The composite writes
one final terminator.

The composite formatter follows the same insufficient-buffer contract as an
ordinary formatter: it never writes outside the provided buffer; otherwise the
buffer contents are unspecified, with null termination required if anything is
written.

### Lifetime

A runtime interpolated string is scoped unless ordinary callable and capture
rules prove a stronger valid lifetime. It may capture values, bound receivers,
array views, and formatter delegates only as permitted by Camp's existing
lifetime analysis.

A scoped formatter may survive an async suspension point when it is contained
in the async frame under the existing async-frame lifetime rules and does not
escape the async method. Interpolation does not add an independent prohibition
against crossing `await`.

Returning, storing, or otherwise escaping a formatter remains subject to
ordinary delegate-context ownership and lifetime diagnostics.

## Textual Composition With `+`

The `+` operator composes text without allocation when an expression contains
a textual anchor and cannot be folded completely at compile time.

A textual anchor is:

- a string literal or primitive string value;
- a compatible counted character view;
- an interpolated string;
- a compatible formatter delegate.

Constant string operands are folded:

```camp
auto value = "one" + "two"; // string: "onetwo"
```

Runtime composition produces the same formatter representation and follows the
same target and interpolation-component rules as `$"..."`:

```camp
auto first = "Total: " + total;
auto second = $"Total: {total}";
```

Those expressions are semantically equivalent formatter compositions.

Parentheses and ordinary left associativity continue to determine arithmetic
before a textual anchor is reached:

```camp
auto first = "Total: " + 1 + 2;    // formats "Total: 12"
auto second = 1 + 2 + " total";    // formats "3 total"
auto third = "Total: " + (1 + 2);  // formats "Total: 3"
```

`char + char` and other numeric operations remain arithmetic unless a textual
anchor makes that part of the expression a formatter composition.

The first runtime value requiring formatting is the first interpolation for
`auto` inference. An explicit formatter target applies to the whole textual
composition:

```camp
LibraryFormatter message = "Record " + recordId;
```

Assigning dynamic composition directly to a primitive string is invalid because
that would require hidden allocation:

```camp
string name = getName();
string message = "Hello, " + name; // ERROR: dynamic composition is a formatter
```

Materialization must be explicit:

```camp
string message = ("Hello, " + name).copyString();
```

This proposal does not give `+=` formatter-composition semantics. The compiler
should nevertheless recognize an attempted textual `+=` and explain the
problem directly:

```camp
message += value;
// ERROR: Textual '+=' is not supported. Runtime text composition produces a
// formatter rather than mutating or allocating a string.
```

The diagnostic may suggest `message = message + value` when `message` can hold
the resulting formatter. When the left side is a primitive string, it should
instead explain that allocation must be explicit, for example by composing the
text and calling `copyString()`.

## Overload Resolution

An interpolated string must not choose among several formatter-shaped overload
selectors.

When an interpolation expression is supplied as the selector argument of an
overload family, the compiler first counts candidate selector types that are
eligible formatter targets:

- If exactly one formatter-shaped selector candidate exists, that candidate
  supplies the explicit interpolation target. Other non-formatter overloads do
  not make the call ambiguous.
- If more than one formatter-shaped selector candidate exists, the call is
  ambiguous. The compiler reports all eligible formatter targets before
  examining whether particular interpolation holes happen to support one of
  them.
- If no formatter-shaped selector exists, ordinary overload failure rules
  apply.

For example, a console family containing one formatter overload remains
ergonomic:

```camp
Console.writeLine($"Total: {total}");
```

This family is intentionally ambiguous:

```camp
void write(overload StringFormatter value)
{
}

void write(overload LibraryFormatter value)
{
}

write($"Record: {recordId}");
// ERROR: interpolated string cannot select between formatter overloads
```

Resolve it with a cast:

```camp
write((StringFormatter)$"Record: {recordId}");
```

or by calling the concrete flattened overload name:

```camp
writeStringFormatter($"Record: {recordId}");
```

Once the cast or concrete method supplies the formatter type, interpolation is
bound normally.

This rule does not introduce general candidate ranking or target-only overload
selection. It is a guard against using interpolation contents or first-hole
ordering to choose between distinct formatter APIs.

## Standard Library Changes

The standard library should use the following canonical UTF-8 formatter name:

```camp
public newtype delegate nuint StringFormatter(
	const this,
	char[] buffer = default);
```

Implementation requires:

- renaming `StringFormatter` to `StringFormatter` throughout the standard
  library, tests, and documentation;
- changing standard `format` methods to use their character buffer as an
  `overload` selector;
- keeping the default buffer value on `StringFormatter`, not on the overload
  selector implementation;
- adding eligible `format` support for primitive string and counted-character
  types so they can appear as interpolation components;
- updating primitive numeric, boolean, and date/time formatter ascriptions;
- updating `Console`, `CharWriter`, and related stream APIs to accept
  `StringFormatter`;
- updating `copyString` to allocate exactly the required size returned by the
  formatter, because that size already includes the terminator;
- auditing every formatter that can return zero so composite formatting skips
  it safely;
- preserving the exact required-size and insufficient-buffer contract in all
  standard formatter implementations.

`WStringFormatter` and `AStringFormatter` may be added later by the standard
library, but the compiler requires no change when a library declares a
compatible wide or system-code-page formatter callable newtype.

The standard UTF-8 `Console` should continue to expose only its UTF-8 formatter
overload. Separate `WConsole` and `AConsole` types may provide other character
families without making ordinary console calls formatter-ambiguous.

## Diagnostics

Diagnostics should use the source range of the responsible interpolation hole
whenever possible. Required cases include:

- unterminated interpolated string;
- physical newline before the closing quote;
- unmatched `{` or `}`;
- empty interpolation hole;
- invalid contained Camp expression;
- an `auto` interpolation whose first hole has no eligible `char[]` formatter;
- several eligible first-hole `char[]` formatters;
- invalid explicit formatter target shape;
- formatter target using a const array, unsupported element type, incorrect
  return type, mismatched typespec, `thrown`, `fn`, `once`, async, or iterator
  shape;
- missing visible instance member named `format`;
- visible `format` members but no candidate for the required buffer type;
- a matching buffer method with an incorrect result length type;
- a matching method not ascribed to the required named callable newtype;
- several methods ascribed to the same required formatter target;
- a direct formatter component incompatible with the target;
- an interpolation capture that violates scoped or escaped lifetime rules;
- dynamic interpolation assigned to a primitive string target;
- attempted textual or formatter `+=`, with guidance that distinguishes
  formatter reassignment from explicit string materialization;
- multiple formatter-shaped overload selector candidates;
- constant interpolation used in an inline constant when a hole requires
  runtime formatting;
- compile-time-known required-size overflow.

Diagnostics should distinguish “no method named `format`” from “a method named
`format` exists but does not implement this formatter.” The latter should list
the required buffer and return types and, for a named target, the required
callable ascription.

Parser recovery should keep the outer expression and later statements
available after malformed braces, strings, or contained expressions. Binding
should not emit cascades for holes already marked invalid.

## Compiler Implementation Strategy

### 1. Tokenization And Parsing

`CampTokenizer` currently recognizes an ordinary quoted string as a single
token and stops it at a physical newline. Interpolation requires a mode-aware
path that can alternate between literal text and ordinary Camp tokens.

Implementation should:

- recognize adjacent `$"` as interpolation start rather than separate `$` and
  string tokens;
- tokenize or otherwise preserve literal segments, brace delimiters, and
  contained expression tokens;
- track nested brace depth inside holes;
- honor ordinary strings, characters, escapes, comments, initializer braces,
  and lambda/block braces while scanning a hole;
- retain precise token ranges;
- recover at an outer quote, physical newline, or safe expression boundary
  after malformed input.

`CampParser` should create dedicated interpolated-string syntax and segment
nodes rather than encoding the feature as an ordinary string token or lambda.
`SyntaxNodeTraversal`, source-range utilities, AST serialization, and parser
dump support must visit and preserve those nodes.

### 2. Bindable Model

`BindableNodeBuilder.Expressions.cs` should translate the syntax into a
dedicated bindable interpolation expression with ordered literal and expression
segments. `BindableNode.Expressions.cs`, bindable traversal, rewriting,
serialization, participation analysis, and code-dump support must understand
the node.

Keeping a first-class node until semantic analysis is important for:

- constant-text evaluation;
- first-hole type inference;
- explicit target propagation;
- hole-specific diagnostics;
- ordered evaluation;
- formatter-overload ambiguity checks;
- normalization with textual `+`.

### 3. Formatter Shape And Member Binding

The analyzer should centralize formatter-shape inspection rather than
duplicating it in interpolation, overload, and standard callable logic.

The helper should:

- inspect anonymous delegates and callable newtypes through
  `CallableShapeService`;
- require a context-carrying reusable delegate family;
- identify the one character-array buffer parameter;
- derive its expanded length-component type through existing type-shape and
  params-component services;
- compare target specs and natural-integer domains exactly;
- find direct formatter conversions;
- perform `format` member and extension lookup;
- filter buffer overload selectors;
- validate callable ascription and receiver compatibility;
- report candidate-specific failure reasons.

Existing overload-family validation remains responsible for requiring one
selector, stable family shape, and no selector default.

### 4. Type Analysis And Constant Evaluation

`BindableNodeAnalyzer.MethodBody.cs` and its semantic helpers should add:

- constant-text preanalysis;
- `auto` first-hole inference;
- explicit formatter-target analysis;
- direct formatter-component handling;
- left-to-right analysis without re-evaluating expressions;
- formatter-aware overload selector ambiguity rules;
- dynamic-string-target rejection.

`BindableNodeAnalyzer.InlineConstants.cs` should fold eligible interpolation
and constant textual `+` expressions into `ConstantValue.String`. It should
preserve dependency-cycle detection for referenced inline constants and reject
holes that would require executing user format code.

Binary `+` analysis currently accepts arithmetic operands. It should recognize
and normalize textual chains before ordinary arithmetic diagnostics are issued,
while preserving arithmetic grouping and left associativity.

Compound-assignment analysis should recognize the same textual anchors for
`+=`, but issue the dedicated unsupported-textual-compound-assignment
diagnostic. It must not lower the operation as numeric addition or silently
allocate a primitive string.

### 5. Lifetime And Flow Analysis

`BindableNodeAnalyzer.LifetimeFacts.cs` and flow analysis should derive the
formatter's lifetime from:

- every captured interpolation result;
- every bound `format` receiver;
- every directly embedded formatter;
- literal and counted-array backing storage;
- the generated delegate context.

The result should default to scoped and use existing capture, async-frame, and
escaped-context diagnostics. Flow analysis must treat hole evaluation as
left-to-right and stop later evaluation on a `thrown` exit.

### 6. Lowering

Lowering should normalize `$"..."` and runtime textual `+` into one internal
formatter-composition representation.

For runtime composition, generate:

- context storage for each evaluated component or bound formatter;
- initialization statements in source evaluation order;
- a delegate invocation function matching the selected formatter target;
- required-size calculation in the exact array length type;
- a sufficiently-large-buffer write path;
- final null termination;
- any cleanup required by captured values.

The generated invocation function should query each dynamic component, skip
zero results, subtract only a component's terminator, and share intermediate
terminators without underflow. Size calculations and slice offsets must retain
the buffer array's typespec and target width.

Lowering must preserve virtual and interface dispatch for bound `format`
methods. It should reuse existing instance-method-reference and delegate-context
lowering rather than bypassing callable ascription.

All interpolation nodes should be eliminated before ordinary C expression
emission. `CCodeEmitter` should receive generated declarations, context
initialization, callable values, and ordinary buffer operations.

### 7. Overloads

`BindableNodeAnalyzer.Overloads.cs` should recognize an interpolated string or
unresolved formatter-composition expression used as the selector argument.

Before ordinary selector analysis, it should:

- classify formatter-shaped selector candidates structurally;
- accept one such target;
- reject two or more regardless of hole compatibility;
- preserve concrete flattened-name calls as ordinary already-selected calls;
- treat an explicit cast as an ordinary independently typed selector
  expression.

This special handling must not make other overload-family names callable or
weaken the independent-static-type rule for other selector expressions.

### 8. Metadata, API, And Tooling

Interpolation is an expression implementation detail and adds no new
ABI-visible type category. After lowering:

- named formatter callable newtypes remain ordinary delegate newtypes in
  metadata and API output;
- anonymous inferred formatters use existing anonymous delegate
  representation;
- generated context helpers remain private implementation details;
- source AST and semantic dumps may retain interpolation nodes in the
  appropriate pre-lowering views;
- metadata must not invent a dependency on `StringFormatter`.

`CampSymbolService`, `CampLanguageService`, syntax highlighting, semantic
tokens, hover, completion, go-to-definition, rename, formatting, and source
mapping should treat hole expressions as ordinary source expressions while
classifying literal segments as string text. Definition navigation from a
formatted value should resolve its selected `format` method.

Coverage instrumentation should attribute hole evaluation to the contained
expression range and formatter invocation to the containing interpolation
expression without exposing generated helpers as user functions.

## Test Coverage

### Tokenizer, Parser, And AST Tests

Add tests for:

- empty, literal-only, single-hole, and multiple-hole interpolation;
- ordinary escapes and escaped quotes;
- `{{` and `}}`;
- nested parentheses, brackets, initializer braces, lambdas, strings,
  characters, and comments inside holes;
- conditional and member expressions inside holes;
- physical CR, LF, and CRLF rejection;
- unterminated literals and holes;
- empty holes and unmatched closing braces;
- recovery into later expressions and statements;
- exact syntax ranges and AST serialization.

### Declaration And Callable Tests

Cover:

- `StringFormatter`-shaped callable newtypes;
- anonymous delegate targets;
- default buffer parameters on callable types;
- required buffer selectors without defaults on `format` implementations;
- extension, instance, inherited, virtual, and interface `format` methods;
- direct bound formatter components;
- invalid `fn`, `once`, async, iterator, `thrown`, const-buffer, and
  multiple-parameter shapes;
- array length return types with default, `_near`, `_far`, and other configured
  target specs;
- same-width but different-domain return-type rejection.

### Semantic And Diagnostic Tests

Cover:

- no-hole string inference;
- fully constant interpolation and inline string dependencies;
- `auto` inference from an ascribed first formatter;
- anonymous inference from an un-ascribed first formatter;
- first-hole ambiguity;
- explicit named and anonymous targets;
- later holes with missing, wrong-buffer, wrong-return, wrong-ascription, and
  ambiguous formatters;
- direct compatible and incompatible formatter holes;
- explicit `char`, `achar`, and `wchar` targets;
- rejection of implicit encoding changes;
- dynamic assignment to primitive strings;
- hole-specific diagnostic ranges and suppression of cascades;
- scoped and escaped capture failures;
- async-frame containment across suspension.

### Overload Tests

Cover:

- one formatter selector plus several non-formatter selectors;
- two or more formatter selector candidates;
- ambiguity even when hole contents support only one candidate;
- explicit cast resolution;
- concrete flattened overload-name resolution;
- preservation of existing weak-selector diagnostics for `null`, `default`,
  aggregate initializers, and lambdas.

### Lowering And C-Emission Tests

Golden lowering and emitted-C tests should verify:

- left-to-right one-time evaluation;
- early `thrown` exit preventing later hole evaluation;
- generated context field order and cleanup;
- named and anonymous delegate construction;
- constant-backed formatter lowering;
- direct formatter composition;
- required-size calculations and shared terminators;
- safe zero-result handling without subtracting from zero;
- insufficient-buffer paths;
- virtual and interface dispatch preservation;
- textual `+` normalization and arithmetic grouping;
- exact target-spec propagation to buffer pointers, lengths, offsets, and
  return values;
- absence of interpolation nodes in final emitted C.

### Runtime Tests

Add standard-library runtime tests for:

- empty and literal-only formatters;
- strings, characters, booleans, signed and unsigned integers, and date/time
  values;
- several differently typed holes in one formatter;
- exact-sized buffers;
- undersized buffers and null termination when anything is written;
- zero-returning embedded formatters;
- repeated formatter invocation;
- direct console and stream output;
- `copyString` allocation and contents;
- evaluation counts and ordering;
- dynamic textual `+`;
- dedicated diagnostics for `StringFormatter`, string, string-view, and
  formattable-value uses of textual `+=`;
- nested formatter components;
- scoped formatters retained safely across an async suspension.

### Metadata, API, LSP, And Coverage Tests

Verify:

- formatter callable newtypes serialize normally;
- no compiler-owned formatter symbol appears in metadata;
- generated helpers do not leak into API headers;
- source and lowering dumps remain stable;
- completion and go-to-definition inside holes;
- semantic highlighting distinguishes string segments and expressions;
- rename edits identifiers inside holes without changing literal text;
- diagnostics use mapped source paths and exact hole ranges;
- coverage marks evaluated holes and does not expose generated helpers.

### Test-Running Strategy

During implementation:

1. Run tokenizer, AST, semantic, diagnostics, lowering, and C-emission tests
   affected by each compiler layer.
2. Run standard-library compile and runtime formatter tests on the primary
   development platform after lowering is functional.
3. Run targeted cross-target emission and compile tests for at least one
   non-default natural-integer/typespec configuration, especially the 16-bit
   near-data-pointer target.
4. Run the complete host compiler suite at integration milestones rather than
   after every local edit.
5. Before acceptance, run the full supported-platform CI suite, because
   delegate ABI, expanded array components, target specs, C compilers, and
   standard-library output are all affected.

This balances fast feedback with confidence in the target-specific surfaces
that targeted host-only tests cannot cover.

## Documentation Updates

### Language Guide

Update:

- `04-everyday-types-values-text-arrays-and-optionals.md` with interpolation,
  the everyday distinction between `StringFormatter` and `string`, constant
  text, and explicit materialization;
- `11-enums-newtypes-and-inline-constants.md` with ordinary examples of
  constant interpolation in inline strings;
- `16-the-standard-library-in-practice.md` with ordinary `StringFormatter`,
  `copyString`, console, and stream examples;
- `18-expressions-statements-and-operators-reference.md` with `$"..."`,
  escaping, textual `+`, and the dedicated textual `+=` diagnostic.

The language guide should not explain formatter method lookup, callable
ascription, custom formatter ecosystems, generated delegate contexts,
overload-target selection, required-size arithmetic, or async-frame
containment. Those are semantic and compiler-writer topics. Suggested
user-facing treatment appears below.

### Semantic Supplements

Update:

- binding and lowering pipeline documentation for the new syntax node and
  normalization stage;
- expanded-form documentation for formatter buffer/length type matching;
- lifetime and async-frame documentation for captured formatter contexts;
- callable lowering documentation for inferred and explicitly targeted
  formatter delegates;
- metadata/API documentation to state that interpolation adds no ABI type;
- target-capability documentation for array carrier typespec propagation;
- diagnostics guidance for literal and hole source ranges;
- core expression semantics for evaluation order, constant folding, target
  inference, `+`, and overload rules.

### Compiler And Contributor Documentation

Update:

- compiler dump and introspection documentation for interpolation syntax and
  bindable nodes;
- language-server and editor documentation for semantic tokens and navigation
  within holes;
- standard-library build documentation for the `StringFormatter` to
  `StringFormatter` API rename;
- the compiler development guide with the formatter-shape helper and lowering
  ownership;
- the documentation contributor guide with interpolation example and escaping
  conventions;
- the Camp LLM coding guide so generated Camp uses `$"..."` for mixed text,
  preserves constant strings, and calls `copyString()` only when materialized
  storage is required.

## Proposed Language-Guide Presentation

The everyday language guide should present the feature approximately as
follows:

---

### Interpolated Text

Put an expression inside `{` and `}` in a `$"..."` string:

```camp
Console.writeLine($"Hello, {name}.");
Console.writeLine($"Processed {completed} of {total}.");
```

Use ordinary string escapes such as `\n`. Double braces when you want braces in
the output:

```camp
Console.writeLine($"Set {{name}} to {value}\n");
```

In ordinary standard-library code, interpolated text containing runtime values
has type `StringFormatter`. A `StringFormatter` can be written directly without
first allocating a string:

```camp
Console.write($"Loading {path}...");
```

Create an owned string explicitly when an API or stored value needs one:

```camp
string label = $"Player {playerNumber}".copyString();
```

Constant text stays constant:

```camp
inline string PRODUCT = "Camp";
inline string TITLE = $"{PRODUCT} compiler";

auto joined = "one" + "two"; // "onetwo"
```

You can also use `+` when it makes longer text easier to lay out:

```camp
Console.writeLine(
	"Player " + playerNumber +
	" scored " + score);
```

---

The language guide should stop at interpolation syntax, ordinary
`StringFormatter` use, explicit `copyString()` materialization, constant
interpolation, and textual `+`. All protocol, binding, lifetime, size, and
lowering rules must be documented comprehensively in the semantic supplements
instead.

## Acceptance Criteria

The proposal is ready to accept when:

- lexical, parsing, escape, and recovery rules are unambiguous;
- constant text remains an ordinary string and inline string interpolation is
  evaluable without running user code;
- runtime interpolation infers or accepts formatter targets according to the
  rules above;
- formatter return types exactly match buffer array length-component types and
  target specs;
- every hole is evaluated exactly once from left to right;
- composite size and write behavior preserve the null-terminated formatter
  contract, including zero-returning components;
- runtime interpolation performs no hidden string allocation;
- textual `+` and `$"..."` normalize to the same formatter semantics;
- attempted textual `+=` receives its dedicated diagnostic rather than a
  numeric-operator error or implicit allocation;
- multiple formatter overload targets require an explicit cast or concrete
  overload name;
- scoped, escaped, virtual, interface, and async-frame behavior follows
  existing Camp rules;
- standard-library migration to `StringFormatter` is specified and tested;
- diagnostics and editor tooling preserve precise hole source ranges;
- targeted target-spec tests and the complete supported-platform suite pass;
- language, semantic, compiler, standard-library, and LLM documentation are
  updated.
