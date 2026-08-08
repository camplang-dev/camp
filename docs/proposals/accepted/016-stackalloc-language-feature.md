# Stackalloc Language Feature

## Status

Accepted.

## Proposal Date

2026-08-08

## Last Updated Date

2026-08-08

## Summary

This proposal introduces `stackalloc` as Camp's explicit source-level feature
for dynamic activation-stack allocation, and removes `init` as a language
keyword.

Explicit array allocation becomes possible only through these source forms:

```camp
int[] heap = new int[100];
int[] stack = stackalloc int[100];
fixed int[100] local = [];
```

Construction into storage that already exists or has already been selected by
the surrounding context is written as a constructor-shaped expression:

```camp
Widget value = Widget(args);
slot = Widget(args);
```

The expression `Type(args)` does not allocate. Allocation remains explicit:

```camp
Widget* heap = within(default) new Widget(args);
Widget* stack = stackalloc Widget(args);
```

`stackalloc` is deliberately visible. Dynamic stack allocation is useful and
cheap, but it has lifetime and loop behavior that should not be hidden behind
`auto` or other ordinary-looking source forms.

## Motivation

Camp currently has several paths that can lower to C `alloca` or equivalent
target stack allocation:

- runtime-sized array construction previously written with `init T[n]`;
- prepared results that need compiler-created buffers;
- runtime string interpolation;
- erased generic temporaries;
- generated iterator/current-value storage.

Those allocations are not all equivalent to fixed local storage. Dynamic
activation-stack storage:

- cannot be reclaimed until the current function finishes or suspends;
- can accumulate in an unsuspended loop;
- is invalid after an `await` or `yield` suspension boundary;
- cannot be freed by an allocator;
- must not escape;
- can be required by erased generic values whose size is known only at runtime.

The old use of `init` blurred two different concepts:

```camp
Widget value = init Widget(args); // construction into selected storage
int[] data = init int[n];         // dynamic storage selection
```

This proposal removes that ambiguity:

- `new` selects allocator-backed storage;
- `stackalloc` selects dynamic activation-stack storage;
- `fixed` declares fixed lexical storage;
- `Type(args)` constructs into storage that already exists or has already been
  selected by the surrounding context.

The result is a louder but cleaner storage model, and one fewer construction
keyword.

## Goals

- Add `stackalloc` as the explicit dynamic activation-stack allocation syntax.
- Remove `init` as a language keyword.
- Treat constructor-shaped calls, such as `Rect(0, 0, 10, 10)`, as construction
  into storage selected by the surrounding context when the target resolves to
  a constructible type.
- Parse and bind trailing initializer lists after calls optimistically, then
  diagnose them later if the call is not type construction.
- Preserve array literal materialization for array values, pointer
  initialization, and call arguments as fixed literal storage, not allocation.
- Allow `stackalloc` in the same broad lexical contexts as `new`, subject to
  type eligibility and lifetime rules.
- Allow `stackalloc` to allocate constructed struct/class instances and return
  scoped pointers to them.
- Define destructor-only `delete` behavior for stackalloc-allocated instances.
- Define stackalloc lifetime as the current function activation or the next
  suspension point, whichever comes first.
- Make dynamic stack allocation visible in arrays, prepared results,
  interpolation, and erased generic locals.
- Add `stackalloc T` local variables for erased/runtime-sized generic value
  storage.
- Treat `stackalloc T` locals as unassigned after suspension.
- Require generic foreach loop variables to use `stackalloc T` when the current
  item requires runtime-sized generic storage.
- Preserve `new`, `within`, allocator policy, and fixed-array storage rules.
- Provide diagnostics and tests that make storage choices explicit.

## Non-Goals

- Do not make `stackalloc` storage allocator-managed.
- Do not permit stackalloc storage to escape.
- Do not permit freeing stackalloc storage with allocator deallocation.
- Do not make every interpolation require explicit `stackalloc`.
- Do not silently convert stackalloc storage to heap storage when it crosses
  suspension.
- Do not infer `stackalloc T` through `auto`.
- Do not preserve `init` as a deprecated alias.
- Do not update accepted or rejected completed proposals or prior release notes.

## Terms

- **fixed storage**: lexically scoped inline storage, such as
  `fixed int[100] local = []`.
- **dynamic activation-stack storage**: runtime stack storage allocated by
  `stackalloc`, typically emitted as target `alloca`.
- **allocator-backed storage**: storage allocated by `new` through Camp's
  `within` allocation policy.
- **suspension boundary**: an `await` or `yield` that ends the current native
  activation and resumes later through generated state.
- **stackalloc-backed value**: a pointer, array view, string view, or other
  value whose backing storage came from `stackalloc` or an implicit dynamic
  activation-stack allocation.
- **destructor-only delete**: a `delete` operation that invokes a destructor but
  does not free backing storage.

## Proposed Storage Model

### Arrays

Explicit array allocation has three source forms:

```camp
int[] heap = new int[count];
int[] stack = stackalloc int[count];
fixed int[100] local = [];
```

Array allocation is not available through constructor-call syntax:

```camp
int[] a = int[100]; // ERROR
int[] b = int[n];   // ERROR
```

Diagnostics should point users to the storage choice:

```text
Array allocation requires an explicit storage form; use new int[count], stackalloc int[count], or fixed int[N].
```

Array literals remain valid. They are not allocation. They are closer in spirit
to `fixed`: literal materialization with compiler-selected stack-frame storage.

```camp
int[] numbers = [1, 2, 3];
auto inferred = [1, 2, 3];
```

Pointer initialization from an array literal also remains valid:

```camp
int* numberPointer = [7, 8, 9];
```

Call arguments may continue to lower an array literal to element storage plus
the length argument required by the expanded parameter shape:

```camp
someMethod([4, 5, 6]);
```

Conceptually, the call argument uses compiler-generated fixed literal storage
for the three elements and passes the pointer plus `3`. This is intentionally
different from `stackalloc`: the source is a fixed literal, not dynamic
activation-stack allocation.

### Constructor Calls Into Selected Storage

`Type(args)` means construction into storage that already exists or has already
been selected by the surrounding source form or compiler context:

```camp
Widget value = Widget(args);
slot = Widget(args);
```

Conceptually:

```camp
Widget value;
Widget(args, &value);
```

The constructor expression may construct into local storage, temporary storage,
heap storage selected by `new`, stack storage selected by `stackalloc`, iterator
state, async state, or another destination selected by the surrounding
operation. The restriction is that the constructor expression itself must not
cause the compiler to call `alloca`, allocate through the current allocator, or
otherwise choose new dynamic storage. It only invokes construction against an
existing destination.

The target must resolve to a type with a matching constructor. A
constructor-shaped expression does not construct primitives, callable values, or
types with no compatible constructor.

```camp
auto bounds = Rect(0, 0, 100, 100);
auto points = [Point(0, 0), Point(100, 100)];
auto list = List<string>();
```

If a function and a type with the same name are both visible, the call is
ambiguous unless qualification makes the intended target clear:

```camp
Rect(0, 0, 100, 100); // ERROR if both function Rect and type Rect are visible
```

The compiler should not prefer a function over a type or a type over a
function. Imports should not silently change construction into an ordinary call
or vice versa.

### Optimistic Initializer Lists

Initializer lists may be parsed and bound after any function-call-shaped
expression:

```camp
Rect() { .x = 10, .y = 20 }
makeRect() { .x = 10, .y = 20 }
```

This is optimistic binding. The parser does not need to know whether the call is
construction. The binder may preserve the initializer list on the call-like
expression while overload/type resolution runs. After resolution, the compiler
accepts the initializer list only when the expression is type construction:

```camp
Rect() { .x = 10, .y = 20 }     // OK when Rect resolves to a constructible type
makeRect() { .x = 10, .y = 20 } // ERROR when makeRect resolves to a function
```

Suggested diagnostic:

```text
Initializer lists can only follow type construction.
```

### `new`

`new` keeps its current role:

```camp
Widget* value = within(arena) new Widget(args);
int[] data = within(default) new int[count];
```

`new` allocates through the active `within` policy and produces allocator-backed
storage. That storage may be deleted according to ordinary Camp ownership,
allocator, destructor, and lifetime rules.

### `fixed`

`fixed` remains the visible fixed-storage form:

```camp
fixed int[100] values = [];
fixed char[256] label = $"status: {status}";
```

Fixed storage is lexical. It ends at the block boundary that owns the fixed
declaration.

### `stackalloc`

`stackalloc` selects dynamic activation-stack storage:

```camp
int[] values = stackalloc int[count];
Widget* widget = stackalloc Widget(args);
```

It is not allocator-backed and does not participate in `within`.

The storage cannot be reclaimed early. It remains allocated until the current
function activation ends or the function suspends. Source lifetimes can end
earlier, destructors can run earlier, and values can become invalid earlier,
but the raw stack allocation itself is not individually freed.

## Stackalloc Syntax

### Stackalloc Arrays

```camp
char[] scratch = stackalloc char[count];
byte[] packet = stackalloc byte[packetSize];
```

The array result is the ordinary expanded Camp array shape: separate element
pointer and length values.

Conceptual C lowering:

```c
char *_scratch_elements_alloc = (char *)alloca(sizeof(char) * count);
char *scratch_elements = _scratch_elements_alloc;
uintptr_t scratch_length = count;
```

The compiler must evaluate the element count once:

```camp
char[] scratch = stackalloc char[nextLength()];
```

### Stackalloc Instances

`stackalloc` can allocate storage for a constructed value and return a scoped
pointer:

```camp
Widget* widget = stackalloc Widget(args);
Point* point = stackalloc Point() { .x = 10, .y = 20 };
```

This is the dynamic-stack analogue of:

```camp
Widget* widget = new Widget(args);
```

but the pointer is scoped and cannot escape the stackalloc lifetime.

Type eligibility should follow the same construction rules as `new` except
where storage origin matters. Known-layout classes are allowed. Extern opaque
classes still require a compatible construction surface; `stackalloc` is not a
license to invent layout for an opaque extern type. Shadow classes are
implicitly escaped and therefore cannot be stackalloc-allocated under ordinary
lifetime rules.

Stackalloc arrays have the same element lifecycle behavior as `new` arrays. If
`new T[count]` can construct, initialize, and clean up elements of a given type,
then `stackalloc T[count]` should follow the same type and lifecycle rules, with
the only storage difference being dynamic activation-stack backing instead of
allocator backing.

### Stackalloc Prepared Results

Prepared results gain an explicit stack allocation modifier:

```camp
auto text = (stackalloc) value.toString();
auto bytes = (stackalloc) packet.serialize();
```

This lowers through the same prepared-result protocol:

```camp
nuint required = value.toString(buffer: default);
char[] buffer = stackalloc char[required];
value.toString(buffer: buffer);
auto text = buffer;
```

The spelling mirrors `(new)`:

```camp
auto stackText = (stackalloc) value.toString();
auto heapText = within(default) (new) value.toString();
```

### Stackalloc Interpolation

Interpolation can explicitly select dynamic stack storage:

```camp
auto text = stackalloc $"status: {status}";
Console.writeLine(stackalloc $"status: {status}");
```

Heap allocation remains explicit with `new`:

```camp
escaped string text = within(default) new $"status: {status}" finally delete;
```

Fixed-buffer interpolation remains fixed storage:

```camp
fixed char[128] label = $"status: {status}";
```

## Lifetime Rules

### Storage Duration

Stackalloc storage lasts until the current function activation finishes or
suspends:

```camp
void f(nuint count)
{
	int[] values = stackalloc int[count];
	use(values);
	// raw storage remains allocated until f returns
}
```

In ordinary synchronous code, a stackalloc-backed value may be used after inner
blocks have exited as long as its source lifetime permits and it has not been
invalidated by ordinary lifetime rules:

```camp
int[] values = stackalloc int[count];

if (condition)
{
	use(values);
}

use(values); // OK before function return
```

This differs from fixed storage:

```camp
{
	fixed int[100] values = [];
	use(values[..]);
}

// values is not available here
```

### Suspension Boundary

An `await` or `yield` invalidates stackalloc-backed values allocated before the
suspension point:

```camp
async void bad(@awaitwith Loop* loop)
{
	char[] scratch = stackalloc char[128];
	await loop.tickAsync();
	use(scratch); // ERROR
}
```

Correct:

```camp
async void ok(@awaitwith Loop* loop)
{
	char[] scratch = stackalloc char[128];
	use(scratch);
	await loop.tickAsync();
}
```

The value must also not be retained in the generated async or iterator frame:

```camp
iter char[] bad()
{
	char[] scratch = stackalloc char[128];
	yield scratch; // ERROR unless copied into valid yielded storage
}
```

### Lifetime Identity And Retention

Stackalloc-backed values have a comparable lifetime identity. The identity is
the current method activation segment: the current function activation up to
the next suspension boundary.

In a synchronous method, all currently-valid stackalloc-backed values in the
same method have the same stackalloc lifetime identity:

```camp
List<string>* list = stackalloc List<string>();
string item = stackalloc $"Apple";

list.add(item); // OK when add retains item into list
```

If a method retains an argument into a receiver or destination, the retained
value must live at least as long as the receiver/destination storage. Two
currently-valid stackalloc-backed values in the same activation segment satisfy
that rule because their lifetime identities are equal.

When an API expresses this by requiring an `unscoped` argument, a
stackalloc-backed argument can satisfy the requirement only for a
stackalloc-backed receiver or destination with the same activation-segment
lifetime identity. The conversion is not a general escape permission; it is a
receiver-relative retention check.

The rule is not that stackalloc globally satisfies `unscoped`. It is a lifetime
comparison:

```text
retained value lifetime >= receiver or destination storage lifetime
```

Allowed:

```camp
List<string>* list = stackalloc List<string>();
string item = stackalloc $"Apple";
list.add(item); // OK: same stackalloc activation segment
```

Rejected:

```camp
List<string>* list = within(default) new List<string>();
string item = stackalloc $"Apple";
list.add(item); // ERROR: heap list may outlive stackalloc item
```

Rejected:

```camp
List<string>* list = stackalloc List<string>();
scoped string item = getScopedString();
list.add(item); // ERROR: scoped item may be shorter than stackalloc list
```

An escaped or heap-backed item may be retained into a stackalloc-backed receiver
when ordinary lifetime analysis proves the item remains valid at least until
the stackalloc receiver stops retaining it:

```camp
List<string>* list = stackalloc List<string>();
string item = within(default) new $"Apple" finally delete;
list.add(item); // OK while item remains valid through the list's use
```

Suspension starts a new activation segment. Stackalloc-backed values from the
previous segment are invalid:

```camp
List<string>* list = stackalloc List<string>();
string item = stackalloc $"Apple";

await tick();

list.add(item); // ERROR: both values were invalidated by await
```

Fresh stackalloc values after the suspension have a new equal lifetime identity:

```camp
await tick();

List<string>* list = stackalloc List<string>();
string item = stackalloc $"Apple";
list.add(item); // OK in the post-await activation segment
```

### Escaping

Stackalloc-backed values are scoped. They cannot be returned, stored in escaped
storage, captured by escaped callables, or retained in object state whose
lifetime exceeds the stackalloc lifetime:

```camp
char[] bad(nuint count)
{
	return stackalloc char[count]; // ERROR
}
```

```camp
escaped char[] badStore(nuint count)
{
	escaped char[] result = stackalloc char[count]; // ERROR
	return result;
}
```

### Delete And Destructors

Stackalloc storage cannot be freed early, but `delete` may be used on a
stackalloc-allocated instance to call its destructor.

```camp
Widget* widget = stackalloc Widget(args);
delete widget; // calls ~Widget(), does not free stack storage
```

This follows the same rules as `delete` for structs and other non-heap storage:
the destructor runs, but the backing memory is not returned to an allocator.

The compiler must know the storage origin. For a stackalloc-backed pointer,
`delete` lowers to destructor-only cleanup:

```camp
Widget* widget = stackalloc Widget(args);
delete widget;
```

Conceptually:

```camp
Widget.~Widget(widget);
// no allocator free
```

For a `new` pointer, `delete` keeps ordinary destructor-plus-free behavior:

```camp
Widget* widget = within(default) new Widget(args);
delete widget; // destructor plus allocator/free path
```

For ambiguous pointer origins, the compiler should not guess. If a pointer may
refer to allocator-backed or stackalloc-backed storage, source must use an
operation whose ownership semantics are clear, or the compiler must preserve
enough origin information to lower correctly.

After destructor-only `delete`, using the object as a constructed value is
invalid even though the raw stack storage remains allocated:

```camp
Widget* widget = stackalloc Widget(args);
delete widget;
widget.doWork(); // ERROR: object was destroyed
```

## Loops And Implicit Dynamic Stack Allocation

Explicit `stackalloc` inside a loop is allowed:

```camp
while (running)
{
	char[] scratch = stackalloc char[nextLength()];
	use(scratch);
}
```

This can consume unbounded stack in an unsuspended loop, but the source has made
the allocation explicit. That acknowledgement is sufficient; no warning is
required merely because explicit `stackalloc` appears in a loop.

The compiler should still control implicit dynamic stack allocation. If a
compiler-inserted dynamic stack allocation can be reached repeatedly without
crossing a suspension boundary, the source must choose storage explicitly.

Rejected:

```camp
while (running)
{
	Console.writeLine($"status: {status}"); // if this needs implicit dynamic stack storage
}
```

Corrected:

```camp
while (running)
{
	Console.writeLine(stackalloc $"status: {status}");
}
```

or:

```camp
while (running)
{
	Console.writeLine(within(default) new $"status: {status}" finally delete);
}
```

Suspension can bound the risk:

```camp
while (running)
{
	Console.writeLine($"tick {count}");
	await timer.next();
}
```

If every repeated path back to the implicit allocation crosses a suspension
boundary, the implicit allocation is allowed because the prior activation-stack
allocation is invalidated before the next iteration can allocate again.

## Generics

This proposal includes `stackalloc T` locals for erased/runtime-sized generic
value storage:

```camp
stackalloc T item = default;
```

This is a source-visible storage class, not an ordinary local and not merely
sugar for `T*`. The variable uses value syntax:

```camp
stackalloc T item = default;
item = array[i];
other[j] = item;
use(in item);
```

The backing storage is dynamic activation-stack storage allocated by the
compiler. Assignment copies into that storage. Reads copy or pass from that
storage according to ordinary value rules.

Ordinary `T` locals are rejected when `T` has runtime-sized erased storage:

```camp
T item = array[i]; // ERROR when T has runtime-sized storage
```

The programmer writes:

```camp
stackalloc T item = array[i];
```

`auto` must not infer `stackalloc T`:

```camp
auto item = array[i]; // ERROR when this would require stackalloc T
```

### Function-Entry Stackalloc Area

For each function that declares `stackalloc T` locals or needs compiler-managed
generic default slots, the compiler should allocate one stackalloc area at
function entry.

Conceptually:

```camp
void f<T: any>(T[] values, sizeof(T))
{
	stackalloc T current = default;
	stackalloc T temp = default;
}
```

lowers to:

```c
uint8_t *__stackallocFrame = alloca(totalGenericStackSize);
void *current = __stackallocFrame + currentOffset;
void *temp = __stackallocFrame + tempOffset;
```

The same allocation is performed on every generator or async resume before the
generated code jumps to the resume label. Generated code must not jump past the
allocation.

This avoids repeated hidden `alloca` calls inside loops. The compiler may
conservatively reserve one slot per declared `stackalloc T` local and later
optimize by sharing slots whose live ranges do not overlap.

`sizeof(T)` is guaranteed to be safe for storage alignment. Allocating a
stackalloc area for generic temporaries using `sizeof(T)` must therefore be no
less alignment-safe than allocating an actual `T[]` array. The compiler should
use the same layout/alignment assumptions for the `stackalloc T` temporary area
that it uses for arrays of `T`.

### Definite Assignment After Suspension

Every `stackalloc T` local is treated as unassigned after every suspension
point:

```camp
stackalloc T item = values[i];
yield item;
use(item); // ERROR: item is unassigned after yield
```

This matches the storage model. The backing stackalloc area is reallocated on
resume, so the previous contents are gone. The diagnostic should be an ordinary
read-before-write/definite-assignment diagnostic, not a special generic
exception.

### Generic Foreach

When a foreach current item has erased/runtime-sized generic storage, the loop
variable must be declared as `stackalloc T`:

```camp
foreach (stackalloc T item in values)
{
	use(item);
}
```

`auto` is rejected:

```camp
foreach (auto item in values) // ERROR when item would require stackalloc T
{
}
```

After a suspension, the loop variable is unassigned:

```camp
foreach (stackalloc T item in values)
{
	yield item;
	yield item; // ERROR: item is unassigned after the first yield
}
```

The common one-yield case remains valid because the next iteration assigns the
loop variable again before it is read:

```camp
foreach (stackalloc T item in values)
	yield item;
```

### Manual Iterator Consumption

Manual iterator consumption also uses `stackalloc T`:

```camp
stackalloc T current = default;

while (values(&current))
{
	yield current;
	current = default;
}
```

After `yield`, `current` is unassigned. The explicit assignment before the next
address-taking operation makes the storage re-use visible:

```camp
while (values(&current))
{
	yield current;
	values(&current); // ERROR unless current was assigned after yield
}
```

### Generic `default`

Generic `default` can require hidden runtime-sized storage:

```camp
void DoAction<T: any>(in T arg);

void Outer<T: any>(const int[] y, sizeof(T))
{
	foreach (auto x in y)
		DoAction<T>(default);
}
```

With `stackalloc T` locals in the language, the compiler may use a
compiler-managed default slot in the function-entry stackalloc area:

```camp
stackalloc T __default_T = default;
DoAction<T>(__default_T);
```

This is allowed because the storage is not allocated repeatedly at the call
site. It obeys the same suspension rule as explicit `stackalloc T` locals.

### Remaining Generic Temporary Classifications

The accepted classifications are:

- transformed prep results: implicit when low-risk; require `(stackalloc)` or
  `(new)` on hazardous repeated paths;
- runtime interpolation: implicit when low-risk; require `stackalloc $"..."` or
  `new $"..."` on hazardous repeated paths;
- generic local value temporaries: use `stackalloc T`; reject ordinary `T`
  locals when `T` has runtime-sized storage;
- generic foreach current items: require `foreach (stackalloc T item in ...)`
  when the item requires runtime-sized storage; reject `auto`;
- generic iterator current storage: use `stackalloc T current`; after
  suspension it is unassigned until written again;
- generic swap/copy scratch temporaries: use `stackalloc T temp`;
- generic `default` for address-required positions: use a compiler-managed
  stackalloc default slot when safe;
- array literals: fixed literal storage, not dynamic stack allocation.

Other forms to audit:

- initializer literals for erased generic `T`;
- optional and array literals containing erased generic values;
- conversions that require materializing an intermediate erased generic value;
- passing `default` to address-requiring parameters in suspending contexts.

## Interaction With `finally` And Cleanup

Cleanup paths must respect stackalloc suspension lifetime.

A `finally` expression or cleanup block may execute after a suspension point if
control suspends before cleanup runs and resumes later. It must not access
stackalloc-backed values allocated before that suspension:

```camp
async void bad(@awaitwith Loop* loop)
{
	char[] scratch = stackalloc char[128];
	try
	{
		await loop.tickAsync();
	}
	finally
	{
		use(scratch); // ERROR
	}
}
```

Destructor-only cleanup is valid only before the suspension boundary:

```camp
async void ok(@awaitwith Loop* loop)
{
	Widget* widget = stackalloc Widget();
	delete widget;
	await loop.tickAsync();
}
```

Invalid:

```camp
async void bad(@awaitwith Loop* loop)
{
	Widget* widget = stackalloc Widget();
	await loop.tickAsync();
	delete widget; // ERROR: destructor would read invalid stackalloc storage
}
```

The analyzer should treat this as use-after-lifetime, not as a special cleanup
exception.

## Lowering Model

### Constructor Calls

Source:

```camp
Rect bounds = Rect(0, 0, 100, 100);
bounds = Rect(10, 10, 50, 50);
draw(Rect(0, 0, 20, 20));
```

Conceptually, the compiler selects destination storage from the surrounding
context, then lowers construction as a constructor helper call against that
storage:

```camp
Rect bounds;
Rect_op_initnew(&bounds, 0, 0, 100, 100);
```

For assignment, the destination is the assigned variable or storage location.
For call arguments, returns, yields, array literals, generated iterator current
storage, and similar contexts, the destination is the temporary/result storage
already selected by that operation. The constructor expression itself does not
select allocator-backed or dynamic activation-stack storage.

Trailing initializer lists lower only after the call-like expression resolves as
type construction:

```camp
Rect bounds = Rect() { .x = 10, .y = 20 };
```

If the expression resolves as an ordinary function call, the initializer list is
diagnosed before lowering.

### Arrays

Source:

```camp
char[] scratch = stackalloc char[count];
```

Conceptual C:

```c
uintptr_t scratch_length = count;
char *scratch_elements = (char *)alloca(sizeof(char) * scratch_length);
```

Camp arrays lower as expanded values. There is no required array struct
temporary.

### Instances

Source:

```camp
Widget* widget = stackalloc Widget(args);
```

Conceptual lowering:

```camp
Widget* widget = /* stack allocation for sizeof(Widget) */;
Widget(args, widget);
```

For C emission:

```c
Widget *widget = (Widget *)alloca(sizeof(Widget));
Widget_op_initnew(widget, args);
```

The actual constructor helper name and vtable/interface initialization sequence
must follow existing construction lowering.

### Prepared Results

Source:

```camp
auto text = (stackalloc) value.toString();
```

Conceptual lowering:

```camp
nuint required = value.toString(buffer: default);
char[] buffer = stackalloc char[required];
value.toString(buffer: buffer);
auto text = buffer;
```

### Interpolation

Source:

```camp
auto text = stackalloc $"value: {value}";
```

Conceptual lowering:

```camp
nuint required = /* interpolation length */;
char[] buffer = stackalloc char[required];
/* write literal text and formatted holes */
auto text = buffer;
```

When interpolation converts to `string`, null-termination remains part of
interpolation conversion, not a general `stackalloc` rule.

### `stackalloc T` Locals

Source:

```camp
stackalloc T item = default;
stackalloc T temp = values[i];
```

Conceptual lowering at function entry:

```c
uint8_t *__stackallocFrame = alloca(totalGenericStackSize);
void *item = __stackallocFrame + itemOffset;
void *temp = __stackallocFrame + tempOffset;
```

Assignments copy into the assigned slot:

```c
memcpy(temp, elementAddress, sizeof_T);
```

For generator and async lowering, the stackalloc frame is recreated on every
resume before control reaches any resume label. Reads after a suspension point
are rejected by definite-assignment analysis until the variable is assigned
again.

## Compiler Analysis

The compiler needs several related facts and checks.

### Storage Origin

Values can be backed by:

- allocator-backed storage;
- fixed lexical storage;
- dynamic activation-stack storage;
- borrowed parameter or field storage;
- unknown storage.

Local slots must have a stable storage-origin/lifetime class. A slot initialized
or declared as stackalloc-backed remains a stackalloc-origin slot; later
assignments must be compatible with that class. The compiler must not allow a
scoped, heap, escaped, fixed, or unknown-origin value to overwrite a
stackalloc-origin slot, and it must not allow a stackalloc-origin value to
overwrite a differently-originated slot except through an explicit permitted
borrow/conversion at the use site.

This keeps `delete` lowering deterministic and prevents CFG joins from creating
locals whose origin is "maybe stackalloc, maybe heap, maybe borrowed."

Stackalloc-backed values must retain origin facts through:

- pointer assignment;
- array views and slices;
- prepared-result rewrites;
- interpolation rewrites;
- casts that preserve pointer identity;
- destructor-only delete checks;
- `stackalloc T` locals and compiler-managed generic default slots.

### Suspension Lifetime

Every stackalloc-backed value has an invalidation boundary:

- function return in non-suspending code;
- the next `await` or `yield` in suspending code.

Use after that boundary is invalid.

The analyzer should assign stackalloc-backed values a lifetime identity for the
current method activation segment. Two currently-valid stackalloc-backed values
in the same segment have equal lifetimes. Suspension invalidates that identity
for source values allocated before the suspension and starts a new segment for
fresh stackalloc values after resumption.

Retention checks should compare lifetimes directly:

```text
retained value lifetime >= receiver or destination storage lifetime
```

A stackalloc-backed value can therefore be retained into another
stackalloc-backed value from the same activation segment. It cannot be retained
into heap/escaped/unknown-lifetime storage, and shorter scoped values cannot be
retained into stackalloc-backed storage.

The analysis is similar to use-after-free. It should be integrated with the
existing lifetime facts rather than implemented as an unrelated pass.

### Implicit Allocation Cycle Analysis

Explicit `stackalloc` is allowed in loops. The cycle analysis exists only for
implicit compiler-created dynamic stack allocations.

The compiler should reject an implicit dynamic stack allocation when:

1. the allocation site is on a repeated control-flow path;
2. the path can reach the same allocation again;
3. no suspension boundary is guaranteed between those visits.

This analysis should be linear in the method body graph using ordinary CFG/SCC
techniques.

### Definite Assignment For `stackalloc T`

`stackalloc T` locals participate in definite-assignment analysis, with one
extra invalidation rule:

```camp
stackalloc T item = values[i];
yield item;
use(item); // ERROR: item is unassigned after suspension
```

Every `await` or `yield` marks every stackalloc-backed generic local as
unassigned. The next read is invalid until an assignment writes new contents
into the re-created stackalloc frame.

## Diagnostics

Old array allocation syntax:

```camp
char[] buffer = init char[count];
```

Suggested diagnostic:

```text
`init` is no longer a Camp keyword. Use stackalloc char[count], new char[count], or fixed char[N].
```

Array allocation through constructor-like syntax:

```camp
char[] buffer = char[count];
```

Suggested diagnostic:

```text
Array allocation requires an explicit storage form; use stackalloc char[count], new char[count], or fixed char[N].
```

Initializer list after an ordinary function call:

```camp
makeRect() { .x = 10, .y = 20 }
```

Suggested diagnostic:

```text
Initializer lists can only follow type construction.
```

Ambiguous constructor-shaped call:

```camp
Rect(0, 0, 100, 100);
```

Suggested diagnostic when both a function and constructible type named `Rect`
are visible:

```text
`Rect(...)` is ambiguous because both a type constructor and a function named `Rect` are visible.
```

Returning stackalloc storage:

```camp
char[] make(nuint count)
{
	return stackalloc char[count];
}
```

Suggested diagnostic:

```text
Stackalloc-backed value cannot be returned because its storage ends with the current function activation.
```

Use after suspension:

```camp
char[] buffer = stackalloc char[100];
await tick();
use(buffer);
```

Suggested diagnostic:

```text
Stackalloc-backed value 'buffer' cannot be used after await; allocate with new or move the use before the suspension point.
```

Deleting stackalloc storage:

```camp
Widget* widget = stackalloc Widget();
delete widget;
```

This is valid and should not warn. If the type has a destructor, the compiler
calls it and does not free memory.

Delete with an explicit `within` context follows the same rules as delete with
structs. It may provide the destructor's allocator/context arguments when the
destructor surface requires them, but it must not free stackalloc backing
storage.

Generic hidden stack allocation:

```camp
auto item = array[i];
```

Suggested diagnostic when `item` would require erased runtime-sized storage:

```text
Inferred local 'item' would require hidden dynamic stack storage; declare it as stackalloc T or write into an existing destination.
```

## Before And After Examples

### Runtime Scratch Array

Before:

```camp
char[] buffer = init char[length];
```

After:

```camp
char[] buffer = stackalloc char[length];
```

or:

```camp
char[] buffer = within(default) new char[length] finally delete;
```

### Fixed Scratch Array

Before:

```camp
char[] buffer = init char[256];
```

After:

```camp
fixed char[256] buffer = [];
```

or, when an array view is needed:

```camp
fixed char[256] storage = [];
char[] buffer = storage[..];
```

### Constructed Local

Before:

```camp
Widget value = init Widget(args);
```

After:

```camp
Widget value = Widget(args);
```

### Stack Constructed Instance

When dynamic stack pointer storage is intended:

```camp
Widget* pointer = stackalloc Widget(args);
```

### Destructor Without Free

```camp
Widget* pointer = stackalloc Widget(args);
use(pointer);
delete pointer; // destructor only
```

### Generic Temporary

Before:

```camp
T item = array[i];
other[j] = item;
```

After:

```camp
stackalloc T item = array[i];
other[j] = item;
```

### Interpolation In A Tight Loop

Before:

```camp
while (running)
{
	Console.writeLine($"status: {status}");
}
```

After:

```camp
while (running)
{
	Console.writeLine(stackalloc $"status: {status}");
}
```

### Interpolation In A Suspending Loop

```camp
while (running)
{
	Console.writeLine($"status: {status}");
	await timer.next();
}
```

No source change is required if the compiler proves that every repeated path to
the implicit allocation crosses suspension.

## Compiler Performance Impact

Parser impact is moderate:

- `stackalloc T[n]`;
- `stackalloc Type(args)`;
- `stackalloc Type(args) { ... }`;
- `stackalloc $"..."`;
- `(stackalloc) preparedCall`.
- call-like expressions with trailing initializer lists, parsed optimistically
  before the call is known to be construction.

Binding impact is moderate:

- removal of `init` as a construction keyword;
- constructor-call recognition for `Type(args)` and `Type<T>(args)`;
- ambiguity diagnostics when a call target can resolve to both a function and a
  constructible type;
- validation of optimistically parsed initializer lists after call/type
  resolution;
- stackalloc type eligibility;
- storage-origin facts;
- destructor-only delete selection;
- suspension lifetime checks;
- generic hidden-temporary diagnostics.

Lowering impact is moderate:

- `init` construction lowering paths are replaced by constructor-call lowering
  into selected storage;
- array literal lowering remains available and should continue using
  compiler-generated fixed literal storage, not allocation;
- stackalloc arrays reuse existing internal stack allocation emission;
- stackalloc instances reuse construction lowering with stack storage as the
  target;
- prepared results and interpolation gain explicit stackalloc variants;
- delete lowering must distinguish destructor-only stackalloc delete from
  destructor-plus-free heap delete.

Control-flow impact is moderate but bounded:

- use-after-suspension checks require lifetime flow;
- implicit-allocation cycle analysis requires CFG/SCC traversal;
- both are linear in method body size.

Overall compiler performance should not materially change. The feature mostly
reuses existing construction, lifetime, and C alloca infrastructure, but it
requires better source-origin tracking.

## Language Complexity Impact

The language gains one loud storage keyword and removes `init`.

Before:

```camp
char[] a = init char[n]; // storage selection hidden behind init
```

After:

```camp
char[] a = stackalloc char[n]; // storage selection explicit
```

This is more verbose, but the verbosity corresponds to real risk. Dynamic stack
allocation is not ordinary block storage and should not look like ordinary
construction.

The model becomes easier to explain:

```camp
fixed T[N] x = []; // lexical fixed storage
stackalloc T[N]   // dynamic activation-stack storage
new T[N]          // allocator-backed storage
T(args)           // construct into selected existing storage
```

## Implementation Work

1. Add parser support for the stackalloc source forms.
2. Remove `init` from keyword parsing and source construction syntax.
3. Add diagnostics for old `init` syntax.
4. Bind constructor-shaped calls whose targets resolve to constructible types as
   construction into selected storage.
5. Diagnose ambiguous constructor-shaped calls when both a callable and a
   constructible type target are visible.
6. Parse and bind trailing initializer lists after call-like expressions
   optimistically.
7. Diagnose trailing initializer lists after resolution when the preceding
   expression is not type construction.
8. Preserve array literal materialization for array values, pointer
   initialization, and call arguments as fixed literal storage.
9. Bind `stackalloc T[n]` as dynamic stack array allocation.
10. Bind `stackalloc Type(args)` and object-initializer variants as
   stackalloc-backed construction.
11. Add stackalloc storage-origin facts to lifetime analysis.
12. Add stable local-slot storage-origin/lifetime class tracking, including
    rejection of assignments that mix stackalloc-origin slots with scoped,
    heap, escaped, fixed, or unknown origins.
13. Add stackalloc activation-segment lifetime identities and equality
    comparison for currently-valid stackalloc-backed values in the same
    segment.
14. Add retention checks that permit retaining stackalloc-backed values into
    same-segment stackalloc-backed receivers/destinations and reject retention
    into longer-lived storage.
15. Add suspension invalidation checks for stackalloc-backed values.
16. Add destructor-only delete lowering for stackalloc-backed constructed
   instances.
17. Reject allocator-free paths for stackalloc-backed storage.
18. Add explicit `(stackalloc)` prepared-result lowering.
19. Add explicit `stackalloc $"..."` interpolation lowering.
20. Add implicit dynamic allocation cycle diagnostics for prep/interpolation and
    any remaining compiler-generated dynamic stack temporaries.
21. Add `stackalloc T` local binding for erased/runtime-sized generic value
    storage.
22. Add function-entry stackalloc area planning for `stackalloc T` locals and
    compiler-managed generic default slots.
23. Recreate the function-entry stackalloc area on generator/async resume before
    jumping to resume labels.
24. Treat `stackalloc T` locals as unassigned after suspension.
25. Reject ordinary `T` locals and `auto` locals when they would require
    runtime-sized generic stack storage.
26. Require generic foreach current items to use `stackalloc T` when runtime-
    sized storage is required.
27. Use compiler-managed stackalloc default slots for generic `default` in
    address-required positions when safe.
28. Update target diagnostics for targets that lack dynamic stack allocation.
29. Update tests and living documentation.

## Test Plan

Diagnostics:

- old `init` syntax rejected with diagnostics that point to constructor-call,
  `new`, `stackalloc`, or `fixed` replacements as appropriate;
- `T[n]` array allocation without `new` or `stackalloc` rejected;
- `Type(args)` accepted as construction when `Type` resolves to a constructible
  type;
- `Type<T>(args)` accepted as generic type construction when the target resolves
  to a constructible generic type;
- constructor-shaped calls diagnosed as ambiguous when both a callable and a
  constructible type target are visible;
- trailing initializer lists after type construction accepted;
- trailing initializer lists after ordinary function calls rejected after
  resolution;
- `stackalloc T[n]` accepted for constant and runtime lengths;
- array literals accepted for array locals, `auto` locals, pointer
  initialization, and call arguments;
- stackalloc values rejected when returned;
- stackalloc values rejected when stored in escaped storage;
- stackalloc values rejected when captured by escaped callables;
- stackalloc values rejected after `await`;
- stackalloc values rejected after `yield`;
- assignment from scoped, heap, escaped, fixed, or unknown-origin values into a
  stackalloc-origin local rejected;
- assignment from stackalloc-origin values into differently-originated locals
  rejected except through permitted borrow/conversion at the use site;
- same-segment stackalloc value retained into stackalloc-backed receiver
  accepted;
- stackalloc value accepted for a receiver-retained `unscoped` parameter only
  when the receiver/destination is stackalloc-backed with the same activation
  segment lifetime identity;
- stackalloc value retained into heap/escaped/unknown-lifetime receiver
  rejected;
- shorter scoped value retained into stackalloc-backed receiver rejected;
- stackalloc values from a previous suspension segment rejected even when both
  receiver and retained value were stackalloc-backed before suspension;
- `delete` on stackalloc-allocated instance accepted with the same behavior as
  struct delete;
- delete with an explicit `within` context on stackalloc-backed storage follows
  struct-delete behavior and does not free backing storage;
- use after destructor-only delete rejected;
- implicit interpolation/prepared allocation rejected on unsuspended repeated
  paths;
- implicit interpolation/prepared allocation accepted in suspension-bounded
  loops;
- `stackalloc T` locals accepted for erased/runtime-sized generic storage;
- ordinary `T` locals rejected when they require runtime-sized stack storage;
- `auto` locals rejected when they would infer `stackalloc T`;
- `foreach (stackalloc T item in values)` accepted when the current item
  requires runtime-sized generic storage;
- `foreach (auto item in values)` rejected when the current item would require
  runtime-sized generic storage;
- `stackalloc T` locals treated as unassigned after `yield` and `await`;
- generic `default` for `in T` accepted through a compiler-managed stackalloc
  default slot when the storage does not need to survive suspension.

C emission:

- `[1, 2, 3]` emits fixed literal element storage, not dynamic stackalloc;
- call argument `[4, 5, 6]` emits literal element storage plus the expanded
  pointer/length arguments;
- `Rect(args)` emits construction into selected destination storage and does not
  allocate by itself;
- `Rect(args) { .x = value }` emits constructor lowering followed by initializer
  lowering according to existing construction rules;
- `stackalloc char[n]` emits expanded array element/length variables and target
  alloca;
- `stackalloc Widget(args)` emits alloca plus the constructor helper;
- destructor-only delete emits destructor call and no free;
- `new Widget(args)` still emits destructor plus allocator/free path on delete;
- `(stackalloc) value.toString()` emits prepared two-call lowering with alloca;
- `stackalloc $"..."` emits interpolation lowering with alloca;
- function with `stackalloc T` locals emits one function-entry stackalloc area;
- generator with `stackalloc T` locals recreates the stackalloc area on each
  resume before the resume label;
- `fixed char[256]` remains fixed lexical storage and does not use alloca.

Runtime:

- constructor-call locals, assignment, arguments, returns, and yields construct
  into the selected destination storage;
- stackalloc-backed containers can retain same-segment stackalloc-backed values
  and observe them through destructor-only cleanup before invalidation;
- stackalloc arrays read/write correctly;
- stackalloc constructed instances initialize and destruct correctly;
- destructor-only delete runs destructor exactly once;
- prepared results and interpolation produce correct values with explicit
  stackalloc;
- suspension-bounded loops using implicit interpolation behave correctly;
- `stackalloc T` assignment, read, `in T` calls, and array copy operations work
  for copyable concrete instantiations;
- generic foreach with `stackalloc T` produces correct results;
- generic iterator code rejects reads of `stackalloc T` values after suspension
  until reassignment.

## Documentation Work

If accepted and implemented, update the living documentation:

- `docs/language/07-lifetimes-allocation-and-within.md` for `new`,
  `stackalloc`, fixed storage, constructor-call construction, and suspension
  lifetime;
- `docs/language/04-everyday-types-values-text-arrays-and-optionals.md` for the
  new array allocation forms;
- `docs/language/05-structs-classes-and-object-lifetimes.md` for stackalloc
  instance construction and destructor-only delete;
- `docs/language/13-generics-and-capabilities.md` for `stackalloc T` locals,
  generic foreach, generic default slots, and hidden generic temporary
  restrictions;
- `docs/language/14-iterators-foreach-and-generated-sequences.md` for
  stackalloc and `yield`;
- `docs/language/15-async-await-and-deferred-calls.md` for suspension
  invalidation;
- `docs/language/18-expressions-statements-and-operators-reference.md` for
  syntax reference;
- `docs/semantics/05-lifetime-analysis-and-flow-facts.md` for storage-origin
  facts, stable local-slot origin classes, stackalloc activation-segment
  lifetime identities, and retention checks;
- `docs/semantics/06-generics-erasure-and-capabilities.md` for erased generic
  storage;
- `docs/semantics/08-async-resumption-lowering.md` for stackalloc invalidation
  at suspension;
- `docs/semantics/10-construction-destruction-and-allocation.md` for the new
  storage model and delete lowering;
- `docs/semantics/14-core-expression-statement-and-access-semantics.md` for
  prepared results and interpolation storage selection;
- `docs/camp-llm-coding-guide.md` for practical guidance on choosing storage.

Do not update accepted or rejected completed proposals or prior release notes as
part of this work.

## Risks And Tradeoffs

### Source Break

Removing `init` is a source break. The language is still in preview, and the
replacement forms are clearer:

```camp
stackalloc T[n]
new T[n]
fixed T[N]
T(args)
```

### More Visible Syntax

`stackalloc` is noisier than the old `init T[n]` spelling. That is
intentional. Dynamic stack allocation has enough unusual behavior that source
should name it.

### Destructor-Origin Tracking

Allowing `delete` to mean destructor-only for stackalloc instances requires
storage-origin tracking through pointer values. If origin becomes unknown, the
compiler must diagnose or require a clearer operation rather than guessing.

### Async And Generators

The suspension invalidation rule is strict. Some useful code may need to move
uses before `await`/`yield` or allocate with `new`. This is preferable to
retaining invalid native stack pointers in generated frames.

### Generic Storage Category

`stackalloc T` locals add a new local storage category. That increases language
and compiler complexity, but it avoids making generic foreach, generic swaps,
and iterator-current code pointer-heavy. The feature is justified because it is
visible in source and carries strict definite-assignment rules after
suspension.

### Stackalloc Lifetime Identity

Giving stackalloc-backed values a comparable activation-segment lifetime adds
precision to lifetime analysis. The benefit is that stackalloc-backed
containers can safely retain same-segment stackalloc-backed values. The cost is
that local slots need stable origin classes and retention checks need to compare
stackalloc segment identities instead of treating stackalloc as merely scoped.

## Resolved Design Points

- `init` is removed as a language keyword.
- `Type(args)` constructs into temporary or generated storage selected by the
  surrounding context, but the constructor expression itself must not
  dynamically allocate storage.
- Trailing initializer lists after call-like expressions are parsed and bound
  optimistically, then rejected after resolution unless the expression is type
  construction.
- `stackalloc T[n]` is allowed for constant and runtime lengths.
- `stackalloc` is the explicit dynamic activation-stack allocation keyword.
- Stackalloc instance construction is allowed for known-layout classes under
  the same construction rules as `new`; shadow classes remain disallowed by
  ordinary escaped lifetime rules.
- Stackalloc arrays have the same element lifecycle behavior as `new` arrays.
- Stackalloc storage cannot be individually reclaimed before function return or
  suspension.
- Stackalloc-backed values have a lifetime identity for the current method
  activation segment.
- Two currently-valid stackalloc-backed values in the same activation segment
  have equal lifetimes.
- Suspension invalidates the current stackalloc activation segment and fresh
  stackalloc values after resumption belong to a new segment.
- Local slots have stable storage-origin/lifetime classes; assignments may not
  mix stackalloc-origin slots with scoped, heap, escaped, fixed, or unknown
  origins except through permitted borrow/conversion at a use site.
- A stackalloc-backed value may be retained into another stackalloc-backed value
  with the same activation-segment lifetime identity.
- A stackalloc-backed value may not be retained into heap, escaped, or
  unknown-lifetime storage.
- A shorter scoped value may not be retained into stackalloc-backed storage.
- `delete` on a stackalloc-allocated instance invokes the destructor and does
  not free memory, following the same rules as delete with structs.
- `stackalloc T` locals are included for erased/runtime-sized generic value
  storage.
- `sizeof(T)` is guaranteed to be alignment-safe for this storage; allocating a
  stackalloc area for generic temporaries is no less alignment-safe than
  allocating an actual array of `T`.
- `stackalloc T` locals are allocated from a function-entry stackalloc area that
  is recreated on generator/async resume.
- `stackalloc T` locals are treated as unassigned after suspension.
- `auto` must not infer `stackalloc T`.
- Generic foreach current items must use `stackalloc T` when runtime-sized
  generic storage is required.
- Generic `default` for address-required positions may use compiler-managed
  stackalloc default slots when safe.
- Explicit `stackalloc` in loops is sufficient acknowledgement; no warning is
  required merely because it appears in a loop.
- `(stackalloc) value.toString()` is the prepared-result spelling.
- Low-risk implicit dynamic stack allocation for prep and interpolation remains
  allowed; explicit `stackalloc` is required only when the compiler detects a
  hazardous repeated path or the source explicitly chooses stack storage.
- Array literal lifetime/provenance follows existing fixed-like rules; assigning
  a literal-derived pointer/view into longer-lived storage is rejected by
  ordinary lifetime analysis.

## Open Questions

No open design questions are known for the draft proposal at this point. The
remaining work is implementation validation: identify every current
compiler-generated dynamic stack temporary and ensure it follows the
classification in this proposal.

## Readiness To Move To Pending

This proposal is ready to move to pending when reviewers agree on:

- removal of `init` as a language keyword;
- constructor-call construction into selected storage;
- optimistic parsing/binding and post-resolution validation of trailing
  initializer lists;
- stackalloc array and instance syntax;
- destructor-only delete semantics;
- suspension lifetime rules;
- implicit dynamic stack allocation diagnostics;
- `stackalloc T` local syntax and unassigned-after-suspension behavior;
- generic foreach requiring `stackalloc T` for runtime-sized current items;
- compiler-managed generic default slots.
