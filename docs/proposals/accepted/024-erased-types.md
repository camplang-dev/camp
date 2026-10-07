# Erased Types And Nested Optionals

## Status

Accepted and implemented (DEV-017).

## Proposal Date

2026-10-06

## Last Updated Date

2026-10-06

## Summary

This proposal adds the erased type `void<V>`. It lowers to `void`; `V` is an
annotation that tells Camp what the storage holds. Camp code uses a `void<V>*`
as it would use a `V*`, but across the C ABI it is always a `void*`, and a
Camp viewer that cannot see `V` sees the same `void*`. Like `void`, a `void<V>` is never
stored by value: it sits behind a pointer, an array, an `in` or `out`
parameter or a `stackalloc` local, and it is returned only through storage the
caller provides.

`void<V>` replaces `struct(V)`. It is the single-object form of an expandable
type wherever one object is required: an array element, a pointer target, the
value an erased generic stores. It also lets any declaration, at any
visibility, hold a pointer to a type it could not otherwise name.

The proposal settles three related rules:

- Optionals may nest. `int??`, `int[]?` and `IntPredicate?` are ordinary types.
  Array elements stay single values; an array of an expandable type is written
  `void<V>[]`.
- An erased generic declaration lowers the same way for every type argument. A
  function or callable fills a generic callback only when its signature matches
  after substitution. There are no automatic adapters; a lambda is the
  convenient way to adapt.
- An erased `T` is never passed by value.

## Motivation

Today the language leaves nested expandable shapes half-defined:

- `int[]?`, `delegate int()?` and `once int()?` are accepted as types, but an
  `int[]?` has no `.value` and a `delegate int()?` has no `.specified`, so the
  payload can never be used.
- `int??`, `int?[]` and `int[][]` are rejected with a suggestion to write
  `struct(...)`, but `struct(...)` is then rejected as a local or cast, and
  where it is accepted the program fails to build.
- Generic code produces nested shapes, yet nesting collapses: the result of
  `makeInnerOptional(makeInnerOptional(makeOptional(5)))` behaves as `int??`
  and its `.value` is an `int`.
- An optional of a fixed struct or a class value is accepted although such a
  payload cannot be copied.

Erased generics accept calls that cannot work:

- An erased `T` parameter passed by value is accepted but cannot be compiled.
- A function whose calling shape differs from a substituted callback type is
  accepted and then misbehaves:

```camp
int peek(in int? v) { return v.specified ? v.value : -1; }
int read<T: copyable>(in T v, delegate int(in T x) f, sizeof(T)) => f(v);

int seven() { return 7; }
Point makePoint() { Point p = { 3, 4 }; return p; }
T produce<T: copyable>(delegate T() make, sizeof(T)) => make();

int? a = 4;
read<int?>(a, peek);        // prints -1 today
produce<int>(seven);        // fails to build today
produce<Point>(makePoint);  // fails to build today
```

Finally, an exported declaration cannot mention a private or internal type, even
behind a pointer. Opaque handles are awkward, and an exported `struct iter` is
impossible whenever its retained state holds a pointer to a private type,
because a struct iterator's state is part of its exposed shape.

## Goals

- Give every nested expandable shape exactly one spelling, and make every
  spelled shape usable.
- Let any declaration, at any visibility, hold an opaque pointer to any type,
  including the retained state of an exported `struct iter`.
- Lower every erased generic declaration the same way for every type argument,
  and report every callback whose signature does not match instead of
  miscompiling it.
- Keep the expanded forms, component names, `in`, `out` and results of existing
  non-generic code unchanged.

## Non-Goals

- No layout guarantee for an expandable value outside `void<V>`. An `int?` is two
  separate storage locations wherever it appears.
- No automatic adapters between generic callback types and functions with a
  different calling shape.
- No arrays of expandable elements except as `void<V>[]`.
- No `void<V>` stored by value.

## 1. Expandable Types

The expandable types are:

- arrays `T[]`, with components `elements` and `length`;
- optionals `T?`, with components `value` and `specified`, including optionals
  of expandable payloads (§3);
- delegate-shape types: `delegate`, `once`, `async` and `iter` callables, and
  callable newtypes, with components `call` and `context`;
- distributive pointers to an expandable type at any depth, such as `int[]*`,
  `int?*` and `delegate void()*`.

A distributive pointer has one pointer per component of its pointee: `int[]*`
is an `int**` and a `nuint*`, and `int??*` is three pointers.

## 2. Erased Types

`void<V>` is valid for any type `V` other than `void` or another erased type. It
lowers to `void`, so a `void<V>*` is a `void*` in C. Inside Camp, `V` says what
the storage holds:

- For a non-expandable `V`, `void<V>` storage is `V`'s storage, and members are
  reached as through a `V*`.
- For an expandable `V`, `void<V>` storage is one object with the members of a
  `V` value (§3). A `void<int?>* p` has `p.value` and `p.specified`; a
  `void<int[]?>* q` has `q.specified`, `q.value`, `q.elements` and `q.length`.

A `void<V>` value is never named. Reading one, by dereferencing, indexing or
calling, produces a `V`. Writing a `V` into `void<V>` storage stores it.

```camp
void<Point>* getOpaquePoint(int x, int y)
{
	return new Point(x, y);
}

void processOpaquePoint(void<Point>* pt)
{
	processValues(pt.x, pt.y);
}
```

### 2.1 Where `void<V>` May Be Written

- behind a pointer, at any depth and with `const`: `void<V>*`,
  `const void<V>*`;
- as an array element: `void<V>[]`;
- as an `in` or `out` parameter: `in void<V>`, `out void<V>`, `in void<V>?`,
  `out void<V>?`;
- as an optional result: `void<V>?` (§6);
- as a generic argument behind a pointer: `List<void<int[]>*>`;
- as a `stackalloc` local: `stackalloc void<int?> value = 3;`.

It may not be written as a bare result or parameter type, or as the type of an
ordinary local, a field, an optional payload outside the positions above, a
fixed-size array element or a by-value generic argument. These rules apply to
written types, including the signatures of callable types. Substituting a
generic argument into a declaration always produces a valid type, and a
substituted result may be a bare `void<V>` (§4, §6).

### 2.2 `stackalloc` Locals

`stackalloc void<V> name = value;` declares stack storage of type `void<V>` under
the same rules as `stackalloc T name = value;` in generic code. `name` reads as
a `V`, and `&name` is a `void<V>*`. Like every `stackalloc` local, its value is
unassigned after a `yield` or `await` until it is assigned again. It is how a
program gives a `void<V>*` to a value it owns:

```camp
stackalloc void<int?> slot = 1;
methodTakingGenericPointer<int?>(&slot);  // T* is void<int?>*
```

A hidden value that must survive `yield`, `await` or a return is allocated with
`new`.

### 2.3 Conversions

- `void<P>*` and `P*` are different types that convert implicitly in both
  directions when `P` is not expandable. `void<P>[]` and `P[]` do the same.
- For an expandable `V`, `void<V>*` and the distributive `V*` are different types
  that do not convert. `&` of an ordinary expandable local, parameter or field is
  always distributive; only `void<V>` storage gives a `void<V>*`:

```camp
int? nullable = 1;
void<int?>* bad = &nullable;   // error: &nullable is the distributive int?*
stackalloc void<int?> held = 1;
void<int?>* good = &held;
```

- `void<A>*` and `void<B>*` convert under the rules for disjoint pointer
  conversions.
- A `void<V>*` follows the existing data-pointer rules: it is recovered from a
  `void*` with an explicit cast, and a pointer of another family, including an
  erased `T*`, goes through a `void*` fence:

```camp
void someGeneric<T: any>(in T arg)
{
	const void<int[]>* asArray = (const void<int[]>*)(const void*)&arg;
	int[] whole = *asArray;
	int first = (*asArray)[0];
}
```

### 2.4 Visibility And Metadata

A type `V` named in `void<V>` does not count toward the visibility of the
declaration that contains it. An exported function, field or iterator state may
hold a `void<PrivateService>*`.

API metadata shows `void<V>` to viewers that can see `V`. Other viewers see the
C form, with `void` in place of `void<V>`: a `void<P>*` is a `void*`, a
`void<P>[]` is a `void*` and a `nuint` length, an `in void<P>` is a
`const void*`, an `out void<P>` is a `void*`, and a `void<P>?` result is a
`bool` result with a trailing `void*`. Such a viewer has the contract of a C
caller: it passes pointers it already holds and is responsible for any storage
it provides.

### 2.5 Names And Overloads

`typenameof(void<V>)` is the empty string, so `void<V>` contributes no fragment
to generated symbol names:

- A `void<V>` parameter cannot be an `overload` selector, for the same reason a
  generic `T` parameter cannot.
- A method whose `this` parameter has a `void<V>` type gets no receiver type
  fragment in its symbol.

## 3. Optionals And Array Elements

### 3.1 Nested Optionals

An optional payload may be any copyable type, including an expandable one.
`int??`, `int???`, `int[]?`, `int[]??` and `IntPredicate?` are ordinary types,
valid as locals, fields, parameters and results. Nested optionals are
discouraged in declarations, but allowed.

- Fixed structs, direct class types and fixed-size arrays are not valid
  payloads; pointers to them are.
- Delegate-shape values are copyable: copying one copies its call and context,
  not its target. A copied `once` value still owes exactly one call.

Members:

- Every optional has `.specified` and `.value`.
- An optional whose payload is an array or a delegate-shape also exposes the
  payload's members directly. An `int[]? a` has `a.elements` and `a.length` and
  can be indexed and sliced; an `IntPredicate? d` has `d.call` and `d.context`
  and can be called.
- An optional of an optional reaches the inner optional through `.value`.

| Type | Camp members | ABI components |
|---|---|---|
| `int? a` | `a.specified`, `a.value` | `a`, `a_specified` |
| `int?? b` | `b.specified`, `b.value.specified`, `b.value.value` | `b`, `b_value_specified`, `b_specified` |
| `int??? c` | `c.specified`, `c.value.specified`, `c.value.value.specified`, `c.value.value.value` | `c`, `c_value_value_specified`, `c_value_specified`, `c_specified` |
| `int[]? a` | `a.specified`, `a.value`, `a.elements`, `a.length` | `a`, `a_length`, `a_specified` |
| `int[]?? b` | `b.specified`, `b.value.specified`, `b.value.value`, `b.value.elements`, `b.value.length` | `b`, `b_value_length`, `b_value_specified`, `b_specified` |
| `IntPredicate? d` | `d.specified`, `d.value`, `d.call`, `d.context` | `d`, `d_context`, `d_specified` |

The first ABI component keeps the binding name, as for every expanded form; the
others follow the Camp member path. Every ABI component name is reserved, and
declaring another symbol with one of these names is an error.

```camp
int[]? a = findNumbers();
if (a.specified)
{
	int first = a[0];
	int* allElements = a.elements;
	sum(a.value);
}

IntPredicate? d = findPredicate();
if (d.specified)
{
	bool isTrue = d(45);
	bool isStillTrue = d.call(d.context, 45);
}

int?? opt = lookup();
if (opt.specified && opt.value.specified)
	process(opt.value.value);
```

Wrapping a value in an optional applies one level at a time:

```camp
int?? both = 5;        // outer and inner present
int?? none = default;  // outer absent
int? empty = default;
int?? inner = empty;   // outer present, inner absent
```

An optional's components are separate storage locations. No program may rely on
their order or adjacency outside `void<V>`, and `&` of an optional is a
distributive pointer with one pointer per component.

### 3.2 Array Elements

An array or fixed-size array element must not be an expandable type. `int?[]`,
`char[][]`, `IntPredicate[]` and `fixed int?[4]` are errors; write
`void<int?>[]`, `void<char[]>[]` or `void<IntPredicate>[]`, and allocate local
element storage with `stackalloc`:

```camp
void<int?>[] buffer = stackalloc void<int?>[4];
buffer[0] = 7;
int? first = buffer[0];
void<int?>* firstSlot = &buffer[0];
```

## 4. Generic Substitution

- Only erased generic parameters (`T: any`, `T: copyable` and interface
  constraints) accept expandable arguments. A representation generic, including
  an unconstrained `T`, rejects them.
- An erased generic declaration lowers the same way for every type argument.
  Substitution therefore writes `void<A>` wherever `A`'s own form would lower
  differently:
  - for an expandable argument, every `T` in the signature: `T[]` becomes
    `void<A>[]`, `T*` becomes `void<A>*`, `in T` becomes `in void<A>`;
  - for any argument, a `T` or `T?` result becomes `void<A>` or `void<A>?`,
    because an erased result is returned through caller storage (§6).

  Everywhere else `T` becomes `A`: on a `List<int>`, a method returning `T[]`
  returns `int[]`. User code never has to write `void<A>` for a non-expandable
  `A`; a trailing `out A` parameter matches a `void<A>` result (§5).
- `List<int?>` stores `void<int?>` values. `List<int[]*>` stores `void<int[]*>`
  values and is a different type from `List<void<int[]>*>`. `List<void<int?>>` is
  an error that suggests `List<int?>`.
- An erased `T` is never passed by value. A parameter of erased type `T` must be
  `in T`, `out T`, `thrown T` or a pointer such as `T*`; a by-value `T`
  parameter is an error. Returning `T` remains valid.
- A generic `in T` with `T = int?` receives one read-only `void<int?>`. A
  non-generic `in int?` keeps its distributive form, one reference per
  component.

## 5. Callables

A function, method or callable value fills a callback type only when the
signatures match after substitution. No adapter bridges a difference in calling
shape; the compiler's existing adapters, such as the context it supplies when a
function is used as a delegate, are unchanged. Two rules relax matching where
both sides lower to the same C function:

- Callable types that differ only by `void<P>` against `P` in an `in`, `out`,
  pointer or array position are compatible when `P` is not expandable, because
  both pass the same pointer.
- A trailing `out void<V>` parameter matches a `void<V>` result, and a `bool`
  result with a trailing `out void<V>` parameter matches a `void<V>?` result
  (§6).

Both rules are part of callable compatibility, so they apply to callable
values, callable newtype ascription, lambdas and interface implementation
matching. Virtual overrides stay exact.

```camp
int peekErased(in void<int?> v) { return v.specified ? v.value : -1; }

read<int?>(a, peek);           // error: in int? does not match in void<int?>
read<int?>(a, v => peek(v));   // 4
read<int?>(a, peekErased);     // 4

void sevenOut(out int result) { result = 7; }

produce<int>(seven);           // error: an int result does not match void<int>
produce<int>(() => seven());   // 7
produce<int>(sevenOut);        // 7: a trailing out int matches void<int>
int value = sevenOut();        // 7, through trailing out result binding

int compareInts(in int x, in int y) { return x - y; }
List<int> numbers = new List<int>();
numbers.sort(compareInts);     // in int is compatible with in void<int>
```

A lambda with implicit parameter types takes its types from the callback, so
it adapts without further declarations:

```camp
List<int?> optionals = new List<int?>();
optionals.sort((x, y) => compareOptionals(x, y));
```

A method reference to an erased generic has the substituted signature. A lambda
gives it a plain result when one is wanted:

```camp
auto get = numbers.get;                                        // delegate void<int>(nuint index)
delegate void(nuint index, out int result) written = numbers.get;  // the same shape
delegate int(nuint index) getter = index => numbers.get(index);    // a plain int result

auto lookup = optionals.getItemOrDefault;  // delegate void<int?>?(int index)
int?? item = lookup(300);
```

The diagnostic for a mismatch suggests a lambda, a trailing `out` parameter or
a `void<V>` parameter.

## 6. Results

An erased generic returns a `T` through storage the caller provides, so a
substituted result may be a bare `void<V>`. In C the function returns `void`
and takes a trailing writable `void*`. A `void<V>?` result returns `bool` for
`specified` and writes the payload through the same pointer. The pointer
occupies the position a trailing `out` parameter would: after the source
parameters and before compiler-supplied slots such as `within`, `thrown`,
`sizeof`, `typenameof` and `vtableof`, as for a return-position `prep` array.
When the result is not specified, the payload storage holds the default value of `V`, as for any
`out` parameter.

A declaration does not write a bare `void<V>` result. It writes a trailing `out`
parameter, which lowers the same way, or a `void<V>?` result:

```camp
void getPoint(out void<Point> result);           // void getPoint(void* result);
void<int?>? find(int index);                     // bool find(int index, void* result);
bool tryFind(int index, out void<int?> result);  // bool tryFind(int index, void* result);
void<Point>* getOpaquePoint();                   // void* getOpaquePoint(void);
```

The caller must know the size of `V` to provide the storage, just as an erased
generic needs `sizeof(T)` to return a `T`. `V` stays hidden in C: the declaring
code must give a C caller the size some other way, and that caller allocates
the storage. When the callee should allocate instead, the result is a
`void<V>*`.

A call whose result is `void<V>` or `void<V>?` produces a `V` or a `V?`.

## 7. `in` And `out` Arguments

`in void<V>` and `out void<V>` take one pointer to `void<V>` storage. The caller
decides what that storage is:

- `void<V>` storage, such as a `stackalloc void<V>` local, an element of a
  `void<V>[]` or a dereferenced `void<V>*`, is passed directly. An `out` write is
  visible immediately.
- An ordinary expandable value, such as an `int?` local, is passed through a
  temporary. An `out` result reaches the value when the call returns.
- An argument of a non-expandable type is always passed directly.

The callee cannot tell which happened and must not depend on it. An `in`
argument cannot be written, so for `in` the difference is visible only if the
original changes through another path during the call.

## 8. Inference

A `void<V>` value is never named, so inference never produces one at the top
level, and it keeps one inside arrays and pointers:

```camp
List<int?> items = new List<int?>();
items.add(1);
items.add(default);                         // { 0, false }
auto first = items.Item[0];                 // int?
auto section = items.take(1, 4);            // void<int?>[]
auto maybe = items.getItemOrDefault(300);   // int??
```

Nested generic results keep their shape:

```camp
T? makeOptional<T>(T value) => value;
T? makeInnerOptional<T: copyable>(in T value, sizeof(T)) => value;

auto result = makeInnerOptional(makeInnerOptional(makeOptional(5)));
// int???: result.specified, result.value.specified,
// result.value.value.specified, result.value.value.value
```

`makeOptional<int?>` is invalid: its unconstrained `T` is a representation
generic.

## 9. Generated State

A `struct iter` exposes its retained state, so an exported struct iterator
follows the same visibility rules as an exported struct. `void<V>*` is how that
state holds pointers to private types:

```camp
export struct iter int getValues()
{
	void<PrivateService>* service = acquirePrivateService();
	void<PrivateValue>* value = new PrivateValue();
	while (service.hasMore(value))
		yield service.currentValue();
	delete value;
}
```

In C the state holds two `void*` fields, and Camp viewers that cannot see the
private types see the same `void*` fields. A `stackalloc` value, including a
`stackalloc void<V>` local, is unassigned after `yield` or `await`, so it never
carries state across a suspension.

## 10. ABI

| Camp | C |
|---|---|
| `void<V>*`, `out void<V>` | `void*` |
| `const void<V>*`, `in void<V>` | `const void*` |
| `void<V>[]` | `void*` and `nuint` |
| substituted `void<V>` result, or a trailing `out void<V>` | `void` result and a trailing `void*` |
| `void<V>?` result, or a `bool` result and a trailing `out void<V>` | `bool` result and a trailing `void*` |
| nested optional | one component per leaf, named as in §3.1 |

Erased generic declarations lower the same way for every type argument. Inside
generic arguments, erased types take part in Camp type identity; a field of
type `List<void<int[]>*>*` is exported as a pointer to `List`.

## Diagnostics

| Condition | Outcome |
|---|---|
| `struct(V)` written | Error suggesting `void<V>` |
| Expandable array or fixed-array element (`int?[]`, `char[][]`, `fixed int?[4]`) | Error suggesting `void<V>[]` |
| Optional payload that is not copyable | Error suggesting a pointer |
| `void<V>` stored by value: ordinary local, by-value parameter, field, optional payload, fixed-array element, by-value generic argument | Error suggesting `V`, `void<V>*` or a `stackalloc` local |
| Bare `void<V>` result written in a declaration or callable type | Error suggesting a trailing `out void<V>` parameter, or `out V` when `V` is not expandable |
| `void<void>`, `void<void<V>>` | Error |
| `&` of an expandable value where a `void<V>*` is required | Error suggesting a `stackalloc void<V>` local |
| `void<V>` parameter declared as an `overload` selector | Error: `void<V>` contributes no symbol type fragment |
| Expandable argument to a representation generic | Error suggesting `any` or `copyable` |
| By-value parameter of erased type `T` | Error suggesting `in T` |
| Callable signature mismatch, including `V` against `void<V>` | Error suggesting a lambda, a trailing `out` parameter or a `void<V>` parameter |

## Compatibility

- `struct(V)` is respelled `void<V>`. Programs that wrote it could rarely build.
- `int[]?`, `delegate int()?`, `once int()?` and other optionals of expandable
  payloads keep compiling and become usable.
- Non-copyable optional payloads and by-value erased `T` parameters become
  errors; neither could be compiled correctly.
- Passing a function whose calling shape differs from a substituted generic
  callback becomes an error; such calls produced wrong results or failed to
  build. Callbacks that differ only in `in`, `out`, pointer or array positions
  of non-expandable types keep working. A callback for a `T` result is declared
  with a trailing `out` parameter or written as a lambda.
- No working program changes meaning.

## Acceptance Criteria

- Every rejection in the Diagnostics table is reported, and each accepted form
  above compiles and runs.
- `void<V>` works for expandable and non-expandable `V` behind pointers, as array
  elements, in `in` and `out` parameters, as `void<V>?` results, as
  `stackalloc` locals and inside generic arguments, with the members of `V`.
  A bare `void<V>` result is rejected wherever it is written and produced only
  by substitution.
- Nested optionals (`int??`, `int???`, `int[]?`, `int[]??`, `IntPredicate?`) work
  as locals, fields, parameters and results with the Camp members and ABI
  component names of §3.1, including `.value`, indexing and calls on optional
  arrays and delegates.
- Inference follows §8: `items.Item[0]` is `int?`, `items.take(1, 4)` is
  `void<int?>[]`, `items.getItemOrDefault(300)` is `int??`, and the nested
  `makeInnerOptional` result exposes all four members.
- Every erased generic declaration lowers the same way for every type argument.
  `read<int?>(a, peek)` and `produce<int>(seven)` are rejected. The lambda forms,
  `peekErased` and `sevenOut` print 4 and 7, and `produce<Point>` with a
  callback declared `void make(out Point result)` returns the point.
- A callback declared with `in int` fills an `in T` parameter when `T` is `int`.
- A trailing `out` parameter matches a `void<V>` result and a `bool` result with
  a trailing `out` matches a `void<V>?` result, both as callbacks and as
  method-reference types. `int value = sevenOut();` binds the trailing `out`.
- An unspecified `void<V>?` result leaves the default value of `V` in the
  payload storage.
- An `out` argument passed as an ordinary `int?` is written when the call
  returns; one passed as `stackalloc void<int?>` storage is written immediately.
- `void getPoint(out void<Point> result)` lowers to
  `void getPoint(void* result)`, `void<int?>? find(int index)` to
  `bool find(int index, void* result)`, and `void<Point>* getOpaquePoint()` to
  `void* getOpaquePoint(void)`.
- Exported declarations and exported `struct iter` state can hold `void<P>*` for
  a private `P`, and metadata shows the C form (`void*`, `const void*`, `void*`
  and `nuint`) to viewers that cannot see `P`.
- `typenameof(void<V>)` is empty, a `void<V>` receiver adds no type fragment to
  a method symbol, and a `void<V>` overload selector is rejected.
- The trailing pointer of a `void<V>` or `void<V>?` result precedes
  compiler-supplied slots such as `sizeof(T)`.

## Open Questions

None.
