# Proposal A: Eager String Interpolation Lowering

## Status

Pending.

## Proposal Date

2026-07-30

## Last Updated Date

2026-07-30

## Summary

This proposal changes runtime string interpolation so it eagerly produces text
instead of lowering to a lambda-like `CharFormatter` delegate.

After this change, an interpolation expression materializes its result at the
point where the expression is evaluated. The result is target-typed:

```camp
string a = $"abc";
string b = $"{a}123";
char[] c = $"def";
const char[] d = $"456{c}";
auto g = $"456{c}";
string h = new $"The answer is {number}";
```

`CharFormatter` remains part of the standard library. It is still used to format
interpolation holes, and existing formatter values can still appear inside
holes. The change is that the interpolation expression itself no longer
produces a `CharFormatter` value unless an ordinary source expression inside a
hole produces one.

This proposal does not introduce `prep`, does not change the standard formatting
API surface, and does not remove `CharFormatter`.

Unless this proposal explicitly says otherwise, existing Camp semantics still
apply. In particular, this proposal does not redefine `within` allocation
policy, `new`, `init`, `thrown`, `catch`, scoped lifetimes, escaped lifetimes,
or ordinary call argument binding.

## Motivation

Runtime interpolation currently uses `CharFormatter` delegate composition. That
keeps formatting allocation-free until a consumer asks for text, but it makes a
simple interpolation lower into generated formatter functions, contexts, and
delegate calls. For ordinary source such as:

```camp
string name = "User";
Console.writeLine($"Hello, {name}.");
```

the generated C should be much closer to "evaluate the hole, allocate or
initialize the result buffer, write the pieces, and pass the resulting text" than
to a composed callable pipeline.

Eager lowering also makes interpolation feel like an expression that produces
text, which is how readers naturally understand it:

```camp
string message = $"Processed {count} items";
char[] scratchText = $"count={count}";
```

## Goals

- Change runtime interpolation to eagerly materialize text.
- Support target-typed interpolation to `string`, `char[]`, `const char[]`, fixed
  `char[N]`, and `new` heap allocation.
- Keep `auto` interpolation inferred as `string`.
- Preserve existing `CharFormatter` formatting behavior for interpolation holes.
- Preserve ordinary thrown propagation from hole expressions and formatter calls.
- Preserve existing scoped and escaped lifetime rules.
- Reduce generated C size compared with formatter-lambda lowering.
- Keep the change independent from caller-prepared return buffers and `prep`.

## Non-Goals

- Do not introduce `prep`.
- Do not remove, deprecate, or redesign `CharFormatter`.
- Do not add a hidden runtime string builder.
- Do not add general string-concatenation changes beyond interpolation lowering.
- Do not add built-in `wstring` or `astring` interpolation.
- Do not change ordinary overload resolution.
- Do not change existing `new`, `init`, `within`, `thrown`, or `catch`
  semantics.

## Supported Forms

### Strings

An interpolation can initialize a `string`:

```camp
string a = $"abc";
string b = $"{a}123";
```

When the interpolation contains runtime holes, the expression eagerly evaluates
the holes, formats them, writes the full result into a character buffer with a
trailing `'\0'`, and produces a `string` pointing at the beginning of that
buffer.

Without `new`, the backing buffer has the same scoped lifetime as an equivalent
`init char[...]` allocation.

### Mutable And Const Character Arrays

An interpolation can initialize mutable and const character arrays:

```camp
char[] c = $"def";
const char[] d = $"456{c}";
```

For `char[]` and `const char[]`, the result contains exactly the formatted text.
The result is not null-terminated unless the text itself contains a terminator.
Without `new`, the backing buffer has the same scoped lifetime as an equivalent
`init char[...]` allocation.

### Fixed Character Arrays

An interpolation can initialize a fixed `char` array:

```camp
const char[] d = "world";
fixed char[5] e = $"abcdefg{d}";

int five = 5;
fixed char[5] f = $"Hi{five}";
```

Fixed-buffer interpolation follows the same behavior as fixed-buffer
initialization from string text:

- when the formatted text is longer than the fixed buffer, the prefix that fits
  is initialized;
- when the formatted text fits and the fixed-buffer string initialization rules
  require a terminator, the result is zero-terminated;
- if the compiler can prove the minimum interpolation length is too large for a
  fixed buffer in a context where an equivalent string literal would diagnose,
  it should report the same kind of diagnostic.

In the examples above, `e` receives only `abcde`. `f` receives `Hi5` followed by
a zero terminator and then any remaining ordinary fixed-buffer initialization
state required by existing fixed-array rules.

### `auto`

The implicit type of an interpolation expression remains `string`:

```camp
auto g = $"456{c}";
```

For runtime interpolation without `new`, the inferred `string` points at scoped
storage, just like an explicit `string` target would.

### `new`

`new` applied to an interpolation allocates the interpolation result buffer on
the heap:

```camp
int number = 41;
string h = new $"The answer is {number}";
char[] chars = new $"The answer is {number}";
```

`new` applies only to the interpolation result allocation. It does not imply heap
allocation for hole values.

The result may target `string` or `char[]`. For `string`, the heap buffer
includes a trailing `'\0'`. For `char[]`, the heap buffer contains exactly the
formatted text and no implicit terminator.

### Formatter Values In Holes

`CharFormatter` remains valid and useful inside interpolation holes:

```camp
string i = $"The date is {Date.today().format}";
```

If a hole expression already has `CharFormatter` type, interpolation uses that
formatter to measure and write the hole text. If a hole expression has another
type, interpolation uses the existing formatter lookup/ascription behavior to
find an accessible formatter such as `int.format`.

This proposal does not change how formatter methods are declared. Existing
formatters still use the current `CharFormatter` callable protocol.

## Evaluation And Formatting

Interpolation is eager. Each hole expression is evaluated at the interpolation
site, in source order. The result is materialized immediately.

The compiler must preserve the ordinary observable behavior of evaluating the
interpolation once:

- literal segments contribute their literal characters;
- each runtime hole expression is evaluated once;
- each hole's formatter is measured and written according to the existing
  `CharFormatter` protocol;
- the interpolation result is produced as a concrete `string`, `char[]`,
  `const char[]`, fixed `char[N]`, or heap-allocated result.

A hole may produce zero characters. Every runtime hole must evaluate to a type
that is capable of producing characters, either because it is already a
`CharFormatter` or because an accessible formatter can be found for it.

If a hole expression throws, or if a formatter call throws, the thrown value
propagates normally according to ordinary Camp throwing rules. Interpolation
does not implicitly catch, ignore, or translate thrown values.

## Target Typing And Overload Resolution

Interpolated strings follow ordinary Camp target typing and overload resolution.
This proposal does not add special overload disambiguation.

A call such as:

```camp
Console.writeLine($"Status: {status}");
```

selects the same kind of text overload that the corresponding literal call would
select:

```camp
Console.writeLine("Status: ready");
```

If a callable surface provides multiple viable text overloads and Camp cannot
select one, the call is ambiguous and the source must disambiguate explicitly
using ordinary Camp mechanisms.

## Lifetimes

This proposal introduces no new lifetime category.

Non-`new` interpolation uses storage equivalent to `init char[...]`. The result
may be used wherever the equivalent scoped `init` result may be used, and it is
rejected wherever that scoped result could not be returned, stored, yielded,
captured, or otherwise retained.

`new $"..."` uses ordinary `new` storage and follows existing escaped ownership
and cleanup rules.

Async functions and generator methods receive no special interpolation rules. A
non-`new` interpolation that requires scoped result storage has exactly the same
async/generator legality as equivalent explicit `init char[...]` storage.

## Constant Interpolation

An interpolation with no runtime holes may still be treated as constant wherever
existing constant string rules allow it:

```camp
auto text = $"abcdefg";      // string by default
char[] chars = $"abcdefg";   // mutable char array target
```

Targeting mutable storage asks for mutable storage even if the interpolation has
only constant text.

## Lowering

Runtime interpolation lowers conceptually in three phases:

1. evaluate hole expressions and obtain formatter values for runtime holes;
2. compute the required result length;
3. allocate or initialize the target result buffer and write all segments.

For a `char[]` target:

```text
required = literal lengths + formatted hole lengths
buffer = init char[required]
write content into buffer
result = buffer
```

For a `string` target:

```text
required = literal lengths + formatted hole lengths
buffer = init char[required + 1]
write content into buffer[0..required]
buffer[required] = '\0'
result = (string)buffer.elements
```

For `new` string target:

```text
required = literal lengths + formatted hole lengths
buffer = new char[required + 1]
write content into buffer[0..required]
buffer[required] = '\0'
result = (string)buffer.elements
```

The compiler may choose a more efficient internal representation, but it must
preserve the source-level behavior above.

## Generated Code Size

Generated C for interpolation must stay compact enough for small and embedded
targets. The compiler should avoid emitting large repeated copy/write sequences
at every interpolation site.

The compiler may emit internal helper functions for operations such as:

- copying literal segments into a destination buffer;
- advancing a write cursor;
- invoking an existing `CharFormatter` into an output slice;
- null-terminating a string result.

Any generated helper must be internal, non-exported, emitted at most once per
binary where practical, and named with compiler-reserved names.

## Compiler Impact

Implementation areas:

- interpolation analysis:
  - target-type runtime interpolation to `string`, `char[]`, `const char[]`,
    fixed `char[N]`, and `new` result forms;
  - keep `auto` interpolation as `string`;
  - bind runtime holes through existing `CharFormatter` behavior;
  - allow existing formatter values in holes;
- lowering:
  - replace formatter-delegate interpolation lowering with eager result
    materialization;
  - preserve once-only hole evaluation;
  - allocate or initialize result storage according to target type;
  - preserve thrown propagation from hole evaluation and formatter calls;
- C emission:
  - emit compact result-buffer write code;
  - emit internal helpers where they reduce generated code size;
  - avoid exporting interpolation helpers;
- diagnostics:
  - diagnose holes that cannot produce characters;
  - diagnose unsupported interpolation targets;
  - diagnose scoped interpolation results where equivalent `init` storage would
    be illegal;
  - follow fixed-buffer string-literal diagnostic policy when interpolation
    minimum length can be proven too large.

## Testing Plan

Use the minimum number of additional tests that covers the cross-product of
target typing, eager evaluation, existing formatter compatibility, lifetimes,
and generated C shape.

Semantic and runtime coverage:

- `string a = $"abc";`
- `string b = $"{a}123";`
- `char[] c = $"def";`
- `const char[] d = $"456{c}";`
- `auto g = $"456{c}";`
- `string h = new $"The answer is {number}";`
- `char[] heapChars = new $"The answer is {number}";`
- `string i = $"The date is {Date.today().format}";`
- formatter holes such as `int.format`;
- hole expression evaluation exactly once and in source order;
- thrown formatter/hole propagation;
- fixed `char[N]` prefix behavior for oversized results;
- fixed `char[N]` zero termination when the formatted text fits;
- scoped lifetime rejection where equivalent `init char[...]` storage would be
  rejected.

Lowering and C-emission coverage:

- runtime interpolation no longer lowers to a `CharFormatter` lambda/context;
- generated code uses eager result storage;
- representative generated C does not duplicate large literal-write logic at
  every site when helper lowering is selected;
- internal helpers are not exported.

Documentation coverage:

- language guide interpolation section;
- semantics interpolation/lowering section;
- LLM guide examples for interpolation targets and lifetime behavior.

## Compatibility

This is a preview-language breaking change for source that relied on runtime
interpolation itself being a `CharFormatter`.

Source that explicitly uses `CharFormatter` values remains valid. Existing
formatter methods remain valid. The migration is specifically from
"interpolation expression as formatter delegate" to "interpolation expression as
eager text result."
