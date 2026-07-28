# Rejected Proposal: Streaming Invocation Statement

Status: Rejected.
Proposal date: 2026-07-25
Last updated date: 2026-07-25

This proposal has been rejected. Its primary motivation was to provide a
convenient, interpolation-like way to construct and output text containing
values of different types. Although streaming invocation can express repeated
writes, it does not solve the broader string-construction problem ergonomically
enough to justify a new operator and its associated language complexity.

## Rejection Rationale

The proposal falls short of its primary string-building motivation in three
important ways.

First, it does not provide a convenient expression that produces a string.
Constructing a string from several components still requires a `StringBuilder`
and one or more surrounding statements. Common cases such as constructing text
for a UI label or appending a file name to a path remain awkward. Streaming
invocation shortens repeated calls, but does not address the fundamental
ergonomic gap.

Second, the syntax is not nearly as concise as proper string interpolation. Its
advantage over existing alternatives such as repeated fluent `add` or `append`
calls, or a variadic-style `concat(a, b, c)` operation, is too small to carry
the feature on its own.

Third, the left-arrow syntax does not look C-like and does not add meaningful
expressive power. Uses such as adding several items to a list are somewhat
shorter, but they do not enable anything that ordinary calls cannot already
express clearly. Those secondary conveniences are not compelling enough to
justify the syntax, semantics, compiler work, documentation burden, and
additional cognitive load.

Overall, the feature does not provide enough benefit to pay for its language
cost. Future work in this area should focus on direct, expression-level string
interpolation or construction rather than a general repeated-invocation
operator.

## Summary

This proposal adds the **streaming invocation statement**, written with `<-`.
It invokes one function or callable target one or more times, using a separate
argument list for each arrow:

```camp
Console.write <- "Processed " <- itemCount <- " items." <- '\n';
```

The statement is equivalent to a sequence of ordinary calls:

```camp
Console.write("Processed ");
Console.write(itemCount);
Console.write(" items.");
Console.write('\n');
```

The target is captured once where a runtime target exists, but every arrow is
bound as an independent call. Different arrows may therefore select different
overload entries, infer different generic arguments, and perform separate
virtual or interface dispatch.

Streaming invocation is parsed as an expression-shaped construct for resilient
syntax recovery, but it is semantically valid only as a complete statement. It
does not produce a value. It is intended for repeated effectful calls such as
writing, appending, collecting, hashing, serializing, binding, and emitting.
The selected function for every arrow must:

- return `void`, exact source-level `this`, or `thrown(E)`;
- declare at least one non-`this` formal parameter without a default value; and
- not be a `once` callable target or a lifecycle declaration.

An ineligible declaration still participates in ordinary lookup, overload
selection, generic inference, and callable binding. The compiler diagnoses
ineligibility only after it has selected the call target. Eligibility therefore
does not change which overload wins.

## Motivation

The primary motivation is incremental text construction and output: building a
string, writing to the console or a stream, and producing other textual content
that mixes text with values of non-text types. Ordinary calls express these
operations accurately, but repeat the least interesting part of the statement:

```camp
Console.write("user ");
Console.write(userId);
Console.write(" completed ");
Console.write(taskCount);
Console.write(" tasks");
Console.write('\n');
```

The repeated target can obscure the sentence being produced. Streaming
invocation keeps the target visible once and lets the source read in the same
order as the output:

```camp
Console.write <- "user " <- userId <- " completed " <- taskCount <- " tasks" <- '\n';
```

This is not string interpolation. Every arrow remains an ordinary call, so the
selected overload controls formatting and behavior. The compact layout is
important: it allows a sentence, message, or log line to remain readable as one
unit instead of distributing its fragments vertically.

The same mechanism can benefit non-text APIs, but those uses are secondary:

```camp
jsonArray.add
	<- 12
	<- "ready"
	<- true;
```

The three arrows can select three different overload entries:

```camp
jsonArray.add(12);
jsonArray.add("ready");
jsonArray.add(true);
```

The feature is named "streaming invocation" because a sequence of argument
lists is sent to one invocation target. It is not a stream type, lazy sequence,
iterator, pipe, channel, or asynchronous completion protocol.

## Goals

- Make incremental text construction and output read naturally when textual
  fragments are mixed with values of other types.
- Make repeated calls to one effectful target concise and readable.
- Preserve all ordinary argument-list features, including named arguments,
  `out`, `catch`, `within`, defaults, source-capture defaults, overload
  selectors, and generic inference.
- Bind every arrow independently so heterogeneous overload families work
  naturally.
- Evaluate a member receiver or callable-producing expression only once.
- Preserve ordinary evaluation order, error propagation, lifetime checking,
  dynamic dispatch, and cleanup behavior.
- Prevent the syntax from suggesting that one call's result becomes the next
  call's receiver.
- Keep the construct out of expression contexts where its discarded results and
  repeated effects would be surprising.
- Lower to ordinary call machinery so expanded forms, interfaces, callables,
  generic capabilities, and target-specific ABI behavior continue to have one
  implementation.

## Non-Goals

This proposal does not add:

- a general pipeline or composition operator;
- implicit conversion of a call result into the next call's receiver;
- string interpolation or formatting semantics;
- lazy evaluation, buffering, batching, transactions, or rollback;
- parallel invocation;
- implicit awaiting or serialized asynchronous completion;
- a way to invoke a zero-argument operation repeatedly;
- a value representing the result of the statement;
- special overload ranking for streaming calls;
- support for consuming a `once` target;
- operator overloading for `<-`;
- metadata schema or ABI surface changes.

An awaited form may be proposed separately in the future. Such a feature would
need to define whether each call is awaited before the next call begins. This
proposal deliberately makes no such promise.

## Everyday Use

### Output And Formatting

The most immediately recognizable use is an overloaded output sink:

```camp
void printSummary(string name, int completed, int total)
{
	Console.write <- name <- ": " <- completed <- " of " <- total <- '\n';
}
```

Each value is formatted by the ordinary `Console.write` overload selected for
its type. No combined string is allocated by the language feature.

Every arrow is still a separate call. Streaming invocation does not make the
output atomic, so another producer may interleave output between segments when
the shared sink does not provide a stronger serialization guarantee.

### Heterogeneous Containers

Dynamic document and serialization types often expose one overloaded insertion
operation:

```camp
class JsonArray
{
	void add(overload int value)
	{
	}

	void add(overload bool value)
	{
	}

	void add(overload string value)
	{
	}
}

void addHeader(JsonArray* values)
{
	values.add
		<- 3
		<- "camp"
		<- true;
}
```

This is clearer than a fluent chain because the following arrow always invokes
`values.add` again. It never invokes a method on an earlier result.

### Builders And Accumulators

A method returning `this` is eligible even though its result is ignored:

```camp
class TextBuilder
{
	this append(const char[] text)
	{
		return this;
	}
}

void writeGreeting(TextBuilder* builder, const char[] name)
{
	builder.append <- "Hello, " <- name <- ".";
}
```

The ordinary fluent form remains available when its result is useful:

```camp
TextBuilder* same = builder.append("Hello, ").append(name).append(".");
```

The streaming form emphasizes repeated input. The fluent form emphasizes the
returned receiver and chaining through it.

### Hashing And Serialization

Streaming invocation is useful when a protocol value is assembled from
successive fields:

```camp
struct PacketHeader
{
	uint version;
	uint flags;
	nuint payloadLength;
}

class HashWriter
{
	void append(overload uint value)
	{
	}

	void append(overload nuint value)
	{
	}

	void append(overload const byte[] value)
	{
	}
}

void hashPacket(
	HashWriter* hash,
	PacketHeader header,
	const byte[] payload)
{
	hash.append
		<- header.version
		<- header.flags
		<- header.payloadLength
		<- payload;
}
```

This syntax does not make the operation atomic. If a later call fails or
propagates an error, earlier effects remain.

### Diagnostics And Event Sinks

Repeated values do not need to be heterogeneous:

```camp
class DiagnosticBag
{
	void add(Diagnostic diagnostic)
	{
	}
}

void reportFailures(
	DiagnosticBag* diagnostics,
	Diagnostic missingName,
	Diagnostic invalidValue,
	Diagnostic unsupportedTarget)
{
	diagnostics.add
		<- missingName
		<- invalidValue
		<- unsupportedTarget;
}
```

Other suitable APIs include binary writers, checksum accumulators, command
encoders, database parameter binders, telemetry sinks, and test-data
collectors.

## Syntax

The parser recognizes this expression-shaped source form:

```text
streaming-invocation-expression:
    invocation-target streaming-segment+

streaming-segment:
    "<-" argument ("," argument)*
```

A streaming invocation is semantically valid only when it is directly the
complete expression of an expression statement:

```text
streaming-invocation-statement:
    streaming-invocation-expression ";"
```

The invocation target is any source form that could appear immediately before
the `(` of an ordinary call, including:

- a free or static function name;
- an instance member name;
- an overloaded function or method family;
- a `fn` value;
- a `delegate` value;
- a callable newtype value;
- a field or property that produces a callable; or
- another expression whose value has an eligible callable type.

Each segment contains at least one syntactic argument. Empty segments are not
part of the grammar:

```camp
writer.flush <-; // invalid: missing argument
```

Camp has no special `()` argument for spelling a parameterless streaming call.
Parameterless calls are deliberately unavailable through streaming invocation.

The two characters of `<-` must be adjacent. Trivia may appear before or after
the operator:

```camp
sink.add <- value;
sink.add
	<- value;
```

Separating the characters retains the ordinary comparison and unary-negative
operators:

```camp
bool earlier = left < -right;
```

An adjacent `<-` is reserved as the streaming-invocation token. Existing source
that spells a comparison with a unary negative as `left<-right` must add
whitespace.

For parsing and recovery, `<-` is below the comma that separates arguments in
one segment. This lets the parser collect a complete argument list before the
next arrow:

```camp
points.add <- 10, 20 <- 30, 40;
```

The parser precedence is only a syntax-construction rule. It does not give
streaming invocation an expression value or permit it as an operand.

### Argument Lists

The portion after each `<-` follows the same syntax as the inside of an ordinary
argument list:

```camp
encoder.write
	<- packet
	<- value: count, format: decimalFormat
	<- source, out bytesWritten
	<- payload, catch encodeError
	<- capacity, within arena;
```

Whether those arguments are valid depends on the independently selected
function for that segment. The streaming syntax does not make an invalid
ordinary argument list valid.

Commas belong to the current segment. The next adjacent `<-` begins the next
segment:

```camp
points.add
	<- 10, 20
	<- 30, 40;
```

This is equivalent to:

```camp
points.add(10, 20);
points.add(30, 40);
```

Parentheses, array literals, initializer lists, lambdas, and other nested syntax
retain their ordinary comma behavior:

```camp
callbacks.add
	<- (int value) => value > 0
	<- (int value) => value % 2 == 0;
```

## Semantic Model

### Expression-Shaped Syntax, Statement Semantics

The parser accepts streaming invocation wherever it can construct an expression
syntax node. Semantic analysis then requires the streaming invocation itself to
be the complete expression of an expression statement.

This separation is intentional. It lets an editor and parser retain the whole
construct while the user is typing, and it lets semantic analysis report a
focused placement error instead of cascading parser errors. The construct still
has no result type and cannot be used where a value is required.

Valid:

```camp
if (ready)
	Console.write <- "ready" <- '\n';

delegate void() action = () =>
{
	Console.write <- "running" <- '\n';
};
```

Invalid:

```camp
consume(Console.write <- value);          // invalid call argument
bool result = sink.add <- value;           // invalid initializer
return sink.add <- value;                  // invalid return expression
delegate void() action = () => sink.add <- value; // invalid expression lambda
(Console.write <- "grouped" <- '\n');      // invalid parenthesized expression
```

The last example can use a block lambda, as shown above.

These invalid forms should still produce complete streaming-invocation syntax
nodes. The analyzer reports that the construct is not the complete expression
of an expression statement, then may continue analyzing its target and segments
to provide useful independent binding diagnostics. Invalid placement must never
reach lowering or emission.

Because the construct has no expression semantics, it cannot be combined with
prefix or postfix operators:

```camp
await service.send <- request;       // invalid
postpone service.send <- request;    // invalid
!(sink.add <- value);                // invalid
```

It also cannot be nested inside one of its own argument lists:

```camp
outer.add <- (inner.add <- value); // invalid nested streaming invocation
```

Use a preceding statement when one operation must feed another:

```camp
inner.add <- value;
outer.add <- inner;
```

Without the parentheses, arrows always continue the current streaming target:

```camp
outer.add <- inner.add <- value;
```

This is one stream to `outer.add` with two segments. The first segment supplies
the method-reference expression `inner.add`; it is valid only if the selected
`outer.add` overload accepts that callable value.

### Target Evaluation And Capture

Target capture happens before the first segment's arguments are evaluated.

For an instance member family, the receiver expression is evaluated once. The
member name remains available for independent binding of every segment:

```camp
getWriter().write
	<- first
	<- second;
```

`getWriter()` runs once, not twice.

Capture preserves receiver semantics:

- a class receiver captures its object pointer value;
- a mutable struct lvalue captures the receiver location rather than copying
  the struct;
- an interface receiver preserves its interface instance and vtable shape;
- an expanded receiver uses the ordinary materialization and lifetime rules
  required for a bound method reference; and
- a temporary receiver remains alive through the complete streaming statement.

For a free or static function family, there is no runtime receiver to capture.
The name is resolved independently for each segment.

For a concrete `fn`, `delegate`, callable newtype, callable field, callable
property, or callable-producing expression, the callable value is evaluated and
captured once:

```camp
getConsumer()
	<- first
	<- second;
```

`getConsumer()` runs once. Both segments invoke the same captured callable
value.

Capturing a target value means later assignments to the source variable do not
retarget the remaining calls:

```camp
consumer
	<- replaceConsumer(out consumer)
	<- nextValue;
```

The second call still uses the consumer captured before the first segment was
evaluated.

The capture itself does not widen lifetimes. A scoped receiver, delegate
context, interface adapter, or backing array must remain valid through the
statement under the ordinary lifetime rules.

### Independent Binding

Every segment is converted into a distinct source-level call and bound
independently. For each segment, the compiler performs ordinary:

- name and member lookup;
- overload-family selection;
- generic inference and constraint validation;
- named-argument binding;
- default-argument insertion;
- `constof` substitution;
- lifetime relation solving;
- interface and virtual call selection;
- async manual-call validation; and
- target capability validation.

For example:

```camp
values.add
	<- 42
	<- "answer"
	<- true;
```

may bind to `addInt`, `addString`, and `addBool`, respectively.

Explicit type arguments written on the target apply to every segment:

```camp
collector.add<int>
	<- first
	<- second;
```

Without explicit type arguments, each segment infers its own substitutions:

```camp
collector.add
	<- firstInt
	<- secondString;
```

If a target is overloaded, streaming eligibility is not used to filter
candidates. An ineligible overload can win ordinary overload selection and then
produce an eligibility diagnostic. An ambiguous ordinary call remains
ambiguous.

### Evaluation Order

The observable order is:

1. Evaluate and capture the receiver or callable target, if one exists.
2. Process segments from left to right.
3. For each segment, evaluate arguments and defaults in the ordinary call
   order.
4. Invoke the independently selected function.
5. Discard its permitted source result and proceed to the next segment if
   control remains in the statement.

This statement:

```camp
getSink().add
	<- makeFirst()
	<- makeSecond();
```

has the conceptual order:

```camp
auto capturedSink = getSink();
capturedSink.add(makeFirst());
capturedSink.add(makeSecond());
```

The conceptual temporary is not a promise that every receiver is copied into an
`auto` local. Lowering must preserve lvalue, fixed-storage, expanded-receiver,
constness, and lifetime behavior.

### Return Eligibility

After a segment has selected its call target, the selected declaration must
have one of these source return forms:

- `void`;
- exact source-level `this`; or
- `thrown(E)`.

Examples:

```camp
class BufferWriter
{
	void write(const byte[] value)
	{
	}

	this append(const byte[] value)
	{
		return this;
	}
}

extern thrown(ParseError) parsePart(
	const char[] text,
	out int value);
```

The rule is based on the source declaration, not its lowered ABI result. A
method returning the concrete receiver type is not equivalent to exact
source-level `this`:

```camp
class Builder
{
	Builder* append(const char[] text) // ineligible
	{
		return this;
	}

	this appendPart(const char[] text) // eligible
	{
		return this;
	}
}
```

The restriction prevents silent loss of an ordinary result and prevents the
syntax from suggesting that the next arrow uses the preceding result:

```camp
int count = values.add(value); // ordinary result is meaningful
values.add <- value;           // invalid if add returns int
```

`void` and `this` are useful indicators of effect-oriented APIs, but this is not
an effect system. The compiler does not attempt to prove that an eligible
function mutates state or performs I/O.

### Required Formal Parameter

The selected function must declare at least one formal parameter that:

- is not `this`; and
- has no default value.

The rule applies to source formal parameters, not generated lowered ABI
components. In particular, an async completion callback introduced by lowering
does not by itself make an async declaration eligible.

An overload selector qualifies because overload selectors cannot have defaults:

```camp
void add(overload int value) // eligible
{
}

void add(overload bool value) // eligible
{
}
```

A function whose non-`this` parameters all have defaults is ineligible even
when a segment supplies an explicit argument:

```camp
class Switch
{
	void setEnabled(bool enabled = true)
	{
	}
}

toggle.setEnabled <- false; // invalid: no required non-this formal parameter
```

Such APIs remain available through ordinary calls:

```camp
toggle.setEnabled(false);
toggle.setEnabled();
```

This restriction keeps streaming invocation focused on APIs that structurally
require input. It also avoids adding an empty-segment spelling for zero-argument
calls.

### `out` And `catch` Scope

Each segment is analyzed in order in the containing statement scope. A
declaration introduced by `out` or `catch` in an earlier segment is available
to later segments and after the streaming statement, exactly as if the calls
had been written as separate statements:

```camp
reader.read
	<- firstText, out int firstValue
	<- secondText, out int secondValue;

Console.write <- firstValue <- ", " <- secondValue <- '\n';
```

The ordinary definite-assignment rules still apply. Streaming syntax does not
make a conditionally assigned value safe to read.

### Error Flow

Calls with a trailing `thrown` parameter or a `thrown(E)` return use ordinary
Camp error flow.

An uncaught error stops the streaming statement. Later segments are not
evaluated:

```camp
decoder.append
	<- header
	<- payload
	<- checksum;
```

If `decoder.append(header)` propagates an error, neither `payload` nor
`checksum` is evaluated or appended.

A segment can catch its own error and allow the following segment to run:

```camp
decoder.append
	<- header, catch DecodeError headerError
	<- payload, catch DecodeError payloadError;
```

This is equivalent to two ordinary calls with two ordinary catch arguments.
Earlier successful effects are not rolled back when a later call fails.

Generated cleanup and `finally` behavior follows the same control-flow path as
the equivalent call sequence. The captured target remains subject to its
ordinary cleanup obligation.

### Source-Capture Defaults

Default arguments are inserted independently for every segment. `sourceof`
captures argument text from the current segment, and `caller(...)` captures the
call-site facts for that segment:

```camp
class Trace
{
	void record(
		bool condition,
		string expression = sourceof(condition),
		uint line = caller(sourceline))
	{
		Console.write <- expression <- " at line " <- line <- '\n';
	}
}

trace.record
	<- itemCount > 0
	<- buffer.length >= requiredLength;
```

Conceptually, the two calls receive different `expression` strings. The source
location for a segment should be anchored at that segment's `<-` token so
multi-line statements report useful line numbers.

Generated thunks, default substitution, and lowering must preserve the
segment's source provenance rather than substituting the target's location or
the first arrow's location.

### Dynamic Dispatch

The receiver value is captured once, but dispatch is performed normally for
every call:

```camp
sink.write
	<- first
	<- second;
```

For a virtual class method, each call uses ordinary virtual dispatch on the
captured class instance. For an interface method, each call uses the captured
interface instance and its ordinary slot dispatch. For overload families,
compile-time overload selection occurs independently before dynamic dispatch.

The phrase "capture the receiver" does not mean "capture one resolved function
pointer" for a member family.

### `fn`, `delegate`, And `once`

Concrete `fn` and `delegate` values may be streaming targets when their
signatures satisfy the return and required-parameter rules:

```camp
void sendValues(delegate void(int value) consume)
{
	consume
		<- 10
		<- 20
		<- 30;
}
```

A `once` value is never an eligible streaming target, including a statement
with only one segment:

```camp
void completeOnce(once void(int result) complete)
{
	complete <- 42; // invalid: once targets cannot use streaming invocation
}
```

Use the ordinary call syntax when consuming a `once` value:

```camp
complete(42);
```

This restriction keeps target eligibility independent of segment count and
keeps single-use continuation APIs out of a repeated-call construct.

A `once` value may still appear as an argument. This matters for manual async
completion callbacks.

### Async Functions

Async declarations receive no special streaming semantics. Each segment must be
a valid ordinary manual async call, including an explicit completion callback
when the existing async call rules require one:

```camp
extern class PacketSender
{
	extern async void sendAsync(const byte[] packet);
}

void beginSends(
	PacketSender* sender,
	const byte[] firstPacket,
	const byte[] secondPacket)
{
	sender.sendAsync
		<- firstPacket, () => { Console.writeLine("first complete"); }
		<- secondPacket, () => { Console.writeLine("second complete"); };
}
```

The calls are initiated in segment order. Their completions are not implicitly
serialized, and the second call does not wait for the first completion callback.

The ordinary source return eligibility rule still applies. For example, an
`async void` declaration can be eligible, while an `async int` declaration is
ineligible because its source success result is neither `void`, `this`, nor
`thrown(E)`.

The generated completion callback is not a `once` streaming target; it is a
`once` argument passed to the async function, which is permitted.

Streaming invocation cannot itself be the operand of `await` or `postpone`.
An awaited streaming feature, if desired, requires a separate proposal.

### Receiver Invalidation

Streaming invocation does not introduce stronger ownership or validity
guarantees than ordinary calls. If an earlier method logically closes,
transfers, or invalidates its receiver, later calls have the same validity
problem as the equivalent separately written statements.

The compiler should diagnose invalidation when existing ownership, lifetime, or
flow rules can prove it. It does not need to infer undocumented semantic effects
from a method name.

## Diagnostics And Error Cases

Diagnostics should identify the selected target and point to the segment that
caused the error.

When a segment binds successfully but the selected function or callable is
ineligible for streaming invocation, the diagnostic range covers the complete
segment: it begins at that segment's `<-` token and ends at the last token of
that segment's final argument. It does not cover the following arrow or the
statement semicolon.

This full range applies to ineligible return types, missing required formal
parameters, `once` targets, lifecycle targets, and other streaming-eligibility
failures. Ordinary binding errors retain their most useful narrow ranges, such
as the offending argument, missing-argument insertion point, or ambiguous
target.

### Missing Segment Argument

```camp
sink.write <-;
```

Suggested diagnostic:

```text
Streaming invocation requires at least one argument after '<-'.
```

### Target Is Not Callable

```camp
int count = 3;
count <- 1;
```

Suggested diagnostic:

```text
Streaming invocation target of type 'int' is not callable.
```

### Ordinary Result Would Be Discarded

```camp
class Values
{
	int add(int value)
	{
		return value;
	}
}

values.add <- 1;
```

Suggested diagnostic:

```text
Streaming invocation target 'add' returns 'int'; targets must return void, this, or thrown(E).
```

The diagnostic occurs after `add(int)` has been selected.
Its range covers `<- 1`, including the arrow.

### Concrete Receiver Return Is Not `this`

```camp
class Builder
{
	Builder* append(const char[] text)
	{
		return this;
	}
}

builder.append <- "text";
```

Suggested diagnostic:

```text
Streaming invocation target 'append' returns 'Builder*'; expected exact 'this'.
```

The range covers the complete `<- "text"` segment.

### No Required Non-`this` Parameter

```camp
class Cursor
{
	void advance(int amount = 1)
	{
	}
}

cursor.advance <- 2;
```

Suggested diagnostic:

```text
Streaming invocation target 'advance' has no non-this parameter without a default value.
```

The range covers the complete `<- 2` segment.

### Ineligible Overload Is Still Selected

```camp
class Collector
{
	void add(overload int value)
	{
	}

	int add(overload string value)
	{
		return 0;
	}
}

collector.add
	<- 1
	<- "value";
```

The first segment is valid. The second selects `addString` by the ordinary
overload selector and then reports that its `int` result is ineligible. The
compiler must not ignore `addString` and attempt to bind the string argument to
`addInt`. The diagnostic covers the complete `<- "value"` segment, beginning
with its arrow.

### `once` Target

```camp
complete <- result;
```

When `complete` has type `once void(int)`, suggested diagnostic:

```text
Streaming invocation cannot consume a once callable; use an ordinary call.
```

The range covers the complete `<- result` segment.

### Expression Context

```camp
return sink.write <- value;
```

Suggested diagnostic:

```text
Streaming invocation is valid only as the complete expression of a statement.
```

The permissive parser retains the complete syntax construct so the compiler
does not produce misleading follow-on diagnostics for every arrow.

### `await` And `postpone`

```camp
await sender.send <- packet;
postpone sender.send <- packet;
```

Suggested diagnostics should state that streaming invocation cannot be an
`await` or `postpone` operand and should not imply that the selected method is
necessarily non-async.

### Ordinary Call Errors

Missing arguments, extra arguments, invalid named arguments, conversion
failures, lifetime failures, missing generic capabilities, unsupported target
operations, inaccessible members, and ambiguous overloads retain their ordinary
call diagnostics. Their ranges should point into the failing segment.

## API, ABI, Metadata, And Compatibility

Streaming invocation adds no declaration surface. It does not change:

- callable signatures;
- overload-family symbol names;
- generated Camp API headers;
- metadata JSON;
- C headers;
- vtable layout;
- interface slots;
- generic ABI capability order; or
- native symbol spelling.

After lowering, emitted calls use the same ABI as separately written ordinary
calls.

The lexical token introduces one source compatibility change. Adjacent
`left<-right` becomes streaming syntax rather than `<` followed by unary `-`.
The comparison remains available as `left < -right`. Because `<-` is otherwise
new syntax, this is expected to be a narrow compatibility cost.

## Implementation Strategy

### 1. Token And Parser Surface

The tokenizer already emits `<` and `-` as individual symbol tokens, and the
parser already recognizes multi-character operators by adjacency. `<-` should
use the same adjacency mechanism.

The general expression parser should recognize a dedicated
`StreamingInvocationExpressionSyntax` node. It should do so in declarations,
arguments, lambda bodies, conditions, and other expression-bearing contexts,
even though semantic analysis will reject every placement except the root of an
expression statement.

This permissive parser behavior is important for incomplete source and editor
overlays. The parser should preserve a nested or misplaced streaming construct
rather than fail at `<-`, reinterpret it as comparison plus unary negation, or
lose the remainder of the containing statement.

Add syntax nodes that preserve:

- the invocation target;
- every `<-` token;
- every segment's `ArgumentListSyntax`;
- comma tokens;
- precise source ranges for recovery and diagnostics.

The containing `ExpressionStatementSyntax` continues to own the terminating
semicolon. The streaming syntax node should be constructible inside any larger
expression node.

The streaming parser layer is below segment commas for syntax grouping. It
should still reuse ordinary argument parsing within every segment. Adjacent
`<-` is reserved streaming syntax everywhere; `< -` remains comparison followed
by unary negation.

The parser reports structural problems such as a missing segment argument.
Semantic analysis, not the parser, reports that a successfully parsed streaming
invocation is not in the required statement-like position.

Likely compiler areas include:

- `src/Camp.Compiler/CampParser.cs`;
- `src/Camp.Compiler/SyntaxNode.cs`;
- `src/Camp.Compiler/SyntaxNodeTraversal.cs`;
- parser and syntax dump tests.

### 2. Bindable Expression Shape

Add a dedicated bindable expression-shaped node, conceptually:

```text
StreamingInvocationExpression
    Target
    Segments[]
        ArrowSource
        Arguments[]
        BoundCall
```

It should not be represented as `BinaryExpression`, `CommaExpression`, or one
large `CallExpression`. Those shapes would incorrectly imply ordinary operator
semantics or one shared overload binding. The dedicated node has no usable
semantic value type even though it derives from or participates in the
expression syntax/bindable hierarchy for parser resilience.

The bindable builder should create ordinary `ArgumentExpression` nodes for each
segment so named arguments, `out`, `catch`, `within`, initializer arguments,
lambdas, and source ranges reuse existing behavior.

When body analysis sees a streaming node directly as the complete expression of
an `ExpressionStatement`, it analyzes the node as a streaming statement. When
ordinary expression analysis encounters the node anywhere else, including
inside grouping parentheses, it reports the placement diagnostic while
retaining and walking the target and segments for additional useful
diagnostics.

Likely compiler areas include:

- `src/Camp.Compiler/BindableNode.Expressions.cs`;
- `src/Camp.Compiler/BindableNodeBuilder.cs` and its expression/statement
  partials;
- `src/Camp.Compiler/BindableNodeTraversal.cs`;
- bindable serializers and dump writers.

### 3. Target Classification And Analysis

Body analysis should classify the target into one of these cases:

1. member family with a captured receiver;
2. free/static function family with no runtime capture;
3. concrete callable value captured once; or
4. invalid/non-callable target.

For a member family, analyze the receiver once to establish its source type,
constness, lifetime facts, and generic substitutions. Then construct a distinct
call target for every segment using the same semantic receiver placeholder and
member name.

For a free/static family, reuse the name for each distinct call.

For a concrete callable, analyze and capture the callable expression once, then
bind every call against its captured callable type.

For each segment:

1. create or retain a distinct `CallExpression`;
2. invoke the existing ordinary call resolver;
3. preserve the selected `FunctionDefinition`, callable parameters, generic
   substitutions, const facts, lifetime facts, and rewrites;
4. validate streaming eligibility after selection; and
5. continue analysis in the same body scope so earlier `out`/`catch`
   declarations are visible.

The implementation should centralize the eligibility predicate and diagnostic
format. It must inspect source declaration kinds and source return types, not
lowered ABI components. An eligibility diagnostic uses the complete segment
range from its arrow through its final argument.

Likely compiler areas include:

- `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.cs`;
- overload and callable analysis helpers;
- `constof`, generic, lifetime, flow, and async call validation;
- diagnostic range helpers.

### 4. Flow And Lifetime Analysis

Flow analysis should visit the target capture and calls in source order.
Thrown flow, definite assignment, `out` assignment, and reachability should be
identical to the corresponding ordinary statement sequence.

Lifetime analysis must distinguish:

- a captured class pointer value;
- a captured struct receiver location;
- a scoped interface adapter;
- an expanded receiver requiring materialized storage;
- a delegate's call and context components; and
- a callable-producing temporary.

Capture cannot convert a scoped target into escaped storage. Its required
lifetime is the lifetime of the streaming statement and any cleanup scopes
created by its component expressions.

Receiver invalidation and delete flow should be checked between calls where
existing analysis supports it.

### 5. Lowering

Lower only after source binding and eligibility validation. Early syntactic
desugaring would lose source provenance and make one-time target evaluation,
source-capture defaults, overload diagnostics, and receiver materialization
harder to preserve.

Lowering should produce an ordinary block or statement sequence:

1. synthesize the narrowest valid target capture, if required;
2. emit one ordinary `ExpressionStatement` containing each bound
   `CallExpression`;
3. preserve segment-specific source syntax on every call;
4. pass every call through existing default, thrown, generic capability,
   interface, instance-call, delegate, expanded-form, and conversion lowering;
   and
5. run target cleanup through existing cleanup lowering.

The capture cannot always be an ordinary copied local:

- mutable struct receivers may require an address/reference to the original
  storage;
- fixed structs must not be copied;
- expanded receiver components may require one materialized context;
- interface and delegate values have multi-component representations; and
- property/callable-producing targets may need a temporary with deterministic
  cleanup.

Existing bound-method and instance-call lowering should be reused where it
already implements these distinctions.

Only a validated statement-position streaming node may be lowered. If lowering
normalizes it completely, `CCodeEmitter` should need no streaming-specific call
emission. It may need only defensive handling or dump support if unlowered nodes
can reach it.

### 6. Tooling

Update tooling so every segment behaves like an ordinary call site:

- syntax highlighting recognizes `<-` as the streaming arrow;
- hover shows the overload or callable selected for the segment under the
  cursor;
- signature help uses the current segment's arguments;
- go-to-definition and references point to every selected declaration;
- rename continues to operate on the target member or function name;
- diagnostics use the current document overlay and segment range;
- formatting preserves compact textual streams and sensible user-selected
  wrapping;
- debug line mapping and coverage can distinguish calls on different lines.

The VS Code grammar lives in
`extras/vscode-camp/syntaxes/camp.tmLanguage.json`. LSP behavior should reuse
the compiler syntax and semantic model rather than reconstructing segments from
text.

## Test Coverage

### Parser And Syntax Tests

Add focused `Ast` cases for:

- one segment and several segments;
- multiple arguments per segment;
- named, `out`, `catch`, and `within` arguments;
- generic targets;
- member, free, static, `fn`, delegate, callable field, and
  callable-producing targets;
- compact single-line, phrase-wrapped, and vertical formatting;
- comments and trivia around arrows;
- nested parentheses, arrays, initializers, and lambdas containing commas;
- `<-` adjacency versus `< -`;
- missing arguments, missing semicolon, and parser recovery;
- successful syntax construction in initializers, arguments, conditions,
  nested streaming forms, expression-bodied lambdas, and grouping
  parentheses.

### Semantic And Diagnostic Tests

Add semantic or `Diagnostics` coverage for:

- eligible `void`, exact `this`, and `thrown(E)` results;
- ineligible scalar, pointer, struct, interface, iterator, and other ordinary
  results;
- concrete receiver return versus exact `this`;
- at least one non-defaulting non-`this` formal parameter;
- all-default and zero-parameter declarations;
- `once` target rejection;
- constructors and destructors/lifecycle declarations;
- placement rejection after permissive parsing in initializers, arguments,
  conditions, nested streams, expression-bodied lambdas, and grouping
  parentheses;
- ineligible overload selected before eligibility validation;
- complete eligibility-diagnostic ranges from `<-` through the final argument;
- ordinary overload ambiguity;
- generic inference performed independently per segment;
- explicit generic arguments shared by all segments;
- generic capability failures isolated to one segment;
- named/default/out/catch/within binding per segment;
- per-segment `constof` substitution;
- async manual calls with explicit completion callbacks;
- async missing-completion diagnostics;
- `await` and `postpone` rejection;
- source-capture defaults and source ranges independently for every segment;
- lifetime failures for captured scoped receivers and delegates;
- invalid use after receiver deletion where existing flow analysis can prove it;
- target availability and unsupported-target diagnostics.

Diagnostics fixtures should verify line and column ranges on the failing arrow,
argument, or target. Dense fixtures are useful for related variants, but parser
recovery, eligibility, lifetime, async, and overload errors should remain
separate enough to identify regressions. Every eligibility fixture should
assert the complete arrow-through-final-argument range, while ordinary binding
fixtures should retain their existing narrow-range expectations.

### Lowering And Emission Tests

Add `Lowering` and `CEmit`/`CCompile` cases for:

- target evaluation exactly once;
- left-to-right argument and call order;
- class pointer capture;
- mutable struct lvalue capture without copying;
- fixed receiver handling;
- interface and virtual dispatch on the captured receiver;
- expanded receiver materialization;
- `fn` and delegate capture exactly once;
- callable fields and callable-producing expressions;
- overloads lowering to different concrete symbols;
- generic calls with different substitutions and capability arguments;
- default argument insertion per segment;
- source-capture default substitution per segment;
- throwing calls that stop subsequent evaluation;
- caught calls that continue;
- cleanup of a captured temporary;
- no streaming-specific ABI artifacts.

### Runtime Tests

Add small `StdRun` cases that make ordering and capture observable:

- heterogeneous `Console.write` output;
- heterogeneous container insertion;
- a receiver factory counter proving one evaluation;
- argument side-effect counters proving left-to-right order;
- a mutable struct receiver proving mutation reaches original storage;
- virtual/interface implementations proving correct dispatch;
- delegate-producing expression evaluated once;
- thrown propagation skipping later calls;
- per-segment catches allowing continuation;
- distinct `sourceof(argument)` text for different segments;
- manual async calls initiating in order without promising completion order.

Runtime cases should print small deterministic outputs and return nonzero on
semantic failure. Avoid timing-based async assertions.

### Tooling Tests

Add focused LSP coverage for:

- hover and signature help on different overload segments;
- go-to-definition/references from the shared target;
- diagnostics after editing one segment;
- formatting that preserves compact textual streams and sensible user-selected
  wraps;
- source ranges in multi-line statements; and
- document symbols and semantic highlighting remaining stable.

Update the VS Code grammar fixture or extension checks for `<-`.

## Test-Running Strategy

The feature is parser- and analyzer-heavy but also touches lowering, native
emission, runtime behavior, and editor tooling. Running the entire suite on
every platform after every small edit would be increasingly expensive. The
recommended strategy uses escalating confidence.

### During Implementation

Build once after compiler changes:

```sh
dotnet build src/camplang.sln
```

Then use the already-built test assembly for narrow repeated runs:

```sh
CAMP_TEST_KIND=Ast,Diagnostics,Lowering,CEmit,CCompile \
CAMP_TEST_CASE=streaming_invocation \
dotnet vstest \
	src/Camp.Compiler.TestRunner/bin/Debug/net10.0/Camp.Compiler.TestRunner.dll \
	--TestCaseFilter:FullyQualifiedName~GoldenFileTests
```

Run the focused semantic unit tests directly when eligibility, capture
classification, or callable shape is covered by a unit test:

```sh
dotnet vstest \
	src/Camp.Compiler.TestRunner/bin/Debug/net10.0/Camp.Compiler.TestRunner.dll \
	--Tests:Camp.Compiler.Tests.SemanticTests
```

Run focused LSP tests after syntax/range/tooling changes:

```sh
dotnet vstest \
	src/Camp.Compiler.TestRunner/bin/Debug/net10.0/Camp.Compiler.TestRunner.dll \
	--Tests:Camp.Compiler.Tests.LspServerTests
```

Add `StdRun` only when the lowered behavior is stable:

```sh
CAMP_TEST_KIND=StdRun \
CAMP_TEST_CASE=streaming_invocation \
dotnet vstest \
	src/Camp.Compiler.TestRunner/bin/Debug/net10.0/Camp.Compiler.TestRunner.dll \
	--TestCaseFilter:FullyQualifiedName~GoldenFileTests
```

Inspect every generated `.actual.*` file before updating an expected baseline.

### Local Milestone Gate

At the end of each implementation stage, run the fast golden suite on the
primary development platform:

```sh
dotnet msbuild src/test-fast.proj -p:NoBuild=true
```

Before requesting review of the implementation, run the full non-skipped suite
once on the primary development platform after the final source change:

```sh
dotnet test src/camplang.sln
```

This preserves the repository's commit gate without paying full-suite cost
during every edit.

### Per-Change Cross-Platform CI

For every implementation change, CI should:

1. build the solution on every supported release runner;
2. run the complete streaming-invocation feature corpus on every runner;
3. run parser, diagnostics, lowering, C emission, C compilation, runtime, and
   LSP tests selected by the `streaming_invocation` case prefix; and
4. run the ordinary fast golden suite on one representative primary runner,
   preferably Linux x64.

The supported release matrix currently includes:

- Windows x64;
- Windows x86;
- Linux x64;
- macOS x64; and
- macOS arm64.

Platform-neutral parser and analyzer tests should run everywhere cheaply.
Native `CCompile` and `StdRun` tests should run where the corresponding compiler
and runtime artifacts can execute. A platform that cannot execute a published
cross-architecture artifact should still run the managed compiler tests and
target-specific C-emission checks.

### Scheduled Full-Suite Rotation

Full-suite coverage should still reach every supported platform, but it need not
run on every platform for every change.

Use scheduled CI to run the full non-skipped suite on a staggered rotation:

- one platform/RID per scheduled job or day;
- all supported platforms completed within a defined rolling window, such as
  one week;
- timing data retained from `tmp/camp-test-timing.txt` to identify suites that
  should be split, cached, or made more targeted; and
- failures attributed to the first change after the last passing run on that
  platform.

Staggering the jobs keeps peak CI cost lower while ensuring that no supported
platform silently loses full-suite coverage.

### Release Gate

Before a release candidate:

- run the full non-skipped suite on every supported platform/RID;
- test the published tools through `CAMP_TEST_CAMPC`, `CAMP_TEST_LSP`, and
  `CAMP_TEST_DAP` where the artifact can execute;
- run release-archive smoke tests; and
- require the focused streaming runtime and native-compile cases to pass on
  every executable artifact.

This makes full all-platform validation a release guarantee while keeping
ordinary development feedback proportional to the feature's risk.

## Documentation Plan

### Language Guide: Everyday Presentation

The primary language-guide section should live in
`docs/language/06-functions-methods-and-callables.md`, near ordinary calls and
overload families.

The guide should introduce textual output and string building as the primary
use case:

> When a sentence or other textual output mixes text with values of different
> types, write the output target once and place each piece after an arrow.

Start with a direct before-and-after example:

```camp
Console.write("total: ");
Console.write(total);
Console.write('\n');
```

```camp
Console.write <- "total: " <- total <- '\n';
```

Then teach only the two facts needed for normal use:

1. Each arrow is a separate ordinary call, so overloads are selected separately.
2. The target is captured once; arrows do not chain through return values.

Follow with a longer sentence demonstrating that overloads format non-text
values directly:

```camp
Console.write <- userName <- " completed " <- completed
	<- " of " <- total <- " tasks." <- '\n';
```

Then show one non-textual use as a secondary application and one multi-argument
example:

```camp
points.add
	<- 10, 20
	<- 30, 40;
```

The everyday section should recommend streaming invocation when:

- textual content and non-text values form one readable output sequence;
- the same target appears in consecutive effectful calls;
- the changing arguments are more important than the repeated target; and
- seeing the sequence as one visual unit improves readability.

It should recommend ordinary calls when:

- there is only one call;
- each call needs separate commentary or error handling;
- partial completion needs to be visually prominent;
- the receiver changes between calls; or
- the return value matters.

Do not lead the everyday section with parser precedence, ABI lowering, hidden
temporaries, or the complete eligibility matrix. A short note can say that the
compiler accepts effect-style functions returning `void`, `this`, or
`thrown(E)` and reports a clear error for result-producing functions.

For textual output, the preferred style is the compact form. Keeping the
fragments together allows the source to read like the sentence it produces:

```camp
Console.write <- "Hi " <- name <- ". The time is " <- time <- ".\n";
```

When a textual sequence becomes too long, wrap at meaningful phrase or clause
boundaries rather than placing every fragment on a separate line:

```camp
Console.write <- "User " <- user.name
	<- " completed " <- completedCount
	<- " of " <- totalCount <- " tasks." <- '\n';
```

For non-textual streams, or segments with long, named, `out`, `catch`, or other
complex arguments, either compact or vertical formatting may be clearer. The
guide should leave that choice to normal readability judgment rather than
impose a streaming-specific one-arrow-per-line rule.

The guide must also state that compact textual syntax still performs separate
calls. It does not make the resulting output atomic or prevent another producer
from interleaving output between segments.

### Language Reference Updates

Update `docs/language/18-expressions-statements-and-operators-reference.md`
with the normative source grammar and clarify that:

- `<-` is parsed in the expression grammar for recovery but is semantically
  valid only as a complete expression statement;
- adjacent `<-` is reserved;
- commas divide arguments within one segment;
- parser precedence places `<-` below segment commas without giving the
  construct a semantic value;
- it is invalid in expression-only positions; and
- evaluation, capture, and error order match sequential calls.

Update:

- `docs/language/09-errors-cleanup-and-ownership-flow.md` with propagation,
  per-segment catch, and partial-effect behavior;
- `docs/language/15-async-await-and-deferred-calls.md` with manual async
  streaming and the absence of implicit awaiting;
- `docs/language/07-lifetimes-allocation-and-within.md` with the statement-long
  capture lifetime where useful.

### Semantic Supplements

Add the normative compiler behavior primarily to
`docs/semantics/14-core-expression-statement-and-access-semantics.md`.

Cross-reference or update:

- `docs/semantics/01-binding-analysis-and-lowering-pipeline.md` for
  statement-level binding before lowering;
- `docs/semantics/05-lifetime-analysis-and-flow-facts.md` for receiver/callable
  capture and sequential flow facts;
- `docs/semantics/07-callable-lowering-and-context-ownership.md` for captured
  `fn`/delegate targets and expanded receivers;
- `docs/semantics/08-async-resumption-lowering.md` for ordinary manual async
  calls and no implicit awaiting;
- `docs/semantics/13-diagnostics-source-ranges-and-error-quality.md` for
  full-segment eligibility ranges, narrow ordinary binding ranges, permissive
  parser recovery, and semantic placement diagnostics.

The supplements should make clear that eligibility is checked after ordinary
call selection and that lowering must not re-resolve the calls.

### LLM Coding Guide

Update `docs/camp-llm-coding-guide.md` with a concise generation rule:

- use streaming invocation for two or more consecutive effect-style calls to
  the same target when it improves readability;
- prefer compact streaming syntax for textual output so sentences remain
  readable as sentences;
- wrap long textual streams at meaningful phrase boundaries;
- do not use it for a single call merely because the syntax is available;
- do not assume arrows chain through returned values;
- use either compact or vertical layout for non-textual or complex segments
  according to ordinary readability;
- verify target eligibility from source or metadata;
- remember that async segments use explicit manual completion and are not
  awaited; and
- preserve ordinary `catch`, `out`, lifetime, and cleanup behavior.

### Compiler And Tooling Documentation

Update:

- `docs/compiler/07-dumps-diagnostics-and-introspection.md` if the new syntax or
  bindable node appears in dumps;
- `docs/compiler/08-language-server-and-editor-tooling.md` for segment-aware
  hover/signature behavior if that behavior needs a documented guarantee;
- `extras/vscode-camp/README.md` if the extension documents supported syntax.

## Alternatives Considered

### Keep Repeating Ordinary Calls

Ordinary calls remain fully expressive and are preferable when each operation
deserves separate explanation or handling. They are unnecessarily repetitive
for tight sequences sent to one sink.

### Fluent Chaining

```camp
builder.append("a").append("b").append("c");
```

Fluent chaining requires every call to return a receiver and dispatches the next
call on the preceding result. Streaming invocation captures one receiver and
discards permitted results. The two forms communicate different intent.

### A General Pipeline Operator

A pipeline normally forwards one result into another function:

```text
value |> parse |> validate |> save
```

Streaming invocation does not forward results and intentionally does not compose
different operations. A pipeline would be a separate, much broader feature.

### Parameterless Streaming Calls

The language could invent a new marker for a parameterless streaming segment,
but Camp currently has no empty-expression argument that could serve this
purpose. This proposal adds no such marker. Zero-argument operations remain
ordinary calls.

### Allow All-Default Functions When An Argument Is Supplied

The language could allow:

```camp
toggle.setEnabled <- false;
```

even when `setEnabled(bool enabled = true)` has no required parameter. That
would make eligibility partly dependent on how a particular segment is written
and would broaden the construct for APIs that rarely benefit from repeated
streaming. Requiring a non-defaulting formal parameter keeps the feature aimed
at APIs structurally designed to receive input.

### Filter Ineligible Overloads

Removing ineligible declarations before overload selection could make a
streaming call choose a different function from the equivalent ordinary call.
That would be surprising and would make eligibility a hidden overload-ranking
rule. This proposal binds normally and diagnoses the selected declaration
instead.

### Allow One-Segment `once` Targets

A `once` target could theoretically be safe when the statement has exactly one
segment. Making target eligibility depend on segment count complicates the
model and adds no benefit over an ordinary call. `once` targets are therefore
always rejected.

## Open Implementation Questions

The source semantics above are intended to be complete. The implementation may
still choose among internal representations for:

- the semantic receiver placeholder shared by independently bound member calls;
- the hidden capture form for mutable/fixed struct lvalues;
- whether target capture is represented explicitly before lowering or recorded
  as analysis metadata on the streaming statement;
- how signature help identifies the active segment during incomplete edits; and
- how coverage counters are attributed when several arrows share one source
  line.

Those choices must not change target evaluation count, call binding, source
diagnostics, lifetime behavior, or emitted ABI.

## Former Acceptance Criteria

Before rejection, the proposal would have been ready to move to pending if
review had agreed on:

- permissive expression-shaped parsing, statement-only semantic placement, and
  lexical treatment of adjacent `<-`;
- target capture for members and concrete callable expressions;
- independent binding without eligibility filtering;
- return, required-parameter, lifecycle, and `once` eligibility;
- sequential scope, error flow, per-segment source capture, full-segment
  eligibility diagnostics, and async manual calls;
- the lowering boundary and one-time evaluation requirements;
- the cross-platform test strategy; and
- the language-guide teaching plan.
