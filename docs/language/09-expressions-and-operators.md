# Expressions And Operators

Expressions are the typed, evaluable parts of Camp source. They include
literals, names, member access, calls, indexing, casts, construction, arrays,
initializer lists, lambdas, unary and binary operators, conditionals, ranges,
`await`, `postpone`, `throw`, `within` expressions, and special capability
expressions such as `sizeof`, `typenameof`, and `vtableof`.

This chapter describes expression behavior that matters when writing Camp code.
Feature-specific chapters give deeper treatment for arrays, lifetimes,
interfaces, async, and generics.

## Expression Typing

Every expression has a static type after binding. Camp does not defer
expression typing to runtime and does not use dynamic dispatch except where a
source feature explicitly says so, such as virtual class methods or interface
calls.

Some expressions are self-typed:

```camp
int count = 4;
bool ready = count > 0;
string title = "Report";
```

Some expressions are target-typed by their destination:

```camp
int? optionalCount = default;
const char[] text = "ready";
Position origin = { .row = 0, .column = 0 };
```

If an expression needs a target type and none is available, the compiler reports
an error. Common target-typed expressions include `default`, string literals,
array literals, initializer lists, omitted trailing `out` result binding, and
lambdas.

## Evaluation Model

Camp expressions are ordinary imperative expressions. A subexpression that
performs a call, assignment, allocation, cleanup registration, `await`, or
`throw` has the source-visible effect described by that feature.

The important practical rules are:

- member lookup and overload resolution happen statically;
- conversions happen only at value boundaries;
- expanded forms are reconstructed only where the language defines that
  reconstruction;
- `out` and `catch` arguments are caller-provided storage, not hidden return
  tuples;
- `finally` cleanup attached to an expression belongs to the surrounding
  cleanup flow.

When an expression performs several operations that all matter for correctness,
prefer a few named locals. This is especially useful around `await`, `catch`,
`finally`, and pointer/lifetime casts.

## Operator Precedence

Camp follows C-family precedence for ordinary operators. From tightest to
loosest:

| Level | Operators and forms |
|---|---|
| postfix | call `()`, index `[]`, member `.`, property/indexer rewrite |
| prefix | unary `+`, `-`, `!`, `~`, address/cast-like prefix forms, `await`, `postpone` |
| multiplicative | `*`, `/`, `%` |
| additive | `+`, `-` |
| shift | `<<`, `>>` |
| relational | `<`, `<=`, `>`, `>=` |
| equality | `==`, `!=` |
| bitwise and | `&` |
| bitwise xor | `^` |
| bitwise or | `|` |
| null coalescing | `??` |
| logical and | `&&` |
| logical or | `||` |
| conditional | `?:` |
| assignment | `=`, compound assignments |

Use parentheses where intent would otherwise depend on a reader remembering
the table:

```camp
int total = (left + right) * scale;
bool eligible = (account != null) && (account.Balance > 0);
```

## Boolean Operators

Conditions are explicit. The condition of `if`, `while`, `do`, `for`, `?:`,
`&&`, and `||` must be `bool`.

```camp
if (count > 0)
	process(values);

if (buffer != null)
	buffer.clear();
```

Integers, pointers, enums, and newtypes do not implicitly become truth values:

```camp
if (count)       // ERROR
	process(values);

if (buffer)      // ERROR
	buffer.clear();
```

Write the comparison that expresses the intended condition.

## Arithmetic And Bitwise Operators

Arithmetic operators apply to numeric operands. Bitwise operators apply to
integral operands. Comparison operators produce `bool`.

```camp
int area = width * height;
uint masked = flags & FLAG_VISIBLE;
bool fits = area <= maxArea;
```

Inline constants and enum values may use constant arithmetic when the operands
are compile-time constants. Overflow or a value that cannot fit the target type
is an error for contexts that require a checked constant value, such as enum
members and inline constants.

## Null Coalescing

`left ?? right` evaluates to `left` when `left` is specified/non-null according
to the left operand's nullable shape; otherwise it evaluates to `right`.

```camp
HttpClientOptions options = suppliedOptions ?? HttpClientOptions();
```

Both operands must be compatible with the result type. The operator is most
useful with optional values and pointer-like nullable values. It is not a
general exception or error-handling construct; `thrown` results are handled
with `catch`, `try`, and propagation.

## Assignment

The left side of an assignment must be a writable location. Writable locations
include mutable locals, mutable fields, writable array elements, writable
pointer targets, and property/indexer setter surfaces.

```camp
position.row = 4;
values[index] = 10;
settings.Theme = "light";
```

Assignments obey ordinary conversion rules. The right-hand expression must be
assignable to the left-hand destination type.

`const` views, `constof(...)` views derived from const anchors, read-only
newtype receivers, and getter-only properties are not writable.

Compound assignments are checked as assignment plus the corresponding operator.

```camp
index += 1;
flags |= FLAG_SELECTED;
```

## Names And Member Access

A bare name resolves through local scope, parameters, receiver members, type
members, imports, visible namespace declarations, generic parameters, and
lifetime anchors.

Member access is always written with `.`. Camp does not use C's `->`.

```camp
point.x
window.resize(800, 600)
windowPtr.resize(800, 600)
buffer.length
maybeValue.specified
```

The same surface works for values, pointers, class instances, class pointers,
expanded values, and static members. This is source sugar over the correct
underlying receiver shape; it does not erase the distinction between values and
pointers.

Static members are accessed through the type name:

```camp
Console.writeLine("ready");
FileHandle.open(path, FileAccess.READ, FileMode.OPEN_EXISTING);
```

Canonical flattened symbols may also be visible where the declaration is
visible:

```camp
Console_writeLine("ready");
```

## Expanded Components

Compiler-expanded values expose named components:

```camp
byte[] buffer;
int? maybeCount;
delegate void() callback;

nuint length = buffer.length;
bool present = maybeCount.specified;
auto callTarget = callback.call;
auto callContext = callback.context;
```

These components are source-visible because they affect ordinary programming
decisions. For a binding named `buffer`, the ABI component names are shaped like
`buffer` and `buffer_length`. For an optional named `maybeCount`, they are
shaped like `maybeCount` and `maybeCount_specified`.

Do not declare names that collide with compiler-expanded component names in the
same scope.

## Calls

A call uses `target(arguments)`.

```camp
writer.writeLine("ready");
nuint copied = stream.read(buffer);
```

Arguments may be positional or named. Positional and named arguments may be
mixed, but no parameter or expanded component may be supplied more than once.

```camp
connect(host, port: 443, useTls: true);
```

Default arguments are inserted according to the static signature being called.
When a callable value has a callable `newtype`, defaults declared on that
callable `newtype` are used for calls through that value. Defaults declared on
the original function or method are used for direct calls to that declaration.

`out` and `catch` arguments write into caller-provided storage:

```camp
parsePort(text, out port, catch error);
```

When trailing `out` parameters are omitted and immediately bound, the compiler
creates the necessary caller storage. See
[Functions And Callables](08-functions-and-callables.md) and
[Statements And Control Flow](10-statements-and-control-flow.md).

## Overload Selector Parameters

`overload` parameters participate in overload selection while preserving one
source name for a family of operations.

```camp
export void write(CharWriter this, overload int value);
export void write(CharWriter this, overload const char[] value);

writer.write(123);
writer.write("ready");
```

The selector is part of the callable surface used for resolution and generated
symbol naming. It is not a magic dynamic-dispatch argument.

## Method References

A method name used without a call refers to the method itself.

```camp
delegate void(const char[]) logLine = Console.writeLine;
logLine("ready");
```

Bound method references carry their receiver through the callable context. If
the referenced declaration has a compatible callable `newtype` ascription, the
method reference has that nominal callable type.

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

struct Date
{
	nuint format(char[] buffer = default): CharFormatter
	{
		...
	}
}

const Date date = ...;
auto formatter = date.format; // CharFormatter
```

The callable context retains the receiver according to the receiver's lifetime
and constness rules. A method reference that would let a scoped receiver escape
is invalid.

## Property Access

Property syntax rewrites to ordinary method syntax when an eligible accessor is
visible.

| Surface form | Rewritten form |
|---|---|
| `value.Name` | `value.getName()` |
| `value.Name = next` | `value.setName(next)` |
| `value.Item[index]` | `value.getItem(index)` |
| `value.Item[index] = next` | `value.setItem(index, next)` |
| `value.[index]` | `value.get(index)` |
| `value.[index] = next` | `value.set(index, next)` |
| `await value.Result` | `await value.getResultAsync()` |

Property access does not create a separate runtime property concept. The
rewritten method call is analyzed normally for overloads, generics, lifetimes,
`thrown` slots, `await`, and visibility.

A visible ordinary member with the property name wins over a possible accessor
rewrite. Getter-like methods returning iterators should be called explicitly
because iterator values carry cleanup and control-flow consequences.

## Indexing

Indexing uses `target[index]`.

```camp
int first = values[0];
values[1] = 10;
```

Array indexing is bounds-sensitive according to the array API and selected
runtime checks. Index-aware parameters may use attributes such as `@index` so
tooling and lowering can recognize index arguments.

Nameless indexer property syntax uses a dot before the bracket:

```camp
headers.["accept"] = "application/json";
auto accept = headers.["accept"];
```

That form rewrites to `headers.set("accept", value)` or
`headers.get("accept")`.

## Ranges And Slices

Range-like syntax is used by slice-aware APIs. Arrays and counted text expose
slice methods with `@range` parameters.

```camp
auto middle = values[2..^1];
auto tail = text[5..];
```

The slice result is a view unless the called API says it allocates. For arrays,
that means a new pointer-plus-length span over existing storage. For counted
text, indexing and slicing operate on code units, not Unicode scalar values.

Slice behavior is described in
[Arrays, Slices, Optionals, And Strings](14-arrays-slices-optionals-and-strings.md).

## Casts

Type casts use `(T)value`.

```camp
nint index = (nint)position;
UserId id = (UserId)rawId;
```

Unsafe casts use `(unsafe T)value` and are explicit evidence that the program
is crossing a boundary the compiler cannot prove safe:

```camp
fn* rawCall = (unsafe fn*)callback.call;
byte* rawBytes = (unsafe byte*)address;
```

Lifetime casts use the lifetime qualifier forms:

```camp
escaped byte* retained = (escaped byte*)buffer;
scoped const char[] view = (scoped const char[])text;
```

Conversions do not tunnel through arrays, optionals, delegates, generic
arguments, or callable signatures. Reconstruct the outer value when the inner
component needs a cast. See
[Pointers, Qualifiers, And Conversions](07-pointers-qualifiers-and-conversions.md).

## Construction Expressions

`init` constructs a value in storage that already exists. `new` allocates
storage and then constructs.

```camp
Position position = init Position(row: 2, column: 4);
Buffer* buffer = new Buffer(1024);
```

`within(context)` can select the allocation context:

```camp
Buffer* buffer = within (allocator) new Buffer(1024);
```

For classes that implement interfaces, hidden virtual/interface storage is
initialized by the construction path before the user constructor body runs. For
structs and fixed structs, `init` constructs in the final storage and does not
imply heap allocation.

Lifecycle details are covered in
[Structs, Classes, And Lifecycle](11-structs-classes-and-lifecycle.md) and
[Lifetimes, Allocation, And `within`](16-lifetimes-allocation-and-within.md).

## Initializer Lists

Initializer lists use braces and may be positional or named depending on the
target type.

```camp
Position home = { .row = 0, .column = 0 };
int[3] scores = { 10, 20, 30 };
```

Do not mix named and positional entries in the same initializer. Fixed-size
array storage may be initialized from a compatible array literal, compatible
string literal, or `default`; that writes into known fixed storage and does not
copy a fixed array value.

Trailing initializer syntax after construction applies assignments after the
constructor establishes the main value:

```camp
auto request = init HttpRequest("GET", url)
{
	.timeoutMs = 5000,
};
```

The construction call and trailing initializer are one initialization operation
for lifetime checking.

## `sizeof`, `typenameof`, `vtableof`, And `symbolof`

`sizeof(T)` supplies size information. In generic code, it is an explicit
capability parameter when erased lowering needs the size.

```camp
void clear<T: any>(T[] values, sizeof(T));
```

`typenameof(T)` produces the source-level type name as a `string` where the
language provides that capability. In generic code it is likewise explicit:

```camp
void writeType<T: any>(typenameof(T))
{
	Console.writeLine(typenameof(T));
}
```

`vtableof(T: Interface)` supplies the interface vtable capability for generic
interface-constrained code:

```camp
void callAll<T: implements Drawable>(T[] values, vtableof(T: Drawable), sizeof(T));
```

`symbolof(Name)` is valid only inside metadata attribute arguments. It is not a
runtime reflection expression.

Generic capability details are in
[Generics And Type Capabilities](17-generics-and-type-capabilities.md).

## `await`, `postpone`, And `throw`

`await` is an expression-level operator for async calls:

```camp
int value = await loadCountAsync(path, catch error);
```

`postpone` captures a call for later invocation:

```camp
auto delayed = postpone loadCountAsync(path);
```

`throw` produces a value for the nearest compatible `thrown` destination:

```camp
throw E_INVALID_PATH;
```

These forms are not merely ordinary function calls. `await` participates in
async frame lowering, `postpone` captures supplied argument slots, and `throw`
participates in thrown-slot flow. Their detailed rules live in
[Errors, Thrown, And Cleanup](15-errors-thrown-and-cleanup.md) and
[Async, Await, And Postpone](19-async-await-and-postpone.md).

## `within` Expressions

`within` selects an allocation context for the expression it wraps.

```camp
string copy = within (allocator) text.copyString();
Buffer* buffer = within (allocator) new Buffer(1024);
```

The selected context affects operations that use a `within allocator`
parameter. It does not retroactively change the lifetime of existing values and
does not make scoped data escaped.

## Expression-Level Cleanup

An expression can register cleanup with `finally` where the grammar permits it.

```camp
Buffer* buffer = new Buffer(1024) finally delete;
FileHandle handle = FileHandle.open(path, FileAccess.READ, FileMode.OPEN_EXISTING) finally close();
```

The cleanup is tied to the surrounding scope and runs according to the cleanup
rules for that scope. Use expression-level cleanup for values whose ownership
is clear at the binding site. Prefer explicit `try` / `finally` blocks when the
cleanup path needs several steps.

## Target Typing And `auto`

`auto` on a local declaration infers the static type from the initializer:

```camp
auto count = 0;
auto title = "Report";
auto reader = file.CharReader;
```

The inferred type is fixed after declaration. `auto` does not create a dynamic
or variant local.

Use an explicit type when the representation is semantically important:

```camp
string terminated = "Report";
const char[] counted = "Report";
escaped byte* data = getEscapedBuffer();
```

This is especially important for string-vs-array text, nominal `newtype`
boundaries, pointer lifetimes, and expanded/materialized forms.
