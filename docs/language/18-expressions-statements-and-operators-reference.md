+++
nav_title = "18. Expressions Reference"
+++

# Expressions, Statements, And Operators Reference

This is the practical lookup chapter for Camp's executable surface. It gathers
the syntax you reach for while writing code: expressions, operators, calls,
construction, cleanup, conditions, loops, `foreach`, `try`, `catch`, `finally`,
`await`, `postpone`, and the smaller forms that are easy to forget until you
need them.

This chapter is not a second tour of the language. Earlier chapters explain why
the features exist. Here, the goal is quick, complete recall.

## Expression Typing And Target Typing

Every expression has a static type after binding.

| Form | Typing rule |
|---|---|
| Literal with an inherent type | Uses the literal's type, then converts if allowed |
| `default` | Requires a target type |
| `null` | Requires a pointer-like or callable-compatible target |
| String literal | Targeted as `string`, `wstring`, `astring`, character pointer, counted text, or fixed character storage |
| Interpolated string | Constant text when all holes are constant text; otherwise eager UTF-8 text targeted as `string`, `char[]`, `const char[]`, or fixed `char[N]` storage |
| Array literal `[a, b]` | Requires or infers an array element type |
| Initializer list `{ ... }` | Requires a target aggregate, fixed array, optional, or other initializer-compatible type |
| Lambda | Requires or infers a callable target |
| Method reference | Has the callable type or callable newtype implied by the referenced method |
| `auto` local initializer | Infers one fixed static type |

```camp
string title = "Report";
const char[] label = "Ready";
int? maybeCount = default;
auto values = [1, 2, 3];
```

`auto` does not make a local dynamic. The inferred type is fixed after the
initializer is analyzed.

An `auto` declaration needs an initializer with an inferable type, and it takes
that expression's natural type: `5` is `int`, `5u` is `uint`, and a conditional
or array literal is the common type of its parts. Write an explicit type when
there is no initializer, when the initializer is a call that returns nothing, or
when the value supplies no type of its own: `null`, `default`, a brace list, an
empty `[]`, and conditionals or array literals made only of those. `auto` takes
no modifiers, so `const auto limit = 5;` is an error; write `const int limit = 5;`.

```camp
auto count = 5;            // int
auto mask = 1u << 3;       // uint
auto weights = [1, 2.5];   // double[]
// auto nothing = null;    // ERROR: null has no type of its own
// auto empty = [];        // ERROR: no element type
```

`out auto name` and `catch auto name` declare a local of the type the callee
gives that slot, so `catch auto fault` is a `Fault` when the callee declares
`thrown Fault`.

## Literals

| Literal form | Notes |
|---|---|
| `0`, `42`, `0xFF` | Integer literals are checked against the target type where needed; without a target they are `int`, or `long` when the value needs it |
| `42u`, `42l`, `42ul` | Suffixed integer literals have the fixed type `uint`, `long` and `ulong` |
| `1.5` | Floating literal; `double` unless the target is `float` |
| `1.5f`, `2d` | Suffixed floating literals have the fixed type `float` and `double` |
| `true`, `false` | `bool` |
| `'x'`, `'é'`, `'😀'` | Character literal, inferred or target-typed as a compatible character/code type |
| `"ready"` | String literal, target-driven |
| `null` | Null pointer/callable-like value where a compatible target exists |
| `default` | Default value for the target type |
| `[1, 2, 3]` | Array literal |
| `{ .x = 1, .y = 2 }` | Named initializer list |
| `{ 1, 2, 3 }` | Positional initializer list |

Initializer entries are either named or positional. Do not mix both in one
initializer. Everyday Types explains the suffixes and the implicit conversions
between numeric types.

```camp
Point origin = { .x = 0, .y = 0 };
fixed byte[4] signature = [0x43, 0x41, 0x4D, 0x50];
```

## Literal Expressions And Constants

An unsuffixed literal, a constant built only from unsuffixed literals, and a
conditional whose two arms are such constants all adapt to where they are used.
They go to any integer type that holds every value they can produce, and to
`float` and `double`, so none of these needs a cast or a suffix:

```camp
uint budget = 256 * 1024 * 1024;
nuint blockSize = 64 * 1024;
byte tier = ready ? 1 : 33;
float weight = 0.1 + 0.2;
bool belowLimit = used < (ready ? 100 : 50);   // used is a uint
uint total = 2 + count + (ready ? 1 : 0);      // count is a uint
```

A value that does not fit is reported at the value: `byte tier = ready ? 1 : 300;`
is an error at `300`.

What a target never does is change the type an operation is computed in. Camp
lowers to C and each operation keeps the type its operands give it. Constant
operations are computed in `int` (or `long` once an operand is `long`) and are
errors when they overflow, even if the target could hold the true result:

```camp
uint mask = 1u << 31;        // valid: 1u is a uint
// uint mask = 1 << 31;      // ERROR: overflows int
// long far = 1 << 40;       // ERROR: shift count too large for int
long far = 1L << 40;         // valid
```

The same applies to run-time arithmetic. `1 << index` is an `int`, so write
`1u << index` to get a `uint`, and add a suffix when a conditional of constants
feeds a sum: `(ready ? 1u : 5) + (full ? 10 : 20)` is a `uint`, while the sum of
two unsuffixed conditionals is an `int`. A suffixed literal keeps its type, so
`byte tier = ready ? 1u : 33u;` is an error: write `ready ? 1 : 33`.

An `inline` constant has the type you declare. Using it is the same as writing
`(T)value`, so it does not adapt: with `inline int PAGE_SIZE = 4096;`, the line
`nuint bytes = PAGE_SIZE * 4;` is an error, while `long bytes = PAGE_SIZE * 4;`
and `nuint bytes = (nuint)PAGE_SIZE * 4;` are valid.

## Operator Precedence

From tightest to loosest:

| Level | Forms |
|---|---|
| Postfix | calls `()`, indexing `[]`, slicing `[..]`, member `.`, postfix `++`, postfix `--`, generic postfix where applicable |
| Prefix | unary `+`, unary `-`, `!`, `~`, address-of `&`, dereference `*`, prefix `++`, prefix `--`, `await`, `postpone`, casts |
| Multiplicative | `*`, `/`, `%` |
| Additive | `+`, `-` |
| Shift | `<<`, `>>` |
| Relational | `<`, `<=`, `>`, `>=` |
| Equality | `==`, `!=` |
| Bitwise AND | `&` |
| Bitwise XOR | `^` |
| Bitwise OR | <code>&#124;</code> |
| Logical AND | `&&` |
| Logical OR | <code>&#124;&#124;</code> |
| Conditional | `?:` |
| Assignment | `=`, `+=`, `-=`, `*=`, `/=`, `%=`, `&=`, <code>&#124;=</code>, `^=`, `<<=`, `>>=` |

Use parentheses when mixing forms that are technically clear but visually busy:

```camp
bool selected = (flags & FLAG_SELECTED) != 0;
int scaled = (left + right) * factor;
```

The `^` token is also used in index and range contexts for from-end positions,
as in `values[^1]` or `values[2..^1]`. That is slice/index syntax, not the
bitwise XOR operator.

## Arithmetic, Bitwise, And Boolean Operators

| Family | Operators | Result |
|---|---|---|
| Arithmetic | `+`, `-`, `*`, `/`, `%` | Numeric result |
| Comparison | `<`, `<=`, `>`, `>=`, `==`, `!=` | `bool` |
| Logical | `!`, `&&`, <code>&#124;&#124;</code> | `bool` |
| Bitwise | `~`, `&`, <code>&#124;</code>, `^`, `<<`, `>>` | Integral result |
| Conditional | `condition ? whenTrue : whenFalse` | The target type, or the common type of both arms |

Use interpolated strings when you want to build text from literal pieces and
values:

```camp
Console.writeLine($"left={left}, right={right}");

string message = $"total: {total}";
```

Runtime interpolation produces text right away. With `auto`, the type is
`string`. When the target is a fixed or counted character array, the same
interpolation initializes that storage:

```camp
auto inferred = $"total: {total}";      // string
char[] chars = $"total: {total}";
fixed char[16] storage = $"total";
```

Without `new` or `stackalloc`, runtime interpolation uses compiler-selected
scoped storage. Use `stackalloc $"..."` when you want that storage choice to be
explicit. Use `new $"..."` when the produced text needs heap lifetime:

```camp
char[] scratch = stackalloc $"total: {total}";
string copy = within(default) new $"total: {total}" finally delete;
```

Constant interpolation remains ordinary text:

```camp
auto literal = $"ready"; // string
```

Textual `+` is not string concatenation in Camp. Write an interpolated string
instead:

```camp
Console.writeLine($"total: {total}");
```

`+=` is still numeric addition assignment. It does not append text.

Conditions must be `bool`. Integers, pointers, enums, and newtypes do not
implicitly become truth values.

```camp
if (count != 0)
	process(values);

if (buffer != null)
	buffer.clear();
```

```camp
if (count)   // ERROR
	process(values);
```

## Mixed Types In Conditionals And Array Literals

With a target type, each arm of a conditional and each element of an array
literal converts to it on its own, so `long total = ready ? count : bigCount;`
and `float[] weights = [1, 2.5];` just work. With no target, as under `auto` or
as an operand, Camp looks for one type that every part converts to. That is the
common type:

```camp
auto wider = ready ? count : bigCount;   // long, from an int and a long
auto weights = [1, 2.5];                 // double[]
auto counts = [small, 2];                // uint[]: the literal adapts
auto rows = [[1, 2], [3]];               // int*[]: each inner literal is a pointer to its elements
```

The result is always one of the types that appear, and it does not depend on the
order. `null`, `default` and `[]` take their type from the other parts, so
`ready ? buffer : null` has the type of `buffer`; a part that is only those gives
nothing to infer from. When no type works, as in `ready ? small : count` over a
`uint` and an `int`, or `[1, 2L, true]`, the compiler reports the first part that
does not convert to the widest type. Add a cast to say which you mean.

Pointers join to the more general pointer: `int*` with `const int*` is
`const int*`, and `Derived*` with `Base*` is `Base*`. Their lifetimes must agree
too. A `scoped` pointer and an `escaped` one do not join; write a lifetime cast
on one of them.

## Assignment And Update

The left side of an assignment must be writable.

| Form | Meaning |
|---|---|
| `target = value` | Assign converted value |
| `target += value` | Add then assign for numeric values |
| `target -= value` | Subtract then assign |
| `target *= value` | Multiply then assign |
| `target /= value` | Divide then assign |
| `target %= value` | Remainder then assign |
| `target &= value` | Bitwise AND then assign |
| <code>target &#124;= value</code> | Bitwise OR then assign |
| `target ^= value` | Bitwise XOR then assign |
| `target <<= value` | Shift left then assign |
| `target >>= value` | Shift right then assign |
| `target++`, `++target` | Increment writable numeric location |
| `target--`, `--target` | Decrement writable numeric location |

```camp
index++;
flags |= FLAG_VISIBLE;
items[position] = nextItem;
```

Writable locations include mutable locals, mutable fields, writable array
elements, pointer targets, and setter-backed properties or indexers. `const`
views, getter-only properties, and read-only receiver views are not writable.

## Names And Member Access

| Surface | Meaning |
|---|---|
| `name` | Resolve a local, parameter, member, type, namespace, generic parameter, or imported declaration |
| `Namespace::Name` | Qualified source name |
| `value.member` | Field, method, property, expanded component, or static member access |
| `Type.member` | Static member access |
| `pointer.member` | Pointer receiver member access; Camp does not use `->` |
| `this.member` | Current receiver member |
| `base(...)` | Base constructor call in a class constructor |

```camp
Std::Console.writeLine("ready");
window.resize(800, 600);
windowPointer.resize(800, 600);
```

Member access uses `.` for values and pointers. This is source syntax only; it
does not erase the difference between a value and a pointer.

Compiler-expanded values expose components through member access:

```camp
nuint length = data.length;
byte* elements = data.elements;

if (maybeValue.specified)
	process(maybeValue.value);

auto callTarget = callback.call;
auto callContext = callback.context;
```

## Property And Indexer Rewrites

Property syntax rewrites to eligible methods.

| Surface | Rewritten form |
|---|---|
| `value.Name` | `value.getName()` |
| `value.Name = next` | `value.setName(next)` |
| `value.Item[index]` | `value.getItem(index)` |
| `value.Item[index] = next` | `value.setItem(index, next)` |
| `await value.Result` | `await value.getResultAsync()` |

After rewriting, the expression is analyzed as the method call. `thrown`,
`await`, generic arguments, visibility, lifetimes, and overload resolution all
follow the method's rules.

Iterator-returning getters are not property-like; call those methods
explicitly.

## Indexing, From-End Indexes, And Ranges

| Surface | Meaning |
|---|---|
| `values[index]` | Element/indexer access |
| `values[^1]` | From-end index, when the target has a visible length |
| `values[start..end]` | Range boundary form |
| `values[start..]` | Range from start to length |
| `values[..end]` | Range from zero to end |
| `values[..]` | Whole range |
| `method(start, count)` | Direct index/count call |
| `method(start..end)` | Boundary form for an `@range` pair |

`@index` enables index-aware behavior such as from-end indexes. `@range` marks
the first parameter in an `index, count` pair and enables range boundary syntax.

Direct comma form and boundary form are different:

```camp
text.slice(5, 2);   // index 5, count 2
text.slice(5..7);   // boundary 5 through boundary 7
```

Boundary form clamps boundaries before computing `count`. Direct form passes
the explicit `index, count` values.

## Calls And Arguments

| Call form | Notes |
|---|---|
| `target()` | Ordinary call |
| `target(a, b)` | Positional arguments |
| `target(name: value)` | Named arguments |
| `target(a, name: value)` | Positional and named arguments may be mixed |
| `target(out value)` | Caller-provided success result slot |
| `target(catch error)` | Caller-provided thrown/error slot |
| `target<T>(value)` | Explicit generic argument |
| `target(value)` | Generic arguments may be inferred when possible |
| `target(sizeof(T))` | Explicit generic size capability |
| `target(vtableof(T: I))` | Explicit typed witness capability for `T` implementing `I` |

No ordinary parameter or expanded component may be supplied more than once.
Default arguments are inserted from the static signature being called.

```camp
connect(host, port: 443, useTls: true);
file.read(buffer, out bytesRead, catch error);
copy = within (allocator) values.copyArray();
```

Overload selector parameters participate in Camp's intentionally limited
overload resolution:

```camp
void write(CharWriter this, overload const char[] text);
void write(CharWriter this, overload int value);

writer.write("ready");
writer.write(42);
```

The `overload` keyword belongs to the declaration. The call site uses the
ordinary argument expression, and the selector keeps the overload family small
and ABI-visible.

## Method References And Callable Invocation

A method name without a call refers to the method:

```camp
delegate void(const char[]) writeLine = Console.writeLine;
writeLine("ready");
```

Bound method references carry their receiver in the callable context:

```camp
delegate void(const char[]) write = Console.write;
write("ready");
```

Callable newtype ascription can give the reference a nominal callable type:

```camp
newtype delegate void LineWriter(const char[] text);

void accept(LineWriter writer)
{
	writer("ready");
}
```

Callable values invoke with the same `target(arguments)` surface as functions.
An interpolation hole can use a `toString` method with a `prep char[]` buffer.
This keeps formatting options as ordinary arguments:

```camp
Console.writeLine($"count: {count.toString(IntegerFormat.POSITIVE_SIGN)}");
```

## Prepared Allocation

Calling a prep-bearing function without supplying its prep buffer produces a
scoped prepared array. Put `(new)` directly before that call when the result
needs allocated lifetime. Capture the owning result or attach cleanup directly:

```camp
char[] text = within(default) (new) count.toString();
// ...
delete text.elements;

within(default) (new) count.toString() finally delete;
```

The `(new)` form owns the direct prepared result. Apply member, index, or slice
operations only after capturing it so the owning reference is not lost.

## Lambdas

Lambdas are callable expressions. They are target-typed by a delegate, once,
iterator, async, or other compatible callable context.

```camp
delegate bool(in ImageInfo info) keepLarge = info => info.width >= 1024;

escaped delegate void(TimerHandle handle) tick = within(default) new delegate handle => {
	stopTimer(handle);
};
```

Captures are inferred from the lambda body and must satisfy the lifetime of the
callable context. Escaped callables need escaped-safe captures.

## Casts And Conversion Boundaries

| Cast form | Use |
|---|---|
| `(T)value` | Ordinary explicit conversion |
| `(unsafe T)value` | Explicit unsafe conversion across an unproven boundary |
| `(escaped T)value` | Lifetime cast to escaped form where allowed |
| `(scoped T)value` | Lifetime cast to scoped form where allowed |
| `(unscoped(anchor) T)value` | Lifetime relation cast where allowed |
| `(constof(anchor) T)value` | Dependent constness relation where allowed |

```camp
FileHandle handle = (FileHandle)nativeHandle;
void* raw = (unsafe void*)callback.context;
escaped byte* retained = (escaped byte*)buffer;
```

Conversions do not tunnel through arrays, optionals, delegates, generic
arguments, or callable signatures. Reconstruct the outer value when an inner
component changes type.

An optional's own conversion is its payload's conversion, not a tunnel: `T?`
converts to `U?` exactly when `T` converts to `U`, implicitly or by the same
cast, converting `.value` and carrying `.specified` unchanged. Nothing converts
deeper than that payload conversion, so `int[]?` does not become `long[]?`.

```camp
int? count = { 300, true };
long? wide = count;            // implicit, as int converts to long
byte? narrow = (byte?)count;   // explicit, as (byte)300; still specified
```

## Construction, Initialization, And Cleanup Expressions

| Form | Meaning |
|---|---|
| `Type(args)` | Construct in storage selected by the destination |
| `stackalloc Type(args)` | Allocate activation-stack storage, then construct |
| `new Type(args)` | Allocate storage, usually heap-like, then construct |
| `within (allocator) expression` | Evaluate allocation-aware expression in allocator context |
| `within(default) expression` | Use the default allocation path for the wrapped expression |
| `delete value` | Destroy/deallocate according to the operand and allocation path |
| `expression finally cleanup` | Register cleanup for the surrounding scope |
| `Type(args) { .field = value }` | Construct, then apply trailing initializer as one initialization operation |
| `new Type(args) { .field = value }` | Allocate and construct, then apply trailing initializer |

```camp
Buffer local = Buffer(1024) finally delete;
Buffer* scratch = stackalloc Buffer(1024) finally delete;
Buffer* heap = within (allocator) new Buffer(1024) finally delete;

HttpRequest request = HttpRequest("GET", url)
{
	.timeoutMs = 5000,
};
```

Constructor-shaped calls create the value in storage that is already present
for the local, field, or destination. In ordinary local code, that usually
means storage with the current block's lifetime. `stackalloc` explicitly
selects activation-stack storage. `new` allocates storage before construction;
in ordinary code that usually means heap-like storage through the current
allocator path.

## Special Expressions

| Form | Meaning |
|---|---|
| `sizeof(T)` | Size capability or concrete type size |
| `typenameof(T)` | Source-level type name as `string` |
| `vtableof(T: Interface)` | Typed witness for a concrete/generic target and interface; ordinary slots take `T*` |
| `symbolof(Name)` | Metadata-only symbol reference inside attribute arguments |
| `await expression` | Await an async/callback-shaped operation |
| `postpone call` | Capture a call for later invocation |
| `throw value` | Produce a value for the active `thrown` destination |
| `this` | Current receiver |
| `classtype` | Limited class-relative type form in allowed class member positions |
| `default` | Target-typed default value |
| `null` | Target-typed null value |

`symbolof(...)` is not a runtime expression. It is valid only inside metadata
attribute arguments.

```camp
void writeType<T>(typenameof(T))
{
	Console.writeLine(typenameof(T));
}

void clear<T>(T[] values, sizeof(T));
void drawAll<T>(T[] values, sizeof(T), vtableof(T: Drawable));
```

## Blocks, Locals, And Fixed Storage

| Statement | Notes |
|---|---|
| `{ ... }` | Block scope |
| `;` | Empty statement |
| `Type name;` | Local declaration |
| `Type name = value;` | Local declaration with initializer |
| `auto name = value;` | Inferred local declaration |
| `fixed T[n] name;` | Inline fixed storage |
| `expression;` | Expression statement |
| `_ = expression;` | Intentional discard |

```camp
{
	fixed byte[256] scratch;
	byte[] view = scratch;
	_ = fill(view);
}
```

Fixed storage owns inline storage. A span view over local fixed storage must not
escape that storage's lifetime.

`_` is write-only. It may be assigned to, used as an `out _` slot, or used as a
`catch _` slot, but it cannot be read or declared as an ordinary name.

## Conditions

The controlling value must be `bool` in:

| Form |
|---|
| `if (condition)` |
| `while (condition)` |
| `do ... while (condition)` |
| `for (...; condition; ...)` |
| `condition ? left : right` |
| `left && right` |
| <code>left &#124;&#124; right</code> |

Some statement forms allow declarations in the condition where the grammar
permits them, but the final controlling value is still `bool`.

## Control-Flow Statements

| Statement | Notes |
|---|---|
| `if (condition) statement` | Optional `else` |
| `while (condition) statement` | Pre-test loop |
| `do statement while (condition);` | Post-test loop |
| `for (init; condition; next) statement` | Init may declare a local or evaluate an expression |
| `switch (value) { ... }` | `case` and optional `default` labels |
| `break;` | Exit nearest loop or switch |
| `continue;` | Advance nearest loop |
| `label:` | Label inside current function body |
| `goto label;` | Jump to a visible label in the same function body |
| `return;` | Return from `void` function or early exit where valid |
| `return value;` | Return a value compatible with the function result |

```camp
for (nuint index = 0; index < values.length; index++)
{
	if (values[index] == match)
		return index;
}
```

`break`, `continue`, `return`, and `throw` participate in ordinary cleanup flow.
Pending `finally` cleanup for scopes being exited runs as structured control
leaves.

`goto` is intentionally low-level. Unlike structured exits, it may jump out of
blocks without running skipped `finally` work. Use structured control flow when
cleanup and lifetime reasoning matter.

## `switch`, `case`, And `default`

```camp
switch (mode)
{
	case READ:
		openReader();
		break;
	case WRITE:
		openWriter();
		break;
	default:
		throw E_INVALID_MODE;
}
```

`break` exits the switch. Prefer an explicit `break`, `return`, or `throw` at
the end of each case unless fallthrough is deliberate and clear.

Enum values usually do not need qualification when the target enum type is
known:

```camp
FileMode mode = OPEN_EXISTING;
```

Qualify when the target type is not known or when it improves clarity:

```camp
open(path, access, FileMode.OPEN_EXISTING);
```

## `foreach` And `yield`

| Form | Notes |
|---|---|
| `foreach (T value in source) statement` | Iterate array or iterator-compatible source |
| `foreach (auto value in source) statement` | Infer loop variable type |
| `yield value;` | Produce one value from a generator body |

```camp
foreach (auto item in values)
	total += item;
```

For arrays, iteration proceeds in index order. For iterators, `foreach` drives
the iterator protocol and runs iterator cleanup on normal exit, `break`,
`return`, and `throw`.

`yield` is valid only in a `struct iter` or `class iter` generator body. An
iterator yields one ordinary value at a time; use a named struct when each item
contains multiple fields.

## Error Handling And Cleanup Statements

| Statement/form | Notes |
|---|---|
| `try { ... } catch (E error) { ... }` | Handle compatible `thrown` value |
| `try { ... } finally { ... }` | Cleanup on exit from protected block |
| `catch (E error)` | Catch variable is an ordinary local in the catch body |
| `finally statement;` | Register statement-level cleanup where allowed |
| `expression finally cleanup` | Register expression-level cleanup |
| `throw value;` | Write compatible thrown value and exit current path |
| `delete value;` | Destroy/deallocate operand |
| `delete delegate;` | Delete generated context inside a valid `new delegate` lambda |
| `delete callback;` | Delete the context of an escaped delegate value, like `delete array;` |

```camp
try
{
	FileHandle file = FileHandle.open(path, READ, OPEN_EXISTING)
		finally close();
	process(file);
}
catch (IoError error)
{
	reportOpenFailure(error);
}
```

`catch` and `out` are both caller-provided storage patterns, but they are not
the same. `catch` handles error flow. `out` is ordinary success data.

`delete delegate` is not a general delete operand. It is valid only inside a
`new delegate` lambda, where it refers to the generated delegate context. In a
void-returning lambda it may be the final statement; in a non-void lambda, use
`finally delete delegate` as the final cleanup registration. To release the
context of an escaped delegate value elsewhere, use `delete callback;`.

## `within` Statements

```camp
within (allocator)
{
	char[] copy = (new) text.toString();
	Buffer* buffer = new Buffer(1024) finally delete;
}
```

A `within` statement establishes the allocation context for operations in the
body that use allocator context. It does not extend the lifetime of existing
values and does not make scoped data escaped.

## Async And Deferred Call Forms

| Form | Notes |
|---|---|
| `await call` | Valid in async bodies; waits for awaitable async/callback result |
| `postpone call` | Captures a call and its supplied argument slots for later |
| `@noawait async ...` | Async body contract: no suspension |
| `@awaitwith parameter` | Selects the resumer for a suspending async body |

```camp
int bytes = await BackgroundFiles.countBytesAsync(path, catch error);
auto later = postpone Console.writeLine("ready");
```

`await` is an expression operator. `postpone` captures a call shape; it is not a
general closure literal.

## Quick Statement Table

| Category | Forms |
|---|---|
| Scope | block, local declaration, fixed storage, empty statement |
| Selection | `if`, `else`, `switch`, `case`, `default`, `?:` |
| Loops | `while`, `do`, `for`, `foreach` |
| Loop exits | `break`, `continue` |
| Function exits | `return`, `throw` |
| Generator output | `yield` |
| Errors | `try`, `catch`, `throw`, `catch` arguments |
| Cleanup | `finally`, expression-level `finally`, `delete` |
| Allocation context | `within` statement, `within` expression, `within` argument |
| Low-level jump | label, `goto` |
| Discard | `_` |

## Quick Expression Table

| Category | Forms |
|---|---|
| Values | literals, `default`, `null`, `this` |
| Names | unqualified names, `::` qualified names |
| Access | `.`, `[]`, property/indexer rewrites, expanded components |
| Calls | ordinary calls, named arguments, defaults, `out`, `catch`, `within` |
| Construction | constructor-shaped type calls, `stackalloc`, `new`, initializer lists, trailing initializers |
| Conversion | casts, unsafe casts, lifetime casts |
| Operators | arithmetic, comparison, logical, bitwise, assignment, update |
| Capabilities | `sizeof`, `typenameof`, `vtableof` |
| Metadata-only | `symbolof` |
| Async/deferred | `await`, `postpone` |
| Error flow | `throw` |
