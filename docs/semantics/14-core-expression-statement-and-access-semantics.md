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

## Caller-Prepared Array Results

Caller-prepared array results use one `prep` parameter to expose a two-call
size/write protocol through an ordinary function or method surface.

### Contextual Grammar And Declaration Shape

`prep` is a contextual parameter modifier, not a globally reserved word. It is
recognized as a modifier only when tentative parameter parsing can consume a
complete following type and parameter name. Thus `prep char[] buffer` uses the
modifier, while `prep value` may use `prep` as the type name and `value` as the
parameter name. Capitalization is not part of this decision. Outside that
parameter context, `prep` is an ordinary identifier or type name.

There is no `prep expression` or `prep new expression` source form. Prepared
transformation is selected by call argument binding, and allocated prepared
storage uses `(new)` as described below. Legacy prefix-shaped text receives
ordinary parser or binder diagnostics; it has no compatibility grammar or
special migration diagnostic.

A declaration may contain at most one `prep` parameter. That parameter:

- is an ordinary mutable array parameter;
- has a copyable element type;
- has an array length component type exactly equal to the callable return type,
  including target specs;
- cannot also be `in`, `out`, `thrown`, `overload`, `within`, or a compiler
  capability parameter such as `sizeof(T)`, `typenameof(T)`, or
  `vtableof(T: Interface)`.

`once` callable signatures cannot contain a prep parameter because a transformed
use may require repeated invocation. Constructors and other callable kinds
already unable to express this declaration shape remain ineligible. Async and
iterator declarations retain their existing restrictions on prep and scoped
results.

A prep parameter may have an ordinary default value. That default remains part
of the declared signature and API, but omission of the prep slot selects a
transformed call; it does not insert the declared default as an ordinary full
call argument.

Every parameter after prep must be satisfiable without a required ordinary
caller argument. A following slot is permitted when it has an ordinary default
or is supplied by an established compiler rule, including `within`,
`sizeof(T)`, `typenameof(T)`, `vtableof(T: Interface)`, and propagated hidden
slots such as `thrown`. A required ordinary or required `out` parameter cannot
follow prep. Declaration validation uses the same defaultable/compiler-supplied
slot classification as ordinary call binding.

The prep and overload modifiers are mutually exclusive, and an overload
selector cannot have a default. Consequently, an overload selector in a
prep-bearing family must precede prep. This lets selection finish against the
declared family before call-mode determination.

Generic `prep T[]` parameters follow ordinary generic array rules. Operations
that need stride, copying, type names, or interface dispatch require the same
`sizeof(T)`, copyability, `typenameof(T)`, and `vtableof` capabilities as the
corresponding ordinary array operations.

### Behavioral Contract

The compiler verifies the source shape, not the full runtime behavior. For the
same receiver and non-prep arguments, declaring prep promises:

- the scalar return is the minimum array length required for the complete
  logical result;
- a supplied buffer receives the first `min(buffer.length, required)` logical
  elements;
- required size and logical contents are stable across protocol calls;
- no write occurs outside the supplied array;
- failure depending only on the receiver and non-prep arguments occurs during
  sizing;
- terminators, sentinels, and padding are excluded unless they are themselves
  part of the logical result.

The contract permits the compiler or caller to issue the calls required by a
use. It does not promise the implementation an exact source-observable call
count or ordering. In particular, optimizations may omit allocation or a write
call when prepared elements are not observable.

### Full-Signature-First Call Binding

Calls bind against the selected untransformed declaration: the scalar return,
the prep parameter in its declared position, and every other declared slot.
Body analysis performs these steps:

1. Perform ordinary lookup and receiver binding.
2. For an overload family, bind the independent selector at its declared
   position and select the concrete entry. Prepared result type and result
   targets do not add candidate ranking or disambiguation.
3. Bind every written positional, named, `out`, `catch`, `within`, and
   capability argument to a slot in that selected declaration.
4. Record the selected callable, receiver, generic and `constof` substitutions,
   complete argument-to-slot mapping, and which declared slots were supplied.
5. If a written argument supplied the prep slot, select full mode.
6. Otherwise, ensure every other omitted slot is defaultable or
   compiler-supplied and select transformed mode.
7. Record the intrinsic result type, allocation mode, error handling, dispatch,
   lifetime facts, and any result-target conversion for lowering.

Lowering consumes these facts; it must not infer the call mode from argument
count, result use, or a following `.length`.

A written `default` is an argument. For
`render(prep char[] buffer, Style style = default)`, `render(default)` binds
`default` to `buffer` and is a full scalar call. The transformed form that
supplies the later option is `render(style: Style.COMPACT)`. The explicit
one-call sizing form is `render(buffer: default)`. This rule is identical for a
prep parameter with or without a declared default.

### Full And Transformed Modes

A full call explicitly supplies the prep slot, positionally or by name. It is
one ordinary invocation, performs no implicit allocation or sizing pass, and
has the declaration's scalar return type. All ordinary receiver, conversion,
dispatch, `thrown`, `catch`, `out`, `within`, generic, default, and lifetime
rules apply.

A transformed call omits the prep slot. Its intrinsic result type is the
substituted mutable array type of that slot. `auto` preserves that array type.
Only after mode and intrinsic type are known may assignment, argument, return,
cast, conditional-arm, initializer, or other target typing apply an existing
conversion. Existing array-to-string-family conversion and terminator rules are
unchanged.

Invocation-time transformation applies through every prep-bearing source
surface: free, static, instance, and extension functions; generic functions and
constraint members; interface and virtual methods; `fn`, `delegate`, callable
newtypes, and other compatible callable values; and declarations imported from
Camp API headers. A method reference remains declaration-shaped when read:

```camp
auto formatter = value.toString;
```

The reference retains the scalar return and prep parameter. Its invocation is
transformed only if that invocation omits the prep slot. A call through a
non-prep callable or interface view remains ordinary even if the concrete
implementation declares the stronger prep guarantee.

### Transformed Lowering And Evaluation

For a transformed call, lowering conceptually:

1. evaluates and captures the receiver when necessary;
2. evaluates and captures every caller-written non-prep argument once, in
   source order;
3. obtains ordinary defaults, hidden slots, substitutions, generic capability
   values, `within` context, dispatch target, and error handling from analysis;
4. calls the selected surface with a default prep buffer to obtain `required`;
5. checks required-length, terminator, and element-size arithmetic before
   allocation;
6. allocates mutable storage of the required size;
7. calls the same selected surface with that storage;
8. produces the prepared array and applies the recorded target conversion.

Receiver, selector, explicit argument, capability, allocator, and dispatch
expressions are not re-evaluated for the write call. Interface and virtual calls
use the same statically selected prep surface and dynamic dispatch mechanism for
both protocol calls. The second scalar return is ignored as an expression
result; the behavioral contract requires it to agree with sizing.

Thrown handling is not invented or discarded. An explicit or propagated
thrown slot and its `catch` behavior are reused consistently for both calls.
Likewise, lowering does not invent storage for required `out` arguments or an
allocator where ordinary call rules would require one. Runtime arithmetic
overflow or allocation failure occurs before writing through the target's
ordinary failure mechanism.

### Measure-Only `.length`

In `value.toString().length`, the call first binds as a transformed expression
whose intrinsic type is the mutable prep array; `.length` is ordinary array
component access. If the prepared elements are not observable, lowering may
replace size/allocate/write with a full call using a default prep buffer and use
its scalar result as the length. Immediate `.length` is the primary recognized
case, but allocation elision is best effort and is not a separate binding mode
or a language guarantee for every equivalent data-flow shape.

When exactly one ordinary sizing call is required, write the full call
explicitly:

```camp
nuint required = value.toString(buffer: default);
```

### Allocated Prepared Results

`(new)` is a cast-like allocation modifier whose operand must be a direct
transformed prep call:

```camp
auto owned = (new) value.toString();
auto arenaOwned = within(arena) (new) value.toString();
```

It changes only prepared-result storage from scoped `init`-like allocation to
ordinary `new` allocation. Lookup, arguments, dispatch, intrinsic type, and
target conversion are otherwise unchanged. Allocator selection follows the
active `within` policy.

`(new)` has unary/cast-like precedence and consumes its postfix operand. The
operand must still be the direct transformed result. Therefore appended member,
component, index, range, or method operations make forms such as these invalid:

```camp
(new) value.toString().length;
(new) value.toString().toUppercase();
(new) value.toString()[2..5];
```

The direct result may be captured for later cleanup, or cleanup may attach
directly with `finally delete`:

```camp
within(arena) (new) value.toString() finally delete;
```

The syntax is the parenthesized `new` token followed by its operand; ordinary
trivia is permitted between tokens. Parser and bindable nodes retain the
parenthesized modifier's range independently from the call range so malformed
syntax and direct-owner diagnostics can recover at `(new)`.

A transformed call without `(new)` has the lifetime of equivalent scoped
initialized array storage. A `(new)` result has ordinary allocated lifetime and
cleanup obligations. Existing return, escape, capture, conditional, loop,
async-frame, iterator-frame, and deletion rules apply; no new lifetime category
is introduced.

### Prep Methods And Property Syntax

A prep-bearing getter or setter is not a valid property accessor. The same
restriction applies to named properties, indexed properties, nameless indexers,
extension accessors, interface accessors, and virtual accessors. Such a function
does not produce property metadata or property completion.

Property lookup still recognizes a prep-bearing accessor candidate before
rejecting it so the diagnostic can name the accessor and require explicit
method-call syntax rather than reporting a missing member. Calling `getText()`
explicitly transforms normally. The ordinary const-receiver convention for
getter-named methods remains available to the explicit call. Ordinary non-prep
properties are unchanged.

### Callable, API, Metadata, And Tooling Views

Prep remains part of callable compatibility. A source callable with `prep T[]`
may satisfy an ordinary mutable `T[]` target because that target ignores the
stronger guarantee. The reverse direction requires an explicit unsafe
conversion. Interface implementations, virtual overrides, and callable
ascriptions preserve the corresponding prep slot as required by their existing
shape rules.

API headers and metadata retain the declared scalar result, source parameter
order, prep modifier, and any declared prep default. They do not expose a
synthetic transformed overload or generated sizing/write helper. Definitions,
method-reference hover, signature help, and navigation show the declaration;
call-expression type information shows the transformed array or full scalar as
appropriate. Property completion and property metadata exclude prep accessors.

## Interpolated Strings

Interpolated strings are source expressions for eagerly producing UTF-8 text
from literal segments and formatted values. A runtime interpolated string
materializes a concrete result at the expression site. It does not produce a
formatter delegate value.

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

Before runtime formatting, the compiler evaluates whether the interpolated
string is fully constant text. An interpolation is constant text when every
literal segment is constant text and every hole is a compile-time string-like or
character constant. Inline string constants participate:

```camp
inline string PREFIX = "Camp";
inline string TITLE = $"{PREFIX} compiler";
```

User `toString` methods are not executed during constant evaluation. Numeric,
enum, newtype, aggregate, and other non-text constants therefore prevent
constant-text folding unless they are already represented as string-like or
character constants.

With `auto`, constant text infers as the equivalent plain string literal. A
constant interpolated string may initialize any target that the equivalent
string literal may initialize, including `string`, compatible counted character
views, and fixed character storage.

### Result Targets

When an interpolation contains runtime holes, the result target is determined by
ordinary target typing:

- no target, `auto`, or a generic target-type placeholder resolves to `string`;
- `string` targets are valid and produce null-terminated primitive string
  storage;
- `char[]` and `const char[]` targets are valid and produce counted character
  views whose length is the required character count;
- fixed `char[N]` targets are valid for declaration initialization and write
  directly into the fixed storage;
- other targets are invalid.

Formatter delegate targets are not valid result targets for an interpolated
string. The standard formatter type may still appear as an ordinary value inside
an interpolation hole, but the interpolation expression itself resolves to
concrete text.

`new` may be applied directly to an interpolated string expression:

```camp
string message = within(default) new $"total: {total}" finally delete;
char[] chars = within(default) new $"total: {total}";
```

A `new` interpolated string uses the current `within` allocation policy and
requires the same explicit `within` context as other heap allocation. `new` is
not valid for fixed-array targets because fixed-array interpolation writes into
existing fixed storage.

Non-`new` runtime interpolation uses scoped storage equivalent to `init`
character-array storage. The result cannot be returned, stored, or otherwise
escape unless ordinary scoped lifetime rules allow the same escape for the
equivalent initialized storage.

### Formatter Protocol

Runtime holes are formatted through caller-prepared `prep char[]` methods. An
eligible formatter follows this contract:

- The return value is the complete required character count in array elements,
  excluding any null terminator.
- If a buffer is supplied, the formatter writes up to `buffer.length` formatted
  characters into the buffer.
- If the provided buffer is too small, the formatter writes the formatted
  prefix that fits and must not write outside the buffer.
- The formatter does not write a null terminator.
- A return value of zero is permitted and means the formatter contributes no
  content.
- The formatter has no `thrown` channel.
- Size-query and write calls over unchanged captured state must agree on the
  required size and formatted content.

The compiler checks the source shape, not the full behavioral contract. An
eligible prepared formatter asserts that its implementation obeys the contract.

### Formattable Values

A runtime hole is formattable when either:

- for a bare non-text hole, ordinary instance member lookup finds exactly one
  eligible prepared method named `toString`; or
- the hole expression itself is a call to any eligible caller-prepared method,
  such as `{value.toString(options)}` or `{value.toHexString()}`; or
- the hole expression is an ordinary non-prep expression that already produces
  a supported direct text or character value.

Receiver-style extension functions, inherited methods, virtual methods, and
interface members participate through ordinary Camp lookup and dispatch rules.

An eligible prepared formatter:

1. has exactly one `prep` parameter after receiver binding;
2. accepts mutable `char[]` as the `prep` buffer type;
3. returns the exact length-component type of that array;
4. is selected either by the `toString` bare-hole convention with no required
   explicit non-prep formatting arguments, or from a hole call expression that
   supplies those ordinary formatting arguments itself;
5. has receiver and lifetime requirements compatible with evaluating the hole
   expression once and invoking the formatter for size and write passes;
6. has no unsupported thrown path.

The name `toString` is a lookup convention, not a reserved method or a general
signature restriction. Methods of that name bind normally outside bare-hole
discovery. An ordinary non-prep `toString()` that returns direct text can be
written explicitly in a hole, but is not discovered implicitly for a bare
non-text value.

When interpolation uses a caller-prepared formatter, the hole expression and any
explicit non-`prep` arguments are evaluated once. The interpolation lowering
then uses the same value for the size pass and the write pass into the final
interpolation buffer. The interpolation expression itself still eagerly
produces concrete UTF-8 text; it does not become a formatter value.

A bare hole and an explicit parameterless `toString()` hole are equivalent when
they select the same prepared formatter. Analysis must preserve that formatter
identity through interpolation lowering instead of first lowering the explicit
call as a general transformed expression. Both forms write directly into the
final interpolation destination without an intermediate prepared allocation or
copy. Explicit formatting arguments are captured once in source order and
reused for sizing and writing.

Example:

```camp
public struct Status
{
	bool enabled;
}

public nuint toString(
	in Status this,
	prep char[] buffer = default)
{
	const char[] text = this.enabled ? "enabled" : "disabled";
	nuint required = text.length;

	nuint count = min(required, buffer.length);
	for (nuint index = 0; index < count; index++)
		buffer[index] = text[index];

	return required;
}
```

An interpolated literal with no holes behaves as an ordinary string literal
under `auto`:

```camp
auto text = $"ready"; // string
```

### Runtime Evaluation And Lowering

Runtime interpolation evaluates every dynamic hole expression exactly once, from
left to right, when the interpolation expression is evaluated. The evaluated
hole result is captured as the value to be formatted. Formatting may call the
selected formatter twice, once for the required size and once for writing, but
the original hole expression is not re-evaluated.

For:

```camp
auto message = $"{first()} / {second()}";
```

`first()` runs before `second()`. If `first()` exits through a `thrown` path,
`second()` is not evaluated and no interpolation result is produced. Formatter
invocation itself has no `thrown` channel; an implementation that must fail
should fail before it participates in the formatter protocol.

Interpolation nodes must be gone before final C expression emission. Runtime
lowering conceptually proceeds as follows:

1. Evaluate and capture each runtime hole in source order.
2. Compute the required character count by adding literal segment lengths,
   constant-hole lengths, and formatter size-query results.
3. Create destination storage:
   - `string`: allocate `required + 1` characters and reserve one null
     terminator;
   - `char[]` and `const char[]`: allocate `required` characters and keep the
     counted length;
   - fixed `char[N]`: use the existing fixed storage and treat `N` as the
     writable length.
4. Write literal segments with compact block-copy operations and invoke dynamic
   formatters with destination slices.
5. For primitive string targets only, write a trailing `'\0'`.

The size pass is:

```text
required = 0
required += literal code units
required += component required
```

For counted array targets, the counted length is the complete required length
even when the physical write was clamped to fixed storage in a different target
form. For fixed `char[N]` targets, the fixed storage is zero-filled before
writing. The write pass copies the prefix that fits. If the required length is
less than `N`, the next element remains zero and the fixed storage is
zero-terminated. If the required length is at least `N`, the visible content is
truncated to the first `N` characters and no extra terminator is written outside
the fixed storage.

The arithmetic uses `nuint`, matching the standard `char[]` length component.
Compile-time-known unrepresentable totals are errors. Runtime overflow must
fail before allocation or buffer writing through the target's ordinary
bounds-failure mechanism; interpolation does not add a `thrown` result.

Async functions and generator methods receive no special interpolation rules.
A non-`new` interpolation that requires scoped result storage follows the same
lifetime and frame-placement rules as equivalent non-const `init` character
array storage in that source position.

### Overload Selection

An interpolated string participates in overload selection as the concrete result
type selected by target typing. With no stronger target, it is a `string`. If a
call has several valid overload candidates for the same interpolation argument,
ordinary Camp overload ambiguity rules apply and the caller must disambiguate.

Callable overloads are not selected by passing an interpolated string. APIs that
accept formatted text should accept a concrete text type and let the caller use
interpolation or a prepared method call at the call site.

### Textual `+`

Binary `+` does not compose text. If either side of `+` is a textual anchor, the
compiler reports a diagnostic and the program should use an interpolated string
instead. Textual anchors include primitive string values, string literals,
compatible counted character views, and interpolated strings. Ordinary numeric
`+` is unchanged when neither operand is textual.

Textual `+=` is also invalid. It does not append text, mutate a string, or
materialize a new string.

### Diagnostics, Metadata, And API Headers

Diagnostics should prefer the responsible hole, interpolation expression,
textual `+` expression, or selected argument range rather than a lowered helper.
Required diagnostic classes include:

- malformed interpolation syntax;
- unsupported runtime interpolation target;
- no formatter candidate for a runtime hole;
- multiple formatter candidates for a runtime hole;
- component `toString` or explicit prepared formatter methods with the wrong
  buffer type, return type, callable kind, thrown slot, receiver, or lifetime;
- scoped interpolation results that escape their valid lifetime;
- `new` interpolation without an explicit `within` context;
- `new` interpolation targeting fixed storage;
- attempted textual `+`;
- attempted textual `+=`.

Interpolation syntax and textual `+` diagnostics are source-expression behavior,
not API surface declarations. Metadata and API headers record ordinary resolved
types, default values, selected functions, and formatter method dependencies.

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

Prep-bearing accessor-shaped functions are excluded from these property facts
and remain ordinary function declarations. Prep declarations retain their
scalar result, source parameter order, modifier, and default in metadata and API
headers; transformed call expressions do not create metadata declarations.

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
- invalid prep declaration shape or following required parameter;
- invalid explicit prep argument or unsatisfied transformed-call slot;
- prep-bearing property candidate requiring explicit method syntax;
- invalid `(new)` prepared operand, ownership chain, allocation context, or
  lifetime;
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
- contextual `prep`, declaration restrictions, and full-signature-first
  full/transformed call binding across direct, callable, generic, interface,
  virtual, and imported API surfaces;
- prepared scoped/allocated lowering, immediate `.length`, explicit buffers,
  target conversions, lifetimes, capabilities, dispatch, and error paths;
- prep property rejection and property metadata exclusion;
- bare and explicit `toString`, alternate prepared formatters, direct text,
  UTF-8 result targets, and direct-to-destination interpolation lowering;
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
  binding, prep call modes, `(new)`, discard recognition, delete/transfer
  analysis, and diagnostics;
- `BindableNodeAnalyzer.Declarations.cs` for prep declaration validation;
- `BindableNodeAnalyzer.Lowering.PreparedBuffers.cs` for prepared sizing,
  allocation, writing, conversions, and `.length` elision;
- `BindableNodeAnalyzer.Lowering.InterpolatedStrings.cs` for prepared formatter
  preservation and direct-to-destination interpolation;
- `BindableNodeAnalyzer.Lowering.Operators.cs` for discard target lowering;
- `BindableNodeAnalyzer.Lowering.Exceptions.cs` for cleanup transfer rewriting;
- `MetadataJsonSerializer.cs` for property metadata fields.
