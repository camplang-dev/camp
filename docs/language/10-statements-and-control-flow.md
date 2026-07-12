# Statements And Control Flow

Statements control evaluation, scoping, cleanup, and flow through a function,
method, constructor, destructor, generator, or lambda body. Camp uses familiar
C-family statement syntax, but several details are Camp-specific: conditions
are explicitly boolean, `out` and `thrown` flow is source-visible, `foreach`
drives ordinary iterator protocol, and cleanup is deterministic.

## Blocks And Scope

A block is a sequence of statements inside braces:

```camp
{
	int count = values.length;
	process(count);
}
```

Declarations inside a block are visible from their declaration point to the end
of the block. Inner declarations may not create an ambiguity with names that
must remain uniquely addressable, such as expanded component names.

An empty statement is a semicolon by itself:

```camp
;
```

Empty statements are occasionally useful in generated code or deliberately
empty loop bodies, but ordinary source should prefer a small block when the
empty body is meaningful.

## Local Declarations

Local declarations introduce storage inside a body.

```camp
int remaining = total;
auto doubled = remaining * 2;
```

`auto` infers the local type from the initializer. It cannot infer from `void`,
from an initializer that requires a target type but has none, or from a fixed
array value by copy. When fixed-size storage is intended, declare it explicitly:

```camp
fixed byte[256] scratch;
byte[] view = scratch;
```

Local variable type annotations do not carry lifetime annotations directly.
Use lifetime casts on the initializer or value expression when a local needs an
explicit lifetime fact:

```camp
escaped byte* retained = (escaped byte*)source;
```

## Fixed Storage Locals

`fixed` storage declares inline storage with stable identity. This is distinct
from an array span.

```camp
fixed byte[64] packet;
byte[] writable = packet;
```

The fixed storage local owns the inline element storage. The array span is a
view over it. Returning or yielding a span view to local fixed-size storage is
invalid because the storage would not outlive the function or generator step.

## Expression Statements

An expression followed by `;` is an expression statement.

```camp
writer.writeLine("ready");
index += 1;
delete buffer;
```

Use expression statements for calls, assignments, cleanup operations, and other
expressions whose effect is the point. A pure value expression that computes a
value and discards it is usually a mistake; use `_ = expression;` when the
discard is intentional.

## `if`

`if` selects a branch based on a `bool` condition.

```camp
if (count == 0)
	return;

if (access == FileAccess.READ)
	openReader();
else
	openWriter();
```

The condition must be `bool`. Pointers, integers, enums, and newtypes do not
implicitly become conditions.

```camp
if (handle != 0)
	closeHandle(handle);

if (handle)       // ERROR
	closeHandle(handle);
```

## `while` And `do`

`while` checks the condition before each iteration. `do` executes the body once
before checking the condition.

```camp
while (remaining > 0)
{
	remaining -= sendNext(remaining);
}
```

```camp
do
{
	pollDevice();
}
while (!device.Ready);
```

The condition must be `bool`.

## `for`

`for` has initializer, condition, and increment portions.

```camp
for (nuint index = 0; index < values.length; index++)
	sum += values[index];
```

The initializer may declare a local or evaluate an expression. The condition is
optional; when present it must be `bool`. The increment portion runs after each
body execution and before the next condition check.

Use `foreach` when iterating over an array or iterator protocol value unless
manual index control is part of the algorithm.

## `switch`, `case`, And `default`

`switch` selects among `case` labels and an optional `default` label.

```camp
switch (state)
{
	case ParserState.START:
		readHeader();
		break;
	case ParserState.BODY:
		readBody();
		break;
	default:
		throw E_INVALID_STATE;
}
```

`break` exits the switch. Without `break`, control follows ordinary statement
flow according to the compiler's switch rules. Prefer explicit `break`,
`return`, or `throw` at the end of each case unless fallthrough is deliberate
and clear.

Enum switches are ordinary switches over the enum value. Camp does not require
every enum value to be listed, but a `default` branch is usually clearer for
inputs that cross API or ABI boundaries.

## `break` And `continue`

`break` exits the nearest enclosing loop or switch.

```camp
while (true)
{
	if (reader.EndOfFile)
		break;
}
```

`continue` advances the nearest enclosing loop.

```camp
foreach (const char[] line in lines)
{
	if (line.length == 0)
		continue;
	processLine(line);
}
```

Both statements participate in normal scope exit and cleanup behavior.

## Labels And `goto`

Labels use `name:`. `goto name;` jumps to a visible label in the same function
body.

```camp
retry:
	if (!tryConnect())
		goto retry;
```

Use `goto` sparingly. Structured control flow is easier for lifetime and
cleanup reasoning, and many resource-management patterns are clearer with
`try` / `finally` or expression-level `finally`.

## `return`

`return` exits the current function or method.

```camp
int clampPositive(int value)
{
	if (value < 0)
		return 0;
	return value;
}
```

A `void` function may use `return;`. A destructor cannot return a value.

When a function declares return type `this`, the return expression must be
`this` or a chain of `this`-returning instance calls on `this`.

```camp
class Builder
{
	this setName(const char[] value)
	{
		this.name = value;
		return this;
	}
}
```

Returning a value checks ordinary assignability, dependent constness,
lifetimes, fixed-storage escape rules, and generic copyability. For example, a
function may not return a span view to local fixed-size array storage.

## Trailing `out` Result Binding

Camp functions may use trailing `out` parameters for additional result slots.
When those trailing slots are omitted at a binding site, the compiler supplies
caller storage and binds the produced values.

```camp
void getBounds(out int width, out int height);

auto (width, height) = getBounds();
```

This is equivalent in shape to:

```camp
int width;
int height;
getBounds(out width, out height);
```

A single omitted trailing `out` value can bind as a single expression:

```camp
void getCount(out int count);

auto count = getCount();
```

This feature is not a general tuple system. Omitted `out` results can be bound
immediately, selected as expanded result components where the language defines
that form, or written explicitly with `out`. When a result needs to travel as
one durable value, use a named struct.

## `yield`

`yield` produces one value from a generator body.

```camp
struct iter int countUp(int limit)
{
	for (int value = 0; value < limit; value++)
		yield value;
}
```

The yielded expression must be compatible with the generator's single yielded
type. An iterator cannot yield multiple ordinary values; use a named struct
when one logical item contains several fields.

`yield` is valid only in a generator body declared with `struct iter` or
`class iter`. A plain function returning `iter T` returns an iterator value; it
does not become a generator merely because of its return type.

If the iterator type includes `thrown E`, the generator may `throw` compatible
errors during iteration. The error is part of the iterator protocol, not the
generator's parameter list.

## `foreach`

`foreach` consumes arrays and iterator-compatible values.

```camp
foreach (int value in values)
	sum += value;
```

```camp
foreach (auto line in reader.iterateLines(buffer))
{
	if (line.length == 0)
		continue;
	Console.writeLine(line);
}
```

The loop variable is scoped to the loop body. `break` exits the loop and runs
the iterator cleanup path. `continue` advances to the next iteration.

For arrays, `foreach` enumerates elements in order. In generic code, iterating
over `T[]` under `T: any` requires the size capability needed to compute the
element stride. Copying yielded generic values requires `T: copyable`.

For iterators, `foreach` drives the iterator's `next(...)` protocol and runs
deterministic cleanup. A failing iterator with `thrown E` participates in
ordinary thrown-slot flow. If the loop body does not catch the error and the
enclosing function does not have a compatible thrown result, the compiler
reports an error.

Iterator details are covered in
[Iterators, `foreach`, And Generators](18-iterators-foreach-and-generators.md).

## `try`, `catch`, And `finally`

Camp error handling is value-based. `try` protects a block, `catch` handles
compatible thrown values, and `finally` runs cleanup.

```camp
try
{
	readFile(path, out data);
}
catch (IoError error)
{
	report(error);
}
finally
{
	delete data.elements;
}
```

The `catch` variable is an ordinary local for the catch body. A catch body can
return, throw another error, or continue normal control flow.

`finally` is for cleanup that must run when control leaves the protected block.
Expression-level cleanup is often more compact for single owned values:

```camp
Buffer* buffer = new Buffer(1024) finally delete;

IoError error;
FileHandle handle = FileHandle.open(
	path,
	FileAccess.READ,
	FileMode.OPEN_EXISTING,
	catch error) finally close();
```

Error and cleanup details are covered in
[Errors, Thrown, And Cleanup](15-errors-thrown-and-cleanup.md).

## `throw`

`throw` writes a compatible error value to the active thrown destination and
exits the current path.

```camp
if (path.length == 0)
	throw E_EMPTY_PATH;
```

The current function, generator, async function, or call-site `try`/`catch`
context must provide a compatible thrown surface. Otherwise the compiler
reports an unhandled thrown value.

## `delete`

`delete` runs the appropriate destruction/deallocation operation for its
operand.

```camp
delete buffer;
delete document;
```

For a class pointer, `delete` runs class destruction and deallocation according
to the allocation path. For struct values, destruction is in-place. For a
`newtype` value, `delete` does not mean "close the wrapped resource"; use an
ordinary cleanup method such as `close()` with `finally close()` when that is
the API contract.

Allocator and lifecycle details are covered in
[Structs, Classes, And Lifecycle](11-structs-classes-and-lifecycle.md) and
[Lifetimes, Allocation, And `within`](16-lifetimes-allocation-and-within.md).

## `within` Statements

A `within` statement establishes an allocation context for the body.

```camp
within (allocator)
{
	string copy = text.copyString();
	Buffer* buffer = new Buffer(1024);
}
```

Only operations that use a `within allocator` parameter are affected. Existing
values do not gain a longer lifetime, and scoped references do not become
escaped merely because allocation happened in a wider context.

## Async Control Flow

An async function body may use `await` and may suspend at await sites.

```camp
async int loadCount(const char[] path, thrown IoError)
{
	byte[] bytes = await readAllAsync(path, catch error);
	return parseCount(bytes, catch error);
}
```

Locals that are live across suspension become part of the async frame. Values
retained across suspension must satisfy the ordinary lifetime rules for the
frame that stores them.

Async details are covered in
[Async, Await, And Postpone](19-async-await-and-postpone.md).

## Statement Conditions

Conditions may contain declarations where the grammar permits them. Such
declarations are scoped to the statement form and are analyzed as part of body
flow and lifetime checking.

Regardless of spelling, the condition's final controlling value must be `bool`.

## Discards

`_` discards a produced value intentionally.

```camp
tryParse(text, out _);
load(path, catch _);
_ = expensiveCall();
```

The discard can receive a value but cannot be read. Use it when ignoring a
result is part of the design, not as a substitute for handling important error
or ownership information.
