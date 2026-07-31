# Caller-Prepared Return Buffers

## Status

Pending.

## Proposal Date

2026-07-30

## Last Updated Date

2026-07-31

## Depends On

Eager String Interpolation Lowering.

## Summary

This proposal adds a `prep` parameter modifier and a `prep` call prefix for
functions that return a caller-prepared array result.

A `prep` parameter marks one mutable array parameter as the caller-provided
result buffer. The function returns the array length required to hold the
complete result. The callee writes as many result elements as fit in the
provided buffer:

```camp
nuint format(int this, IntegerFormat options = default, prep char[] buffer = default)
{
	// return required char count; write a prefix into buffer
}

nuint serialize(in PacketHeader this, prep byte[] buffer)
{
	// return required byte count; write a prefix into buffer
}
```

The `prep` call prefix makes these APIs ergonomic by performing the standard
two-call sequence for the caller:

```camp
char[] text = prep value.format(IntegerFormat.POSITIVE_SIGN);
byte[] packet = prep header.serialize();
char[] label = prep ctl.Text;
```

This proposal builds on the current eager interpolation model. Runtime
interpolation already materializes target-typed text at the expression site;
this proposal generalizes the same size/write preparation pattern for ordinary
functions and methods.

`CharFormatter` is not removed by this proposal. The standard library should be
reviewed after `prep` formatting APIs exist.

Unless this proposal explicitly says otherwise, existing Camp semantics still
apply. In particular, this proposal does not redefine `within` allocation
policy, `new`, `init`, `thrown`, `catch`, scoped lifetimes, escaped lifetimes,
or ordinary call argument binding.

## Motivation

Many native APIs compute into caller-provided buffers:

- formatting values into text;
- serializing packets or headers into bytes;
- extracting points, vertices, records, or handles into arrays;
- converting between text encodings;
- querying platform APIs that first report a required size.

Camp already uses the caller-provided-buffer pattern in `CharFormatter`, but the
current abstraction is character-specific and callable-shaped. It does not
naturally support non-text buffers or additional formatting/serialization
options as ordinary method parameters.

The desired source form is direct:

```camp
auto text = prep value.format();
auto bytes = prep packet.serialize();
```

`prep` gives that pattern a source-level form while keeping ABI and runtime
behavior explicit: the callee writes to a caller-provided array, and the caller
owns the allocation.

## Goals

- Add a source-level way to declare caller-prepared array result buffers.
- Support any mutable array whose element type is copyable, not only `char[]`.
- Preserve ordinary Camp call semantics for direct calls to `prep` methods.
- Provide an ergonomic `prep` call prefix that allocates the result buffer and
  performs the sizing/write calls.
- Support `prep new` for heap-allocated prepared results.
- Support `prep` property getter syntax when the getter is already valid as a
  property accessor.
- Allow standard-library formatting APIs to migrate toward explicit formatting
  options without inventing a family of formatter delegate types.
- Preserve existing `within`, `new`, `init`, `thrown`, `catch`, and lifetime
  rules.
- Preserve ordinary overload behavior and require existing disambiguation when
  calls are ambiguous.
- Preserve `prep` in metadata and API headers.

## Non-Goals

- Do not add general multi-return values.
- Do not add a hidden runtime string builder.
- Do not redefine current eager interpolation result semantics.
- Do not make `prep` imply null termination.
- Do not make ordinary `prep` method calls implicitly allocate.
- Do not change existing `new` allocation-context rules.
- Do not change existing thrown/catch rules.
- Do not change scoped or escaped lifetime rules.
- Do not make `prep` prefix expressions initialize fixed-size storage directly.
  Use an ordinary call with a slice of fixed storage when the caller wants to
  provide fixed storage explicitly.
- Do not decide the final `CharFormatter` standard-library surface in this
  proposal.

## `prep` Parameters

### Syntax

`prep` is a parameter modifier:

```camp
nuint format(int this, IntegerFormat options = default, prep char[] buffer = default)
{
}

nuint serialize(in PacketHeader this, prep byte[] buffer)
{
}

nuint extractPoints(const Geometry* this, bool normalize, prep Point[] buffer)
{
}

nuint toWCharArray(const char[] this, prep wchar[] buffer)
{
}
```

`prep` becomes a language keyword in parameter declarations and expression
prefixes.

### Shape Rules

A function or method with a `prep` parameter must obey these source-shape rules:

1. It may declare at most one `prep` parameter.
2. The `prep` parameter must be a mutable array type.
3. The `prep` array element type must be `copyable`.
4. The function return type must exactly match the length component type of the
   `prep` array, including target specs.
5. The `prep` parameter must not also be `in`, `out`, `thrown`, `overload`, or
   `within`.
6. The `prep` parameter may declare a default value.
7. Other parameters may use ordinary Camp modifiers, including `thrown`, subject
   to existing rules.

`prep` is not valid on `once` callable signatures. The `prep` prefix requires a
sizing call followed by a write call, so the callable target must be invocable
at least twice for the same captured state.

`prep` is not a capability parameter. It cannot be combined with special
compiler-provided capability parameter forms such as `sizeof(T)`,
`typenameof(T)`, or `vtableof(T: Interface)`, because those forms are not
ordinary mutable array value parameters.

The compiler enforces the shape rules. The behavioral contract below is asserted
by using `prep`; the compiler cannot generally prove it.

A `prep` parameter default value is used when the method is called as an
ordinary method without a `prep` prefix. It is not used when the method is called
with the `prep` prefix: the prefix removes the `prep` parameter from the
effective source call shape and supplies the measure/write buffers itself. A
default may still be useful or required by ordinary parameter ordering rules,
for example when the `prep` parameter follows another parameter with a default
value.

For generic element types, these rules compose with existing generic array
rules. A `prep T[]` parameter is valid only when existing `T[]` storage, copy,
and element-stride requirements are satisfied by the generic declaration,
constraints, and explicit capabilities such as `sizeof(T)` where those
capabilities are already required.

### Behavioral Contract

For the same receiver and same non-`prep` arguments:

1. The return value is the minimum array length needed to hold the complete
   result.
2. The callee writes the first `min(buffer.length, required)` result elements
   into the `prep` buffer.
3. The return value and logical result contents are stable across repeated calls.
4. The callee must not write outside the provided array.
5. If the function can throw for the receiver and non-`prep` arguments, it must
   do so during the sizing call.
6. The return value excludes terminators, sentinels, or padding unless those are
   part of the logical result.

For text formatting, this means a formatter returns the number of formatted
characters, not a null-terminated string length. Null termination is a consumer
choice, not a `prep` method contract.

The two-call protocol is a contract. If the sizing call succeeds and a later
write call throws or reports a different logical result, the callee violated the
contract. The compiler does not add special recovery semantics for broken
`prep` implementations beyond ordinary cleanup that is already in source scope.

### Ordinary Calls

Methods with `prep` parameters remain ordinary callable methods. The `prep`
parameter appears in the ordinary parameter list and is supplied in its declared
position:

```camp
int statusNum = 42;

nuint needed = statusNum.format(IntegerFormat.POSITIVE_SIGN);

char[] statusText = init char[needed];
statusNum.format(IntegerFormat.POSITIVE_SIGN, statusText);

fixed char[120] fixedText = default;
statusNum.format(IntegerFormat.POSITIVE_SIGN, fixedText[..]);

char[] escapedText = new char[needed];
statusNum.format(IntegerFormat.POSITIVE_SIGN, escapedText);
```

The default value of a mutable array is the natural measure-only `prep`
argument. The proposal does not introduce a special `null` sizing convention.

Ordinary calls use existing argument binding rules, including named arguments,
default arguments, `catch`, `out`, overload selectors, and receiver binding.
The only new ordinary-call behavior is that `prep` is a recognized parameter
modifier and participates in declaration validation.

`prep` is part of a callable signature contract. When a callable type, interface
method, virtual method, or override declares `prep`, an implementation that
conforms to that surface must preserve `prep` on the corresponding parameter. A
concrete implementation may strengthen a non-`prep` surface by marking the
corresponding mutable array parameter `prep`; callers through the non-`prep`
surface simply do not receive the caller-prepared-result guarantee.

Function type compatibility follows the same contract direction:

```camp
fn nuint(prep char[]) prepared = somePreparedFormatter;
fn nuint(char[]) ordinary = prepared;       // allowed
fn nuint(prep char[]) unsafePrepared =
	(unsafe fn nuint(prep char[])) ordinary; // requires unsafe conversion
```

A function that promises `prep` can be used where an ordinary mutable-array
function is expected. An ordinary mutable-array function cannot safely be used
where callers rely on the `prep` contract without an explicit unsafe conversion.

## `prep` Call Prefix

### Syntax

The `prep` call prefix is an expression prefix applied to a compatible call
expression or property getter expression:

```camp
char[] statusText = prep statusNum.format(IntegerFormat.POSITIVE_SIGN);
auto same = prep statusNum.format(IntegerFormat.POSITIVE_SIGN);
char[] label = prep ctl.Text;
```

To allocate the prepared result with `new`, write `prep new` before the call or
property getter:

```camp
char[] escapedText = prep new statusNum.format();
char[] arenaText = within (arena) prep new statusNum.format();
char[] escapedLabel = prep new ctl.Text;
```

`new` and `within` follow existing Camp allocation rules. This proposal does not
change when an explicit `within` context is required.

### Type Transformation

When `prep` prefixes a compatible call:

1. The call target must resolve to a function or method with exactly one `prep`
   parameter.
2. The `prep` parameter is omitted from the source argument list.
3. The expression result type is the `prep` parameter's mutable array type.
   Ordinary assignment or argument conversion may then convert that array to a
   compatible const view.
4. `prep new` uses heap allocation for that array result.
5. `prep` without `new` uses `init` array allocation for that array result.

When `prep` prefixes a compatible property getter, the getter is treated as the
call target. The getter must have exactly one `prep` parameter, and the source
property expression omits that parameter.

This does not add a separate property-access rule. After the `prep` parameter is
omitted from the effective getter call, the getter must be valid for property
accessor syntax under ordinary Camp property rules. The `prep` prefix changes
only the effective call shape by supplying the `prep` argument through the
two-call protocol.

Example:

```camp
char[] text = prep statusNum.format(IntegerFormat.POSITIVE_SIGN);
```

lowers conceptually to:

```camp
auto receiver = statusNum;
auto options = IntegerFormat.POSITIVE_SIGN;
nuint required = receiver.format(options, default);
char[] text = init char[required];
receiver.format(options, text);
```

The actual compiler lowering does not need to introduce source-visible names,
but it must preserve the same observable behavior.

Because `prep` lowering needs temporary values and result storage, a `prep`
prefix is valid only in expression positions where the compiler can introduce
the required temporaries. If the compiler cannot introduce temporaries in a
particular expression position, it must reject `prep` there. `prep new` may
still be valid in some of those contexts when the lowered form does not require
scoped `init` result storage, but receiver and argument evaluation still must
run only once.

### Evaluation Rules

The receiver and all explicit non-`prep` arguments are evaluated once.

This is required because a `prep` prefix performs two calls: one sizing call and
one write call. Side effects in receiver or argument expressions must not run
twice merely because `prep` is used.

The lowered implementation must:

1. evaluate and store the receiver when needed;
2. evaluate and store explicit non-`prep` arguments when needed;
3. call the method once with a default/empty `prep` buffer to get `required`;
4. allocate an array of length `required`;
5. call the method again with the allocated array;
6. produce the allocated array as the expression result.

The return value from the second call is not the result expression. Under the
`prep` contract it must match the first call. If it does not, the callee has
violated the contract.

### Inapplicable Calls

It is an error to use the `prep` prefix with a call target that has no `prep`
parameter:

```camp
char[] some = prep statusNum.somethingElse(); // error
```

It is also an error when omitting the `prep` parameter leaves the call
ambiguous, or when required non-`prep` parameters are not provided by explicit
arguments or defaults.

The `prep` prefix does not add overload disambiguation. If ordinary Camp
overload resolution cannot select one compatible target after the `prep`
parameter is omitted, the caller must use an explicit full overload name, target
type, cast, or other existing disambiguation mechanism.

### Property Getter Syntax

`prep` also applies to property getter syntax when the getter has a compatible
`prep` parameter.

Given:

```camp
class Control
{
	nuint getText(prep char[] buffer)
	{
	}

	void setText(const char[] buffer)
	{
	}
}
```

the getter can be called through property syntax:

```camp
auto text = prep ctl.Text;
auto escapedText = prep new ctl.Text;
```

This lowers as if the getter method itself had been called:

```camp
nuint required = ctl.getText(default);
char[] text = init char[required];
ctl.getText(text);
```

When spelling the two-call protocol by hand, use the ordinary method-call form:

```camp
char[] buffer = init char[ctl.getText(default)];
ctl.getText(buffer);
```

Property assignment does not use `prep`. Existing assignment, setter, and
lifetime rules are sufficient.

## Existing Semantics Remain In Force

### `within`, `new`, And Allocation Contexts

`prep new` is ordinary `new` allocation applied to the prepared array result. It
follows the existing rules for allocation contexts.

This proposal does not require explicit `within(default)` in contexts where Camp
already permits `new` without it, and it does not remove explicit `within`
requirements from contexts where they already apply.

### Lifetimes

`prep` without `new` allocates with `init`, so the resulting array has ordinary
`init` lifetime.

`prep new` allocates with `new`, so the resulting array has ordinary `new`
storage and follows existing escaped ownership and cleanup rules.

No new lifetime category is introduced.

This is intentionally literal. A non-`new` `prep` expression has the same
lifetime, storage, and escape behavior as the equivalent source program that
uses `init` array storage and passes that array to the `prep` method. It may be
used wherever that scoped `init` array result may be used, and it is rejected
where that scoped result could not be returned, stored, yielded, captured, or
otherwise retained.

### Thrown Parameters

Thrown slots follow existing Camp rules.

A `prep` method may have a `thrown` parameter if the rest of the declaration is
valid. A `prep` prefix may call such a method only when the source call handles
the thrown slot in the ordinary way:

```camp
char[] text = prep value.format(options, catch error);
char[] text = prep value.format(options, catch _);
```

The compiler implicitly supplies only the `prep` argument. It does not
implicitly catch or ignore thrown values.

Because the lowered `prep` expression calls the method twice, the same explicit
thrown handling applies to both the sizing call and the write call.

A conforming `prep` method that can fail for the supplied receiver and
non-`prep` arguments should fail during the sizing call. Throwing only during
the write call after allocation means the method did not follow the protocol.
The compiler does not guarantee leak-free behavior for such a broken
implementation beyond ordinary source-level cleanup.

## Interaction With String Interpolation

Current runtime interpolation eagerly produces target-typed text. This proposal
does not change that source behavior: an interpolated string expression still
resolves to `string`, `char[]`, `const char[]`, or fixed `char` storage
according to existing target-typing rules.

After `prep` formatting APIs exist, interpolation lowering may treat a
compatible `prep char[]` method call inside a hole as a formattable value. The
hole expression and its explicit non-`prep` arguments are evaluated once, and
the interpolation write path supplies the sizing and destination buffers. This
lets formatting options remain ordinary method arguments:

```camp
Console.writeLine($"count: {count.format(IntegerFormat.POSITIVE_SIGN)}");
```

The observable interpolation result remains the current eager result. The
interpolation expression itself does not become a formatter delegate.

String interpolation still formats UTF-8 `char` text. There is no built-in
`wstring` or `astring` interpolation in this proposal. Library conversion
methods can expose `prep` APIs:

```camp
SendMessage(
	hWnd,
	WM_SETTEXT,
	null,
	prep ($"Hello {user}, your status is {status}").toWCharArray());
```

Here the interpolation first produces UTF-8 text, and `toWCharArray` is an
ordinary `prep` conversion method.

## Standard Library Impact

### Formatting

Standard formatting methods may move to `prep` methods:

```camp
public nuint format(int this, IntegerFormat options = default, prep char[] buffer = default);
public nuint format(bool this, prep char[] buffer = default);
public nuint format(const char[] this, prep char[] buffer = default);
```

This allows formatting options without creating separate formatter delegate
types:

```camp
Console.writeLine($"count: {count.format(IntegerFormat.POSITIVE_SIGN)}");
```

### Encoding Conversion

Encoding conversions fit the same model:

```camp
public nuint toWCharArray(const char[] this, prep wchar[] buffer);
public nuint toCharArray(const wchar[] this, prep char[] buffer);
```

### `CharFormatter` Review

`CharFormatter` is not removed by this proposal. After `prep` formatting APIs
are implemented, the standard library should be reviewed to decide whether
`CharFormatter` should remain unchanged, change, be deprecated, or be removed in
a later proposal.

`CharFormatter` does not currently provide chunked streaming semantics: standard
console and stream formatter overloads still size the formatter, allocate a full
intermediate buffer, write into it, and then emit that buffer. Moving some APIs
to `prep` therefore does not remove an existing block-streaming capability. It
changes which side of the call owns the full-buffer preparation and generalizes
the pattern to non-character buffers.

## Metadata And API Headers

Metadata must preserve `prep` as source-level parameter information. A `prep`
parameter is represented as a parameter object with `modifier: "prep"`:

```json
{
  "kind": "function",
  "name": "format",
  "returnType": "nuint",
  "parameters": [
    { "name": "this", "type": "int" },
    {
      "name": "options",
      "type": "IntegerFormat",
      "defaultValue": "default"
    },
    {
      "name": "buffer",
      "modifier": "prep",
      "type": "char[]",
      "defaultValue": "default"
    }
  ]
}
```

This is source API, not lowered ABI. Metadata does not add separate synthetic
sizing/write declarations for a `prep` prefix use. The callee remains one
ordinary function whose source contract says the buffer is caller-prepared.

API headers must persist the `prep` modifier and any ordinary default value on
the parameter:

```camp
public nuint format(int this, IntegerFormat options = default, prep char[] buffer = default);
```

Downstream compilation from an API header must therefore preserve the same
ordinary-call behavior, `prep` prefix eligibility, callable compatibility, and
property getter eligibility as compilation from the original source.

## Compiler Impact

Implementation areas:

- tokenizer/parser:
  - reserve `prep`;
  - parse `prep` as a parameter modifier;
  - parse `prep` and `prep new` expression prefixes;
- syntax and bindable model:
  - add `prep` to parameter syntax/model;
  - add expression nodes for prepared calls or represent them as a call rewrite;
- declaration validation:
  - enforce single `prep` parameter;
  - enforce mutable array parameter;
  - enforce copyable element type;
  - enforce exact length-type return;
  - reject `prep` on `once` callable signatures;
  - reject incompatible parameter modifier combinations;
- call binding:
  - ordinary calls bind `prep` parameters normally;
  - `prep` prefix calls omit the `prep` parameter;
  - `prep` property getter syntax binds to compatible getter methods;
  - overload resolution remains ordinary and ambiguous candidates require
    existing explicit disambiguation;
  - argument evaluation must happen once;
  - thrown slots remain explicit and ordinary;
  - callable/interface/override compatibility preserves `prep` where the
    required surface declares it;
- lowering:
  - generate sizing call;
  - allocate `init` or `new` array;
  - generate write call;
  - lower `prep` property getter syntax through the underlying getter method;
  - preserve receiver and argument evaluation order;
- metadata/API emission:
  - emit `modifier: "prep"` for `prep` parameters in metadata;
  - preserve `prep` in API headers and imports;
  - keep metadata source-level and do not expose generated sizing/write
    temporaries.

No special C ABI is required beyond ordinary array parameters and scalar return
values.

## Diagnostics

Required diagnostics include:

- more than one `prep` parameter;
- `prep` parameter is not an array;
- `prep` parameter is const;
- `prep` element type is not copyable;
- return type does not match the `prep` array length type;
- `prep` appears on a `once` callable signature;
- `prep` parameter is combined with an incompatible modifier;
- `prep` prefix is used on a call without a `prep` parameter;
- `prep` property syntax is used on a property whose getter has no compatible
  `prep` parameter;
- `prep` prefix call omits required non-`prep` arguments;
- `prep` prefix call remains ambiguous after omitting the `prep` parameter;
- interpolation hole calls a `prep` formatter that is ambiguous, missing the
  `prep char[]` buffer contract, or has an unsupported thrown slot;
- implementation, override, ascription, or callable conversion fails to preserve
  a required `prep` contract;
- non-`new` `prep` uses scoped `init` storage where equivalent explicit `init`
  array storage is not legal.

Diagnostics should point at the `prep` keyword for declaration-shape errors, at
the prefixed call for `prep` use-site errors, and at the responsible hole
expression for interpolation formatter errors.

## Testing Plan

Use the minimum number of additional tests that covers the cross-product of
declaration shape, ordinary calls, prefix calls, properties, callable/interface
contracts, lifetime behavior, and emitted ABI shape.

Parser and AST coverage:

- parse `prep` parameters;
- parse ordinary calls to prep methods;
- parse `prep call`;
- parse `prep new call`;
- parse `prep property`;
- parse `prep new property`;
- parse `within (arena) prep new call`;
- reject malformed `prep` prefixes.

Semantic coverage:

- validate legal prep declarations for `char[]`, `byte[]`, and custom copyable
  element arrays;
- reject non-array prep parameter;
- reject const array prep parameter;
- reject multiple prep parameters;
- reject return length mismatch;
- reject `prep` parameters on `once` callable signatures and callable newtypes;
- reject incompatible modifier combinations;
- allow `prep` parameter default values;
- bind ordinary calls with explicit prep argument;
- bind ordinary calls using a `prep` parameter default value;
- bind `prep` prefix calls with omitted prep argument;
- reject ambiguous `prep` prefix calls using ordinary overload rules;
- bind `prep` property getter syntax to compatible getter methods;
- reject `prep` property syntax when the getter has no compatible `prep`
  parameter;
- preserve `prep` for overrides, interface implementations, and callable
  ascriptions when the required surface declares it;
- allow a concrete implementation to strengthen a non-`prep` interface or
  callable surface with `prep`;
- allow implicit conversion from `fn nuint(prep char[])` to `fn nuint(char[])`;
- require unsafe conversion from `fn nuint(char[])` to `fn nuint(prep char[])`;
- preserve receiver and argument evaluation once;
- allow ordinary thrown handling on prep prefix calls;
- reject prep prefix calls with unhandled thrown slots;
- bind interpolation holes that call compatible `prep char[]` formatters with
  explicit formatting options;
- reject interpolation holes whose `prep` formatter cannot be selected by
  ordinary overload rules.

Lowering and runtime coverage:

- lower `prep value.format(options)` to sizing call, allocation, write call;
- lower `prep new value.format(options)` to heap allocation;
- lower `prep ctl.Text` through the compatible property getter;
- lower `prep new ctl.Text` through the compatible property getter and heap
  allocation;
- ensure receiver and non-prep arguments are captured once;
- lower byte-array and custom-element-array prep methods;
- stack-allocated prep formatting;
- heap-allocated prep formatting;
- stack-allocated prep property getter;
- heap-allocated prep property getter;
- fixed buffer ordinary call;
- byte serialization into `byte[]`;
- custom copyable element extraction into `Point[]`;
- short-buffer ordinary calls write prefixes;
- zero-length/default prep calls return required length;
- preserve ordinary ABI shape for prep methods;
- emit metadata/API headers with `prep`;
- preserve eager interpolation result typing while using `prep` formatter holes.

Documentation coverage:

- language guide parameter modifier and caller-provided-buffer sections;
- semantics sections for declaration shape, prefix typing, lowering, lifetime
  behavior, callable/interface compatibility, and diagnostics;
- focused LLM guide examples.

## Compatibility

The language feature is additive. Source that uses ordinary functions without
`prep` remains valid.

There may be preview-library breaking changes later if standard-library APIs
migrate from `CharFormatter`-shaped formatting to `prep`-shaped formatting.
This proposal does not make that migration by itself. Source that uses
`CharFormatter` directly remains valid unless a later standard-library review
changes that surface.
