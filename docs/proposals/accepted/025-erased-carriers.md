# Erased Carriers And Simpler Optionals

## Status

Accepted and implemented (DEV-018).

## Proposal Date

2026-10-10

## Last Updated Date

2026-10-10

## Summary

This proposal revises the erased type rules of proposal 024. It narrows what an
optional can hold, generalizes `void<V>` into a family of erased carriers, and
gives every callable produced from a generic declaration one honest signature.

- **Optionals hold one level.** An optional payload is a non-erased value
  type, an array or a delegate shape. Nested optionals (`int??`), erased
  optionals (`T?`, `void<V>?`) and optionals of carriers are removed.
- **Integer carriers.** `int<V>`, `long<V>`, `nint<V>` and the other integer
  carriers name a value of type `V` that crosses the ABI as the carrier
  integer. A `long<Color>` is to `T: long` what `void<V>` is to `T: any`: the
  same erasure, without a type parameter.
- **Erased results are written directly.** A `void<V>` result is a valid
  declaration and callable result. It returns through caller storage, as an
  erased generic `T` result already does. The rule that let a trailing `out`
  parameter stand in for a result is removed.
- **Callables from generic declarations use erasure substitution.** A method
  reference, a `fn` value or a callable type inside a substituted signature
  replaces each type parameter with its erased form: `void<A>` for `any` and
  `copyable`, the constraint's carrier `C<A>` for a representation generic, and
  `A` itself for an interface constraint.
- **Conversions see through carriers.** Before a conversion, receiver, overload
  or initializer is checked, top-level carriers and non-expandable `void<V>`
  outside callable results are stripped, and the remaining carriers are treated
  as newtypes of their carrier. The existing conversion rules then apply.
- **`stackalloc` locals are for unspecified-size types only.** `auto` never
  infers one.
- **Annotated types have names but no symbols.** `typenameof(void<V>)` is the
  name of `V`. A `void<V>` or carrier type can never select an overload or be a
  method's receiver type.

## Motivation

Proposal 024 made erased types and nested optionals coherent, but four parts of
it are harder to use than they should be.

**Nested optionals are complicated, mostly at the ABI.** `int??` and `int???`
each add a presence component, with derived names (`b_value_specified`,
`c_value_value_specified`) that every C consumer must learn and that reserve
identifiers in the declaring scope. Generic `T?` with an optional argument
produces them by substitution, so `List<int?>.getItemOrDefault` returns an
`int??`. Few programs want an optional of an optional; many programs pay for
the possibility.

**A trailing `out` parameter can be read as a result.** Under 024 a callback
type with a `void<V>` result accepts a function whose last parameter is
`out V`. The rule is broad: wherever a result and a trailing `out` meet in
callable matching, the language has to consider both shapes. A plain mismatch
between two callable types can then be reported as the wrong one of two
problems, or not at all.

**Method references to generic methods have surprising types.** Under 024,
`void<A>` appears in a substituted signature only where `A`'s own form would
lower differently, so the type of the same method reference depends on whether
`A` is expandable. `List<int>.setItem` and `List<int?>.setItem` have different
looking signatures although they are one C function.

**A representation generic has no correct callable type.** Given

```camp
enum Color: int
{
	RED,
	GREEN
}

struct Slot<T: long>
{
	T value;

	T get() => this.value;
}

Slot<Color> slot;
auto get = slot.get;
```

`get` returns a `long` in C. Its only available Camp type, `delegate Color()`,
describes a function returning a 32-bit value. There is no way to write the
real signature.

One shortcoming in current implementations is also addressed:
`T: implements I` does not imply `copyable`, yet a declaration that returns a
plain `T` or writes an `out T` under that constraint is accepted, and then
fails to build.

## Goals

- Every optional has at most one presence component.
- Every type parameter's erasure can be named in source, including for
  representation generics.
- A callable produced from a generic declaration has the type of the function
  it calls, independent of the shape of the type argument.
- Callable matching never treats a parameter as a result.
- Every erased type has a meaningful `typenameof` and never produces a
  misleading symbol name.

## Non-Goals

- No change to the default constraint of an unconstrained type parameter.
- No `untyped<V>` carrier. `untyped` is not a constraint, so it has no
  generic counterpart to generalize.
- No change to how direct calls read their results: a call to a generic still
  produces an `A`.
- No new adapters. A lambda remains the way to change calling shape.
- No layout guarantee for an expandable value outside `void<V>`.

## 1. Optional Payloads

### Before

Any copyable type, including an optional, an erased type or a type parameter,
could be an optional payload. `int??`, `int[]??`, `T?`, `void<V>?`,
`in void<V>?` and `out void<V>?` were valid.

### After

An optional payload must be one of:

- a non-erased, non-expandable copyable type: `int?`, `Color?`, `bool?`;
- an array, including an array of erased elements: `int[]?`, `T[]?`,
  `void<int?>[]?`;
- a delegate shape: `IntPredicate?`, `delegate int()?`.

A payload cannot be an optional, a type parameter, `void<V>` or a carrier.
`int??`, `T?`, `void<V>?` and `long<Color>?` are errors wherever they are
written.

An optional of an array has three components, each of specified size:
`T[]?` is the elements pointer, the `nuint` length and the `bool` presence
flag. The array's element type does not change the optional's shape.

An optional type remains a valid type argument. `List<int?>` and
`List<int[]?>` are allowed; their items are stored as `void<int?>` and
`void<int[]?>` storage, the struct-layout form of the optional's components.

Members and ABI components are those of 024 for the remaining shapes:

| Type | Camp members | ABI components |
|---|---|---|
| `int? a` | `a.specified`, `a.value` | `a`, `a_specified` |
| `int[]? a` | `a.specified`, `a.value`, `a.elements`, `a.length` | `a`, `a_length`, `a_specified` |
| `IntPredicate? d` | `d.specified`, `d.value`, `d.call`, `d.context` | `d`, `d_context`, `d_specified` |

Because a generic declaration cannot write `T?`, substitution can never produce
a nested optional.

### Replacing Erased Optionals

A method that returned `T?` returns `bool` and writes the value through a
trailing `out` parameter:

```camp
public bool tryGetItem(int index, out T result)
{
	if (index < 0 || (nuint)index >= this.length)
		return false;
	result = this.items[(nuint)index];
	return true;
}

List<int?> items = new List<int?>();
int? found;
if (items.tryGetItem(3, out found))
	use(found);
```

A parameter that accepted `T?` or `in void<V>?` takes a nullable `T*` or
`void<V>*`, or is redesigned around a `bool`.

## 2. Integer Carriers

### Before

`void<V>` was the only annotated erased type. A value of a type parameter
`T: long` crossed the ABI as `long`, but source could name it only as `T`
inside the generic, or as the type argument outside it.

### After

For each integer representation constraint `C` (`byte`, `ushort`, `short`,
`uint`, `int`, `ulong`, `long`, `nuint`, `nint`), `C<V>` is a carrier type: a
value of type `V` carried as a `C`. It has the semantics a value of a type
parameter `T: C` bound to `V` has.

- `V` must be a valid type argument for `T: C`: an integer type of equal or
  smaller width, an enum or value newtype with a compatible underlying type,
  or, for `nint` and `nuint`, a pointer type. `V` cannot be expandable, `void`,
  `void<W>` or another carrier.
- A carrier has a specified size. Unlike `void<V>`, it is stored by value: it
  may be a local, field, by-value parameter, result, array element, pointer
  target or generic argument.
- A carrier is never an optional payload (§1), an `overload` selector or a
  receiver type (§6), and never a `stackalloc` local (§5).
- A carrier value reads as `V`. Inference never produces a top-level carrier:
  `auto c = slot.value` is a `Color`.
- `typenameof(C<V>)` is the name of `V` (§6).

Conversions involving carriers are classified through the conversion view of
§7. In particular, a carrier value converts as its `V` does, and a carrier
behind a pointer or array converts as a newtype whose underlying type is the
carrier.

`List<long<Color>>`, `List<Color>` and `List<long>` are three different types.

### Visibility And Metadata

As for `void<V>`, a type `V` named in `C<V>` does not count toward the
visibility of the declaration containing it. Metadata shows `C<V>` to viewers
that can see `V` and the plain carrier `C` to viewers that cannot:

```camp
export struct Handle
{
	long<PrivateKind> kind;   // a long to viewers that cannot see PrivateKind
}
```

## 3. Erased Results

### Before

A written bare `void<V>` result was an error. A declaration wrote a trailing
`out void<V>` parameter instead, and that parameter matched a `void<V>` result
in callable matching. An erased optional result `void<V>?` returned `bool` and
wrote its payload through a trailing pointer.

### After

A result of unspecified size, meaning an erased generic `T` (`any`,
`copyable`) or a `void<V>`, returns through caller storage. In C the function
returns `void` and takes a trailing writable `void*`, placed after the source
parameters and before compiler-supplied slots, as in 024 §6. This applies to
declarations, callable types and method references alike.

A bare `void<V>` result may now be written:

```camp
void<int> seven() { return 7; }   // C: void seven(void* result)
void<Point> origin()              // C: void origin(void* result)
{
	Point p = { 0, 0 };
	return p;
}

int x = seven();                  // a call reads as V
```

`return` writes the value into the caller's storage. The caller must know the
size of `V` to provide that storage, as before.

A bare `void<V>` **parameter** remains an error; it needs `*`, `[]`, `in` or
`out`.

The matching rule between a trailing `out void<V>` and a `void<V>` result is
removed. `void sevenOut(out int result)` and `void<int> seven()` have the same
C signature but are different Camp callable types. Trailing `out` result
binding at a call site, such as `int value = sevenOut();`, is unchanged; it is
a call form, not a callable-matching rule.

`void<V>?` results are removed (§1).

## 4. Callables From Generic Declarations

### Before

Substitution wrote `void<A>` only where `A`'s own form would lower
differently: everywhere for an expandable `A`, and only in results for a
non-expandable `A`. Representation generics substituted `A` directly.

### After

**Erasure substitution** replaces each type parameter by its erased form:

| Constraint | `T` becomes |
|---|---|
| `any`, `copyable` | `void<A>` |
| representation constraint `C`, including the default constraint of an unconstrained parameter | `C<A>` |
| `implements I` | `A` |

Erasure substitution applies wherever a callable type is formed from a
generic declaration:

- a method reference or a `fn` value of a generic method, or of a method of a
  generic type;
- a callable type written inside a generic signature, such as the comparer
  parameter of `sort`, once the type arguments are known.

```camp
List<int> numbers = new List<int>();

auto get = numbers.getItem;   // delegate void<int>(nuint at)
auto set = numbers.setItem;   // delegate void(nuint at, in void<int> value)

Slot<Color> slot;
auto read = slot.get;         // delegate long<Color>()
```

The result is the same for every type argument: a method reference has the
same type shape for `List<int>` as for `List<int?>`.

Direct calls are unchanged. A call to a generic reads its result as `A`, and
non-callable parameter types accept `A`.

A type parameter under `implements I` substitutes to `A` because nothing is
erased: no expandable type implements an interface, and such a `T` is only
ever passed by pointer (§4.1).

### Callable Compatibility

Callable types are compared in the conversion view (§7). This applies to
callable values, callable newtype ascription, lambdas and interface
implementation matching; virtual overrides stay exact.

The 024 rule that `void<P>` and `P` are compatible in `in`, `out`, pointer and
array positions for a non-expandable `P` follows from the view and is no longer
a separate rule. A `void<V>` result is never stripped, and a carrier inside a
signature is nominal, so those differences remain mismatches:

```camp
int compareInts(in int x, in int y) { return x - y; }
numbers.sort(compareInts);                      // in int matches in void<int>

delegate void(nuint at, in int value) put = numbers.setItem;   // compatible
delegate int(nuint at) plain = at => numbers.getItem(at);      // a lambda for a plain result

delegate Color() wrong = slot.get;              // error: long<Color> result is not Color
delegate Color() wanted = () => slot.get();     // a lambda adapts
```

### 4.1 `T: implements`

An interface constraint does not imply `copyable`. Its type parameter is
transported only by pointer:

- `T*`, `const T*`, `T[]` and `out T*` are valid.
- A `T` result, an `in T` parameter and an `out T` parameter are errors.
- A local `T x` and a `stackalloc T x` are errors (§5).

## 5. `stackalloc` Locals

### Before

`stackalloc T` and `stackalloc void<V>` locals existed, but which local types
`stackalloc` accepted, and how its storage was allocated, were not stated as
one rule.

### After

A `stackalloc` local gives local-variable syntax to a value of unspecified
size. Its type must be one of:

- an erased type parameter (`any` or `copyable`), with `sizeof(T)` in scope;
- a `void<V>`.

Any other type is an error: `stackalloc int x`, `stackalloc Point p`,
`stackalloc long<Color> c` and `stackalloc T x` for a representation `T`. Such
values are ordinary locals. A `T: implements I` is pointer-only (§4.1) and has no
local of either kind: `T x` and `stackalloc T x` are errors that suggest `T*`.
`void<V>` is valid for a `V` that has a specified size or is expandable, never
for an erased type parameter, which is already unspecified-size storage: `void<T>`
is an error. `stackalloc` allocation expressions
(`stackalloc int[4]`, `stackalloc Point()`) are unaffected.

Storage for every `stackalloc` local is allocated once on entry to the
function, even when the local is declared inside a loop; for an iterator or
async body, entry includes each resumption. That is why a `stackalloc` local is
unassigned after `yield` or `await` until it is assigned again.

`auto` never infers a `stackalloc` local:

```camp
List<int?> numbers = new List<int?>();
numbers.add(42);

stackalloc void<int?> a = 42;
stackalloc void<int?> b = (void<int?>)42;
stackalloc void<int?> c = numbers.Item[0];
auto d = (void<int?>)42;          // error: auto cannot infer a stackalloc local

int? e = 42;
auto f = (int?)42;                // int?
auto g = numbers.Item[0];         // int?
```

An explicit cast to an erased type is valid wherever the implicit conversion is:
in a `stackalloc` initializer, as an `in void<V>` argument, or when writing
into `void<V>` storage. `(void<int?>)42` is the explicit spelling of the
conversion `42` already receives in those places.

## 6. Type Names, Selectors And Receivers

### Before

`typenameof(void<V>)` was the empty string. A `void<V>` parameter could not be
an `overload` selector. A method's `this` parameter could have a `void<V>`
type and then added no receiver fragment to the method's symbol.

### After

`typenameof` of an annotated type is the name of the annotated type. Outer
forms apply as they do to `V`:

```camp
typenameof(void<User>*)       // "User"
typenameof(nint<ColorType>)   // "ColorType"
typenameof(int)               // "Int"
typenameof(List<int[]>)       // "List"
typenameof(void)              // "Void"
```

The receiver of a `typenameof` capability already knows the carrier from its
constraint; the annotation is the part it cannot know.

An annotated erased type, either `void<V>` or a carrier, can never be an
`overload` selector or the type of a method's `this` parameter. No symbol name
would be correct:

```camp
void add(overload void<PrivateUser>* item);   // error
void initialize(void<PrivateUser>* this);     // error
void invert(int<PrivateColorType> this);      // error
```

`addVoid` is opaque to every caller, `addPrivateUser` names a type the caller
may not see, and `add` hides that the method is an overload. Such a method is
written as an ordinary function or method.

Calling methods of `V` through a `void<V>*` is unaffected; the receiver there
is `V*`.

## 7. Conversion View

### Before

024 defined conversions for `void<V>` one case at a time: `void<P>*` and `P*`
converted implicitly for a non-expandable `P`, `void<V>*` and the distributive
`V*` did not convert for an expandable `V`, and callable matching carried its
own `void<P>` against `P` relaxation.

### After

Before a conversion is classified, both types are rewritten into their
**conversion view**. *Stripping* a carrier erases it and leaves only the
carried type.

1. If the type itself is a non-void carrier, not behind `*` or `[]`, the
   carrier is stripped: `long<Color>` becomes `Color`. This applies only at the
   top of the type, never to a by-value position inside a callable signature.
2. The remaining type is rewritten recursively. A `void<V>` whose `V` is not
   expandable is stripped, except in a callable result position at any depth.
   This includes `fn` and `delegate` types and callable types nested inside
   other signatures.
3. Any remaining non-void carrier is treated as a newtype whose underlying type
   is the carrier: `long<byte>*` is classified as a pointer to a
   `newtype LongCarrierOfByte: long`.

The existing conversion rules then classify the rewritten types. The view is
used for:

- conversion classification: implicit, explicit, unsafe, fence-required or
  forbidden;
- callable compatibility (§4);
- receiver eligibility. A value is an eligible receiver for a method declared
  on a type it converts to implicitly;
- overload selection;
- initializer and literal conversions.

The view does not change type identity. Symbols, metadata, generic type
identity and `typenameof` use the written types.

Under the view, `Widget*`, `void<Widget>*` and `void<Widget*>` are the same type
for conversion purposes.

| Conversion | View | Result |
|---|---|---|
| `void<Button>*` → `void<Widget*>` | `Button*` → `Widget*` | implicit |
| `void<Button>*` → `Button*` | `Button*` → `Button*` | implicit |
| `uint<short>` → `ushort` | `short` → `ushort` | explicit |
| `long<ColorEnum>` → `BackgroundEnum` | `ColorEnum` → `BackgroundEnum` | explicit |
| `nint<Widget*>` → `Button*` | `Widget*` → `Button*` | unsafe explicit |
| `fn void<int>()` → `fn int()` | unchanged: result positions are not stripped | `fn*` fence |
| `long<Color>*` → `Color*` or `long*` | `LongCarrierOfColor*` → `Color*` or `long*` | explicit, as other value-newtype pointers |
| `fn void(long<Color>)` → `fn void(Color)` | `LongCarrierOfColor` against `Color` | `fn*` fence |

Initializers and literals are checked against the carried type:
`int<byte> b = 300;` is rejected as out of range for `byte`, and
`long<Color> c = RED;` finds `RED` through the target type `Color`.

Overload selection uses the view: a `long<Color>` argument selects the `Color`
member of an overload family, and a `void<Button>*` argument selects the
`Button*` member.

Generic arguments are rewritten too, so `List<void<int>*>*` and `List<int*>*`
convert implicitly. They remain different types.

### Expandable `void<V>`

A `void<V>` whose `V` is expandable is not stripped. It converts with `V` by
reading and writing its storage:

| Conversion | Result |
|---|---|
| `void<int[]?>` → `int[]?` | implicit (read) |
| `int[]?` → `void<int[]?>` | implicit (write into storage) |
| `void<int[]?>*` → `int[]?*` | implicit expansion |
| `&` of an `int[]?` → `void<int[]?>*` | forbidden |

**Implicit expansion is new.** 024 did not let `void<V>*` and the distributive
`V*` convert in either direction. Now any `void<V>*` value expands implicitly to
the distributive pointer, with one pointer per component, each addressing that
component inside the one storage object. The reverse remains forbidden: the
components of an ordinary expandable value are separate storage locations.

The `int[]?` → `void<int[]?>` direction makes an `int[]?` an eligible receiver
for methods declared on `void<int[]?>`. Since no method can declare a `void<V>`
receiver (§6), this has no effect.

## 8. ABI

| Camp | C |
|---|---|
| `C<V>` | `C` |
| `C<V>*`, `C<V>[]` | `C*`, `C*` and `nuint` |
| `void<V>` result, erased `T` result | `void` result and a trailing `void*` |
| optional of a non-expandable payload | value and `bool` |
| optional of an array | elements, `nuint` length and `bool` |
| optional of a delegate shape | call, context and `bool` |

The rows of 024 for `void<V>?` results and for nested optionals are removed.
All other rows of 024 §10 are unchanged.

## Diagnostics

| Condition | Outcome |
|---|---|
| Optional of an optional (`int??`) | Error suggesting a `bool` and an `out` parameter, or a `void<V>*` |
| Optional of a type parameter, `void<V>` or a carrier (`T?`, `void<V>?`, `long<Color>?`) | Error suggesting a `tryGet` form with `out T`, or a nullable pointer |
| `in void<V>?`, `out void<V>?` | Error suggesting a nullable `void<V>*` |
| Carrier with an invalid `V` (`int<long>`, `int<int?>`, `nint<float>`, `int<void<V>>`) | Error naming the constraint `T: C` that `V` fails |
| Bare `void<V>` parameter | Error suggesting `in`, `out`, `*` or `[]` |
| Callable mismatch between a `void<V>` result and a trailing `out V` | Error suggesting a lambda or a `void<V>` result |
| `T` result, `in T` or `out T` under `T: implements I` | Error suggesting `T*` or `out T*` |
| `stackalloc` local of a specified-size type | Error suggesting an ordinary local |
| `T x` or `stackalloc T x` under `T: implements I` | Error suggesting `T*` |
| `void<T>` for an erased type parameter `T` | Error suggesting `T` or `T*` |
| `auto` local that would infer a `void<V>` or erased `T` | Error suggesting `stackalloc` with an explicit type |
| `void<V>` or carrier `overload` selector | Error: annotated erased types cannot select an overload |
| `void<V>` or carrier `this` parameter | Error: annotated erased types cannot be a receiver type |

## Compatibility

- `int??`, `int[]??` and every other nested optional become errors.
- `T?`, `void<V>?`, `in void<V>?` and `out void<V>?` become errors. In the
  standard library, `List.getItemOrDefault` is replaced by `List.tryGetItem`.
- Callbacks written as a function with a trailing `out` parameter no longer
  fill a callable type with a `void<V>` result. They are rewritten with a
  `void<V>` result or passed through a lambda.
- The inferred type of a method reference to a generic method changes where a
  non-expandable `A` used to appear plainly: `auto set = numbers.setItem` is now
  `delegate void(nuint at, in void<int> value)`. Explicitly typed delegates
  keep compiling, because the conversion view strips the non-expandable
  `void<int>`.
- Method references to representation generics change from `A` to `C<A>`.
  These had no correct type before.
- `typenameof(void<V>)` changes from the empty string to the name of `V`.
- A method declaring a `void<V>` `this` parameter becomes an error.
- `T` results and `out T` parameters under `T: implements` become errors. They
  could not be built.
- `stackalloc` locals of specified-size types become errors.
- A `void<V>*` of an expandable `V` now converts implicitly to the distributive
  `V*`. This only accepts programs that were rejected before.

## Acceptance Criteria

- Every rejection in the Diagnostics table is reported.
- `int?`, `int[]?`, `T[]?`, `void<int?>[]?` and `IntPredicate?` work as locals,
  fields, parameters and results with the members and ABI components of §1.
  `List<int?>` and `List<int[]?>` store and return items.
- No declaration, substitution or inference produces a nested optional.
- `List.tryGetItem` replaces `List.getItemOrDefault` and reports absence through
  its `bool` result.
- Every integer carrier is accepted for each valid `V` of its constraint, is
  stored by value, reads as `V`, and lowers to its carrier.
  Viewers that cannot see `V` see the plain carrier in metadata.
- `void<int> seven()` lowers to `void seven(void* result)`, its call reads as
  `int`, and it fills `delegate void<int>()`. `void sevenOut(out int result)`
  does not fill it; `int value = sevenOut();` still binds.
- Method references to `List<int>` and `List<int?>` methods have the
  erasure-substituted types of §4. `auto read = slot.get` on a `Slot<Color>` is
  `delegate long<Color>()` and returns the stored value.
- `numbers.sort(compareInts)` with `compareInts(in int, in int)` still compiles.
- Under `T: implements I`, a `T` result and an `out T` parameter are rejected,
  and `out T*` is accepted.
- `stackalloc` locals are accepted only for erased type parameters and
  `void<V>`, are allocated once per function entry, and are unassigned after
  `yield` and `await`. `auto d = (void<int?>)42;` is rejected;
  `stackalloc void<int?> b = (void<int?>)42;` is accepted.
- Every conversion in the §7 tables is classified as listed, as an assignment,
  a cast, a callable match, a receiver and an overload argument where each
  applies. `int<byte> b = 300;` is rejected and `long<Color> c = RED;` is
  accepted. Symbols and metadata still show the written types.
- `typenameof` returns the values in §6. `void<V>` and carrier types are
  rejected as `overload` selectors and as `this` parameter types.

## Open Questions

None.
