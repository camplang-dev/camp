# Proper Lifetime Annotations And Lifetime Analysis

This document is an implementation plan for Camp lifetime annotations and
lifetime analysis. It is based on the current language spec
(`camp_unified_spec_v17.md`) and the clarifications made during design
discussion.

The goal is to implement the source-language lifetime model described by the
spec, not to add a separate ownership system. Lifetime checking is an escape
analysis and relationship-proving system for pointer-bearing values.

## Summary Of The Language Model

Camp lifetime annotations describe how long pointer-bearing values remain valid
relative to call arguments, receivers, local scopes, escaped storage, delegate
contexts, iterator frames, and future async frames.

The important question is:

> What values are known to outlive what other values?

The annotations are:

- `scoped`
- `unscoped`
- `escaped`

These annotations apply to raw pointers and to pointer-bearing aggregate or
context values, including:

- arrays and fixed-size arrays whose elements contain pointers;
- strings and character spans;
- structs/classes with pointer-bearing fields;
- materialized params values;
- delegates and delegate-like callable values;
- iterator frames;
- future async frames;
- optionals whose payload contains pointers;
- erased generic values after substitution.

Lifetime annotations are intentionally sparse in source. The compiler should
infer default lifetime relationships. The standard library and user code should
not write annotations merely to restate defaults.

Lifetime annotations are valid in only a small number of source positions:

- method/function signatures, including return types, parameters, receiver
  parameters, callable type references, and callable newtype signatures;
- explicit casts;
- `escaped class` and `escaped interface` declarations.

They do not precede fields, global variables, local variables, or ordinary type
uses. For example, a parameter may be declared `scoped const char[] value`, but a
local variable should not be declared `scoped const char[] value`. The local
slot and current-value facts are inferred by flow analysis instead.

Future non-copyable generic constraint arguments may allow lifetime annotation
syntax, but those rules are not settled. The parser may recognize the pattern
for good diagnostics, but the compiler should reject it until the language
rules are designed.

## Spec Semantics To Preserve

### `escaped`

`escaped` is a storage/context fact. It means the value is not in stack storage.

It does not mean:

- process-global;
- thread-safe;
- allocator-independent;
- owned by the caller;
- forever valid.

Examples:

```camp
escaped Widget* createWidget();
void retain(escaped char[] text);
void register(escaped delegate bool(int) predicate);
escaped class Window;
escaped interface IService;
```

Pointer-form `delete` is validated through the callable it lowers to. If the
visible `free` function or allocator `free` method requires an escaped pointer,
then the delete target must satisfy that signature:

```camp
extern void free(escaped void* ptr);

Widget local = init Widget();
Widget* ptr = &local;
delete ptr; // ERROR: cannot satisfy free's escaped pointer parameter
```

Likewise, `new` does not get a baked-in lifetime merely because it is `new`.
The lifetime fact should come from the allocation function or allocator method
that is actually called after lowering. Until the call-site relation stage can
substitute those allocator signatures precisely, the compiler should treat some
allocation results as unknown rather than guessing.

### `scoped`

`scoped` is the default for ordinary parameters, receivers, and pointer-bearing
contents of aggregate/context parameters.

Inside the callee, a scoped value may be used during the call, but may not be
stored into caller-context or escaped storage. Since `this` is caller-context,
storing a scoped argument into a field of `this` is invalid unless another rule
removes the restriction.

```camp
class Holder
{
    const char[] text;

    void setText(const char[] value)
    {
        this.text = value; // ERROR: scoped value retained by this
    }
}
```

At the call site, any lifetime may be supplied to a scoped parameter if ordinary
type conversion succeeds. This is the low-friction borrowing case.

### `unscoped`

`unscoped` removes the ordinary scoped restriction.

An unscoped argument without an explicit anchor means:

> The argument lifetime must be equal to or longer than all scoped arguments.

In an instance method, `this` is considered scoped by default from the callee's
point of view. Therefore, bare `unscoped` on an ordinary instance-method
argument typically means a lifetime of at least `unscoped(this)`.

For example, this parameter may be stored into `this`:

```camp
class Holder
{
    const char[] text;

    void setText(unscoped const char[] value)
    {
        this.text = value; // OK
    }
}
```

The caller is responsible for proving that `value` outlives the receiver. The
callee does not need to know whether `value` is escaped, local, or tied to some
other object.

Anchored unscoped forms narrow the relationship:

```camp
void setText(unscoped(this) const char[] value)
```

This states the relevant relation directly. However, source code should not
write `unscoped(this)` merely to restate the default relationship for an
ordinary instance method.

### Return Defaults

Return values and `out` parameters are relationship templates, not final
call-site facts.

For an instance method, an unannotated pointer or pointer-bearing return value is
assumed to be `unscoped(this)`.

For a static method or free function, an unannotated pointer or pointer-bearing
return value is anchorless `unscoped`, meaning it is at least as long-lived as
all scoped arguments.

`unscoped` may still be written on returns, especially when an explicit anchor
is useful:

```camp
unscoped(buffer) char[] fill(char[] buffer, const char[] value)
```

`scoped` on a return describes a relation to caller-provided input. It does not
mean the result is necessarily short-lived at the call site.

```camp
scoped char[] trim(char[] value);
```

If the caller passes an escaped `value`, the result is known to be escaped. If
the caller passes a local fixed-array span, the result is tied to that local
storage.

The same principle applies to constness where provable. If a function has a
`scoped const` input and `scoped const` output but the caller passes a provably
mutable value, the result may retain its mutable fact when the relationship is
precise enough. For pointer-bearing aggregate values, this may not always be
provable.

`out` parameters follow the same lifetime semantics as return values. They
produce caller-visible results through caller-provided storage instead of
through the function's return slot. An unannotated pointer-bearing `out`
parameter in an instance method therefore defaults to the same `unscoped(this)`
relationship as an unannotated return value. In a static/free function, it
defaults to anchorless `unscoped`.

Example:

```camp
bool tryGetSpan(const char[] this, out const char[] result)
{
    result = this[..];
    return true;
}
```

The `result` value is related to `this` by the same default relation as an
ordinary returned span from an instance method.

### Explicit Lifetime Casts

Explicit lifetime casts are the intentional escape hatch for cases where the
programmer knows a lifetime relationship that the compiler cannot prove. They
are assertions to the compiler, not runtime conversions. They erase from emitted
C just like other lifetime annotations.

The supported cast forms are:

```camp
(scoped) value
(unscoped(anchor)) value
(escaped) value
```

They may also be combined with an ordinary type cast:

```camp
(escaped string) ptr
(unscoped(this) const char[]) span
```

The meanings are:

- `(scoped)` forces the expression to be treated as caller-context scoped.
- `(unscoped(anchor))` forces the compiler to assume the expression lifetime is
  equal to or longer than the named anchor. An explicit anchor is required in a
  lifetime cast.
- `(escaped)` forces the expression to be treated as escaped.

These casts should be visibly reviewed in code. They are for carefully
documented interop boundaries, low-level container internals, and other cases
where the source program has information the compiler cannot derive. They
should not be used to paper over ordinary lifetime diagnostics when a better
signature, copy, allocation, or narrower scope would express the relationship.

Invalid cast examples:

```camp
auto a = (unscoped) value;        // ERROR: cast requires an explicit anchor
auto b = (unscoped(missing)) v;   // ERROR: anchor could not be resolved
```

### Callee View Versus Caller View

The callee sees a signature contract. The caller sees actual values.

These views are intentionally different.

The signature is a relation template:

```camp
scoped const char[] trim(scoped const char[] value);
```

At a call site, the compiler substitutes actual argument facts into the
template:

```camp
auto heapText = getEscapedText(); // known escaped from the callee signature
auto result = trim(heapText);
// result is known escaped because heapText is known escaped.
```

This is one of the core implementation requirements. The compiler must track
both slot constraints and current value facts.

### Slots And Values Are Different

A variable slot has a maximum storage constraint. The particular value currently
stored in that slot has a value fact.

Example:

```camp
char[] localSlot = escapedText;
```

The slot may be declaration-scoped, but the current value in it may still be
known to refer to escaped storage. If a later assignment stores a narrower value
into the same slot, the current value fact changes.

The analysis must track:

- slot lifetime constraints: what may legally be stored here;
- current value lifetime facts: what is known about the value currently flowing
  through the slot.

### Aggregate And Container Rule

Pointer-bearing contents are treated as unscoped relative to their containing
value by default. This applies recursively through materialized storage.

The compiler must check assignment into fields as assignment into the containing
value's lifetime.

```camp
struct View
{
    const char[] text;
}
```

`View` is pointer-bearing. Returning, assigning, storing, capturing, yielding,
or passing a `View` is checked through the lifetime of `text`.

### Constructors

`new` does not have an intrinsic lifetime. Its result lifetime comes from the
allocation function or allocator method selected by lowering. If that callee
returns `escaped`, the allocation site is escaped. If the compiler cannot yet
substitute that callee signature, the allocation result remains unknown until
call-site relation solving can prove it.

`init` constructs in existing storage. When a pointer-bearing local aggregate or
context value is initialized, the local receives a fixed lifetime based on the
pointer-bearing values retained by the initialized value.

Later assignments to fields do not widen that lifetime.

```camp
Matrix m = { arg1, arg2 };
// m's value lifetime is fixed from arg1/arg2.
```

Scoped constructor parameters that are used only during construction and are not
retained do not contribute to the result lifetime.

### `within allocator`

`within allocator` has special lifetime behavior documented by the spec. It is
not an ordinary scoped value that always becomes illegal when stored or used to
produce allocation-backed results.

Allocator-backed allocation results are escaped:

```camp
export escaped T[] resize<T: copyable>(
    escaped T[] this,
    nuint newSize,
    within allocator,
    sizeof(T));
```

The input buffer is escaped because allocator `realloc` only applies to
allocation-backed storage. The return is escaped because it remains
allocation-backed.

The `within allocator` parameter itself should not force noisy explicit
annotations in ordinary stdlib signatures.

### Delegate And Callable Context Lifetimes

For `delegate`, `iter`, `once`, `async`, and `async iter`, lifetime annotations
on the callable value describe the hidden context pointer.

For example:

```camp
void retain(escaped delegate bool(int value) predicate);
void invoke(scoped delegate void() action);
```

The lifetime annotation is typically most useful where the delegate is consumed
or stored as an argument, not necessarily on the delegate newtype declaration
itself.

Callable newtypes may include explicit callable `this` parameters to qualify the
hidden context contract:

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);
newtype once void Completion(escaped this, int result);
```

These qualifiers are part of the callable contract and must be enforced during
method-reference conversion and callable ascription.

Lambda capture behavior follows callable lifetime:

- scoped delegate: local state may be captured by reference within the valid
  declaration scope;
- escaped delegate-like form: captures are copied into escaped context storage,
  and captured values are read-only from the closure state.

The current compiler does not yet implement escaped delegates, `async`, or
`async iter`. This plan prepares the shared lifetime model for those features,
but their actual enforcement and tests are deferred to a later stage.

## Implementation Algorithm

The compiler should not perform unbounded symbolic lifetime solving. The
implementation should use a compact finite fact system and relation-template
substitution at calls.

### Core Data Structures

Add a semantic lifetime model separate from formatted type strings.

```csharp
enum LifetimeKind
{
    Unknown,
    Error,
    Escaped,
    Static,
    DeclarationScope,
    CallerContext,
    Scoped,
    Unscoped
}
```

The exact enum can differ, but the analysis needs these concepts:

- escaped/non-stack;
- local declaration scope;
- function-member values such as parameters and receiver;
- caller-context values;
- anchor relationships;
- unknown/error fallback.

Represent facts as bounded values:

```csharp
sealed record LifetimeFact(
    LifetimeKind Kind,
    ImmutableArray<LifetimeAnchor> Anchors,
    SourceRange? Origin);
```

Represent anchors as stable references:

- `this`;
- parameter symbol;
- local declaration symbol;
- global/static storage;
- generated frame/context symbol;
- declaration-scope id;
- escaped/static sentinel.

Represent function signatures as relation templates:

```csharp
sealed record LifetimeSignature(
    ReceiverLifetime,
    ParameterLifetimes,
    ReturnLifetime,
    ReturnConstFlow,
    ThrowsAndOutLifetimes,
    CallableContextLifetime);
```

The signature records callee-side relationships. It does not decide final
call-site facts until actual argument facts are substituted.

### Type Pointer-Bearing Analysis

Add one shared helper that answers:

- whether a type is pointer-bearing;
- whether a type contains pointer-bearing fields/components;
- whether pointer-bearing-ness depends on a generic parameter;
- what expanded components carry pointers;
- whether a value may be copied, retained, captured, returned, or yielded.

This helper must include:

- pointer types;
- string/astring/wstring;
- arrays and fixed-size arrays;
- delegates and iterator callable values;
- optionals;
- materialized params values;
- structs/classes/newtypes by resolved definition;
- erased generics after substitution or conservatively when unknown.

The lifetime checker should run only when the type or substituted type is
pointer-bearing. Non-pointer scalar values should not acquire false const or
lifetime restrictions.

### Slot Facts And Value Facts

Each variable-like storage location receives a slot constraint:

- globals/statics: escaped/static storage;
- fields: lifetime of containing aggregate/context;
- parameters: signature parameter relation;
- local variables: declaration-scope slot unless initialized to a
  pointer-bearing aggregate with a fixed inferred lifetime;
- generated frames: frame lifetime;
- `out` storage: caller-provided slot that receives a result value with return
  semantics.

Each expression receives a current value fact:

- string literal: escaped/static constant storage;
- `new`: escaped;
- `init`: constructed storage lifetime;
- local reference: current value fact of the local;
- field reference: value fact stored in the containing aggregate;
- array slice: fact derived from source;
- address-of local/fixed storage: declaration-scope;
- call expression: result of substituting actual argument facts into callee
  return template;
- explicit lifetime cast: asserted fact from the cast target;
- lambda/method reference: callable context fact;
- default/null: no retained pointer fact unless target type requires one.

Assignment checks both:

1. Is the value allowed by the destination slot constraint?
2. What is the destination's new current value fact after assignment?

### Bounded Relation Solving

Use a small lattice instead of arbitrary graph search.

Ordering for storage width:

```text
escaped/static >= caller-context >= declaration-scope
```

`scoped` and `unscoped` are relation forms, not final storage widths.

At call sites:

1. Analyze receiver and arguments, producing actual value facts.
2. Instantiate the callee lifetime signature with those facts.
3. Validate parameter requirements:
   - `scoped`: any compatible value may be supplied;
   - anchorless `unscoped`: supplied value must be at least as long-lived as
     all scoped arguments in the call;
   - `unscoped(anchor...)`: supplied value must be at least as long-lived as
     each anchor argument;
   - `escaped`: supplied value must be proven escaped.
4. Compute return fact:
   - instance method unannotated return defaults to `unscoped(this)`;
   - static/free unannotated return defaults to anchorless `unscoped`;
   - `scoped` return substitutes scoped argument facts;
   - `scoped(anchor)` substitutes explicit anchor facts;
   - `unscoped(anchor)` return substitutes explicit anchor facts;
   - `escaped` return produces escaped fact.
5. Reduce fact sets with a hard cap. If a join becomes too complex, collapse to
   the narrowest conservative fact and keep a diagnostic breadcrumb.

This makes complexity bounded by the number of arguments and local expression
nodes, not by whole-program graph traversal.

### Const Flow

Constness should use the same template-substitution idea where provable.

If a function returns a value explicitly related to a single argument, and the
caller passed a mutable value, the result may preserve mutability if the
relationship proves it is the same view/value family.

This should be conservative:

- allow precise const flow for direct pointer/array view relationships;
- avoid guessing through pointer-bearing aggregates when field-level constness
  is not provable;
- keep existing type compatibility rules as a fallback.

### Diagnostics

Lifetime diagnostics must name:

- the value being stored/passed/returned;
- its proven lifetime;
- the destination or parameter requirement;
- the anchor or storage that caused the requirement;
- how to fix the problem when obvious.

Examples:

```text
error: Scoped value 'value' cannot be stored in field 'this.text' because the field may outlive this call.
note: Mark the parameter 'unscoped' if callers must provide a value that outlives the receiver.
```

```text
error: Argument 'text' does not outlive receiver 'list'.
note: Parameter 'item' is retained by the receiver through an unscoped argument relationship.
```

```text
error: Delete target cannot satisfy free parameter lifetime 'escaped'.
```

```text
error: Capturing lambda cannot convert to escaped delegate because local 'count' is declaration-scoped.
```

## Stage 1: Semantic Lifetime Model And Binding

Add lifetime facts, anchor binding, defaults, validation, and diagnostics without
yet enforcing all assignment/call flows.

### Work Items

- Add semantic lifetime records independent of `ResolvedType` strings.
- Bind `escaped`, `scoped`, and `unscoped` type references into lifetime facts.
- Bind anchor identifiers to `this`, parameters, or other valid symbols.
- Bind explicit lifetime casts, including `(scoped)`, `(escaped)`, and
  `(unscoped(anchor))`.
- Validate invalid anchors and invalid annotation placement.
- Restrict lifetime annotations to signatures, casts, `escaped class`, and
  `escaped interface`.
- Reject bare `(unscoped)` lifetime casts because an explicit anchor is
  required in casts.
- Apply default parameter and receiver lifetimes.
- Apply `escaped class` and `escaped interface` declaration-site defaults.
- Apply callable explicit `this` lifetime qualifiers.
- Preserve existing source serialization and API output behavior.
- Add a type helper for pointer-bearing analysis.

### Completion Criteria

- ~~The compiler can report bound lifetime facts in an internal debug/test view.~~
- ~~Invalid anchors produce clear diagnostics.~~
- ~~Lifetime annotations still erase from emitted C.~~
- ~~Existing tests continue to pass.~~
- ~~Explicit lifetime casts bind to expression lifetime facts.~~
- ~~Lifetime annotations outside signatures/casts/escaped type declarations are
  rejected with clear diagnostics.~~
- ~~`(unscoped)` casts without an explicit anchor are rejected.~~

Examples:

```camp
void use(scoped(owner) char[] text) {}
```

Now binds `owner` to a real symbol or reports:

```text
error: Lifetime anchor 'owner' could not be resolved.
```

```camp
escaped class Service
{
    void run() {}
}
```

`run` receives an effective `escaped this` receiver contract.

### Tests

- AST/API serialization for `scoped(anchor)` and `unscoped(anchor)`.
- Diagnostics for unresolved anchors.
- Diagnostics for lifetime annotations on fields, globals, locals, ordinary
  type uses, and generic constraint arguments.
- Diagnostics for `(unscoped)` casts without anchors and casts with unresolved
  anchors.
- Bound debug/declarations view for explicit lifetime casts.
- Escaped class/interface default receiver binding.
- Callable `this` lifetime binding for delegate and iter newtypes.

## Stage 2: Expression Facts, Slot Facts, And Local Flow

Track value facts and slot constraints inside method bodies.

### Work Items

- ~~Add per-body lifetime state.~~
- ~~Track slot facts for parameters, receiver, locals, fields, globals, static
  fields, generated frame fields, and `out` storage.~~
- ~~Track expression facts for literals, locals, fields, array components,
  slicing, address-of, `new`, `init`, calls, explicit lifetime casts, lambdas,
  and default/null.~~
- ~~Distinguish slot lifetime from current value lifetime.~~
- ~~Track reassignment of current value facts.~~
- ~~Add conservative fallback for unknown pointer-bearing generic values.~~

### Completion Criteria

~~The compiler can identify the difference between:~~

```camp
char[] local = escapedText;
```

~~where `local` is a declaration-scope slot, but the current value is escaped.~~

~~It can also update facts after reassignment:~~

```camp
local = stackSpan;
```

~~Now the current value fact is tied to `stackSpan`.~~

~~Examples that should be understood internally:~~

```camp
auto p = new byte[64];   // escaped T[]
fixed byte[64] storage;
auto s = storage[..];    // declaration-scope view
auto forced = (escaped) storage[..]; // explicit assertion
```

### Tests

- ~~Debug metadata/golden tests for expression lifetime facts, if a suitable
  internal view exists.~~
- ~~Expression-fact tests for `(scoped)`, `(escaped)`, and
  `(unscoped(anchor))`.~~
- ~~CCompile regression tests proving lifetime metadata does not disturb lowering.~~
- ~~Diagnostics remain unchanged until enforcement stages.~~

## Stage 3: Assignment, Storage, Return, Yield, And Delete Enforcement

Start enforcing lifetime rules for direct storage and result flow.

### Work Items

- ~~Check assignment into globals, static fields, instance fields, locals, array
  elements, fixed-array elements, materialized expanded values, and generated
  context fields.~~
- ~~Reject storing scoped values into caller-context or escaped storage.~~
- ~~Permit unscoped relationships when the value outlives the destination anchor.~~
- ~~Enforce pointer-form `delete` against the visible global `free`
  parameter contract when that contract is known.~~
- ~~Enforce returned/yielded pointer-bearing values satisfy return/yield relation.~~
- ~~Enforce fixed-array span returns through the general lifetime system,
  while allowing iterator yields to rely on generator state lifting.~~
- ~~Honor explicit lifetime casts as programmer assertions when checking
  assignment, return, yield, and delete flows.~~

Allocator `free` validation remains part of Stage 4, where allocator-call
substitution can identify the actual method signature selected by lowering.

### Completion Criteria

~~This should fail:~~

```camp
class Holder
{
    const char[] text;

    void setText(const char[] value)
    {
        this.text = value;
    }
}
```

~~This should pass:~~

```camp
class Holder
{
    const char[] text;

    void setText(unscoped const char[] value)
    {
        this.text = value;
    }
}
```

~~This should fail:~~

```camp
char[] bad()
{
    fixed char[16] local = "hello";
    return local[..];
}
```

~~This uses the escape hatch and should pass type/lifetime checking, while
remaining a sharp tool that code review should question:~~

```camp
char[] trusted(escaped void* owner, char* ptr, nuint length)
{
    return (unscoped(owner) char[]){ ptr, length };
}
```

The exact inline expanded-initializer return form above type-checks after this
stage, but C emission for that direct form is tracked separately as BUG-023.
The Stage 3 CCompile regression uses the equivalent local materialization.

~~This should fail:~~

```camp
Widget local = init Widget();
extern void free(escaped void* ptr);
delete &local;
```

~~This should pass:~~

```camp
extern escaped void* malloc(nuint size);
extern void free(escaped void* ptr);
Widget* ptr = new Widget();
delete ptr;
```

### Tests

- ~~Diagnostics for scoped parameter stored into `this`.~~
- ~~Diagnostics for scoped parameter stored into global/static.~~
- ~~Positive unscoped instance-method storage.~~
- ~~Return diagnostics for local fixed-array spans.~~
- ~~Iterator fixed-array span yields remain valid after generator state
  lifting; direct returns are diagnosed.~~
- ~~Pointer-form delete diagnostics driven by a `free(escaped void*)`
  signature.~~
- ~~Positive and negative explicit lifetime cast diagnostics.~~
- ~~Positive allocation/delete smoke using source-level escaped allocation
  signatures.~~

## Stage 4: Call-Site Relation Solving And Return Substitution

Implement signature-template substitution at call sites.

### Work Items

- ~~Build lifetime signatures for all callable declarations.~~
- ~~Apply anchorless `unscoped` parameter rule:~~
  - ~~argument must outlive all scoped arguments;~~
  - ~~in instance methods this includes receiver by default.~~
- ~~Apply instance-method unannotated return default as `unscoped(this)`.~~
- ~~Apply static/free unannotated return default as anchorless `unscoped`.~~
- ~~Apply the same defaults to pointer-bearing `out` parameters.~~
- ~~Apply `scoped` return substitution.~~
- ~~Apply `scoped(anchor)` and `unscoped(anchor)` substitution.~~
- ~~Apply equivalent `out` parameter substitution.~~
- ~~Preserve lifetime facts produced by explicit lifetime casts when they flow
  into call arguments or out-parameter storage.~~
- Track return const flow where directly provable. Deferred to BUG-024 because
  it is a type-refinement pass, not a lifetime fact substitution pass.
- ~~Validate call arguments against parameter relations.~~
- ~~Produce diagnostics with call-site source ranges.~~

### Completion Criteria

If a scoped-return function is called with escaped input, the result is known
escaped:

```camp
scoped const char[] trim(const char[] text);

auto heapText = getEscapedText(); // known escaped from the callee signature
auto result = trim(heapText);
delete result.elements; // not necessarily semantically desirable, but lifetime fact is escaped
```

If it is called with local storage, the result is local:

```camp
fixed char[16] storage = "hello";
auto result = trim(storage[..]);
return result; // ERROR
```

Instance-method default return:

```camp
class Buffer
{
    char[] items;

    char[] getItems()
    {
        return this.items;
    }
}
```

The return is treated as `unscoped(this)` without requiring the source to spell
that annotation.

Static/free default return:

```camp
char[] choose(char[] a, char[] b)
{
    return condition ? a : b;
}
```

The return is anchorless `unscoped`: at least as long-lived as the relevant
scoped input relation permits.

### Tests

- ~~Scoped return from escaped argument becomes escaped at call site.~~
- ~~Scoped return from local argument cannot escape.~~
- ~~Unannotated instance-method return tied to receiver.~~
- ~~Unannotated static return tied to scoped arguments.~~
- ~~Explicit `scoped(anchor)` return.~~
- ~~Explicit return `unscoped(anchor)`.~~
- ~~Unannotated pointer-bearing `out` parameter in an instance method tied to
  receiver.~~
- ~~Unannotated pointer-bearing `out` parameter in a static/free function using
  anchorless `unscoped`.~~
- ~~Explicit `scoped(anchor)` / `unscoped(anchor)` `out` parameter relation.~~
- ~~Explicit lifetime cast used to satisfy an `unscoped(anchor)` or `escaped`
  parameter requirement.~~
- Const flow positive and conservative negative tests. Deferred to BUG-024.

## Stage 5: Constructors, `init`, `new`, And Retained Values

Implement constructor result lifetime rules and local aggregate lifetime fixing.

### Work Items

- ~~Determine constructor result lifetime from the constructor parameter
  contract and supplied pointer-bearing arguments. Constructor body changes do
  not alter callsite lifetime analysis.~~
- ~~For `new`, derive the result lifetime from the selected allocation function
  or allocator method signature.~~
- ~~For `init` and aggregate initialization of local pointer-bearing values, fix
  local value lifetime at first initialization.~~
- ~~Do not widen fixed local lifetime after later assignments.~~
- ~~Include trailing initializer syntax in retained-value analysis.~~
- ~~Respect scoped constructor parameters by tying the initialized value to the
  supplied argument rather than inspecting how the constructor body uses it.~~
- ~~Account for `within allocator` forwarding without requiring noisy source
  annotations.~~

### Completion Criteria

~~This should pass:~~

```camp
struct View
{
    const char[] text;

    View(unscoped const char[] text)
    {
        this.text = text;
    }
}

View make(const char[] input)
{
    View view = init View(input);
    return view;
}
```

~~This should fail:~~

```camp
View bad()
{
    fixed char[16] local = "hello";
    View view = init View(local[..]);
    return view;
}
```

~~Later assignments do not widen a local:~~

```camp
View view = init View(localSpan);
view.text = escapedSpan;
return view; // still ERROR
```

~~`new` result remains escaped:~~

```camp
auto list = new List<int>();
return list; // OK if return type permits escaped pointer
```

### Tests

- ~~Constructor retained parameter positive/negative tests.~~
- ~~Constructor body changes do not alter the result lifetime contract.~~
- ~~Aggregate initializer lifetime fixing.~~
- ~~Trailing initializer lifetime fixing.~~
- ~~Later assignment does not widen local lifetime.~~
- ~~`new` escaped result facts.~~
- ~~`within allocator` constructor forwarding smoke.~~

## Stage 6: Scoped Delegates, Lambdas, Method References, And Callable Newtypes

Apply lifetime analysis to callable contexts that the compiler supports today.
Prepare the representation for escaped delegates, but defer escaped delegate
capture semantics until the later deferred-callable stage.

### Work Items

- ~~Treat delegate/iter/once/async callable value lifetime as hidden context
  lifetime in the shared model, even where some callable families are not yet
  implemented.~~
- ~~Enforce callable explicit `this` qualifiers during method-reference
  conversion.~~
- ~~Enforce callable ascription receiver lifetime rules.~~
- ~~Target-type lambdas using callable context lifetime.~~
- ~~Scoped delegates may capture locals by reference only within valid scope.~~
- ~~Reject conversions that would require escaped delegate support with a clear
  "escaped delegates are not implemented yet" diagnostic, rather than accepting
  unsound code.~~
- ~~Preserve non-capturing lambda `fn` behavior.~~
- ~~Avoid putting unnecessary lifetime annotations on delegate newtype
  declarations when the relationship belongs at the consuming argument.~~

### Completion Criteria

~~Scoped delegate capture:~~

```camp
void runNow(delegate void() action)
{
    action();
}

void test()
{
    int count = 0;
    runNow(() => count++);
}
```

~~Escaped delegate rejection:~~

```camp
void register(escaped delegate void() action);

void test()
{
    int count = 0;
    register(() => count++); // ERROR: escaped delegates are not implemented yet
}
```

~~Callable `this` contract:~~

```camp
newtype delegate nuint Formatter(const this, char[] buffer = default);

class Date
{
    nuint format(char[] buffer = default) : Formatter
    {
        return 0;
    }
}
```

~~The method body is analyzed as `const this`.~~

### Tests

- ~~Scoped lambda local capture positive.~~
- ~~Escaped delegate use reports deferred-feature diagnostic.~~
- ~~Method reference to callable requiring `escaped this`.~~
- ~~Method reference to callable requiring `const this`.~~
- ~~Callable newtype ascription with explicit/implicit callable `this`.~~
- ~~Delegate argument lifetime specified at use site.~~

## Stage 7: Iterators And Generated Contexts

Apply the container rule to generated frames.

### Work Items

- Treat generator state structs/classes as pointer-bearing context objects.
- Lifted locals receive frame lifetime constraints.
- Values crossing `yield` must outlive the iterator frame.
- `struct iter` and `class iter` differ by frame storage, but both participate
  in lifetime analysis.
- Iterator `foreach` cleanup must preserve lifetime facts for yielded values.
- Prepare shared generated-context hooks so async frames can use the same model
  later, without implementing async lifetime enforcement in this stage.

### Completion Criteria

This should fail:

```camp
struct iter const char[] lines()
{
    fixed char[16] local = "hello";
    yield local[..]; // ERROR: yielded view cannot outlive frame/step safely
}
```

This should pass when the source outlives the iterator:

```camp
struct iter const char[] splitLines(unscoped const char[] text)
{
    yield text[..1];
}
```

### Tests

- Generator retained scoped parameter negative.
- Generator retained unscoped parameter positive.
- Yield borrowed view lifetime diagnostics.
- Nested foreach over iterator lifetime smoke.

## Stage 8: Generics And Erased Values

Apply lifetime analysis after generic substitution and conservatively inside
erased generic bodies.

### Work Items

- Treat `T` as potentially pointer-bearing unless constraints prove otherwise
  or the operation is outside the generic body with concrete substitution.
- Enforce lifetime policies outside generic types where type arguments are
  known.
- Avoid forcing generic implementations such as `List<T>` to understand
  concrete pointer-bearing semantics internally.
- Apply aggregate/container rules to `in T`, `out T`, return `T`, array
  elements, optionals, delegates, iterator frames, and future async frames.
- Keep `in T` transport address scoped to the call.
- Ensure `T: copyable` and `T: any` existing rules compose with lifetime checks.

### Completion Criteria

Generic list storage is validated at the call boundary:

```camp
auto list = new List<const char[]>();
fixed char[16] local = "hello";
list.add(local[..]); // depends on whether local outlives list; checked at call site
```

Inside `List<T>`, the implementation copies/stores `T` according to the generic
contract. It does not need to inspect the concrete pointer-bearing fields.

`in T` transport cannot escape:

```camp
T* bad<T: any>(in T value)
{
    return &value; // ERROR
}
```

### Tests

- `List<const char[]>` positive/negative lifetime tests.
- `List<int>` unaffected by pointer-bearing rules.
- `in T` address escape diagnostic.
- Generic delegate/context storage diagnostics.
- Generic array element lifetime tests after substitution.
- Existing generic array erasure tests continue to pass.

## Stage 9: Standard Library API Cleanup

Apply the policy to the stdlib without over-annotating defaults.

### Source Policy

Do not write explicit lifetime annotations merely to restate defaults.

Prefer sparse signatures. Future tooling may display inferred lifetime
information, but this plan does not require that support.

Annotations should appear when:

- allocation results need to be explicitly `escaped`;
- a borrowed return relation is not the default or is ambiguous;
- an API intentionally requires escaped context;
- an API consumes/stores a delegate-like context with a non-default lifetime.

### Likely Stdlib Adjustments

`std_array.camp`:

```camp
export escaped T[] resize<T: copyable>(
    escaped T[] this,
    nuint newSize,
    within allocator,
    sizeof(T));
```

`List.ensureCapacity` should use:

```camp
this.items = this.items.resize<T>(newCapacity);
```

Copy-producing APIs may need explicit escaped returns:

```camp
escaped T[] copyArray<T: copyable>(const T[] this, within allocator, sizeof(T));
escaped string stringCopy(const char[] this, within allocator);
escaped string Console.readLine(within allocator);
escaped FileHandle* FileHandle.open(...);
```

Borrowing APIs can stay sparse if defaults express the intended relationship.
Where useful, use anchors only for non-obvious returns:

```camp
scoped const char[] trim(const char[] this);
```

Do not automatically rewrite ordinary instance methods to `unscoped(this)` in
source. That is default behavior.

Delegate lifetimes should usually be specified at the argument/use site, not on
the delegate newtype itself, unless the callable type always requires that
context lifetime.

### Completion Criteria

- Stdlib builds with lifetime enforcement enabled.
- StdRun tests continue to pass.
- `Array.resize` rejects stack spans and accepts escaped arrays.
- `List<T>` remains idiomatic and sparse.
- Copy APIs return escaped facts.
- Stream/file reader-writer callable contexts cannot be used after their source
  object is no longer valid.

### Tests

- StdRun resize/list tests.
- String copy and delete tests.
- FileHandle writer lifetime diagnostics.
- Console writer positive escaped/static context.
- List of pointer-bearing values positive/negative tests.

## Stage 10: Diagnostics And Documentation Polish

Make the feature usable.

### Work Items

- Add diagnostic codes or stable message prefixes.
- Add notes explaining inferred default relationships.
- Add doc examples to the spec where implementation reveals ambiguity.
- Document explicit lifetime casts as the escape hatch for relationships the
  compiler cannot prove.
- Document that lifetime annotations are allowed only in signatures, explicit
  casts, and escaped type declarations.
- Ensure metadata/API serializers preserve source annotations but do not expand
  inferred defaults into noisy source.

### Completion Criteria

Given:

```camp
list.add(localSpan);
```

The diagnostic should explain:

- `localSpan` lifetime;
- `list` receiver lifetime;
- why the parameter relation requires the value to outlive the receiver;
- whether `stringCopy`, `new`, or moving allocation outside the scope would fix
  it.

### Tests

- Diagnostics golden tests with line/column ranges.
- API/metadata tests ensuring inferred lifetimes do not pollute source output.
- Regression tests for no duplicate/noisy annotations in generated `.camp` API.
- Diagnostics and docs examples for invalid explicit lifetime casts and invalid
  annotation placement.

## Later Stage: Escaped Delegates, Async, And Async Iterators

The compiler does not currently implement escaped delegates, `async`, or
`async iter`. The core lifetime model must be designed so these features can
plug into it later, but this implementation plan defers their actual
enforcement and tests.

### Future Work Items

- Implement escaped delegate context allocation/capture semantics.
- Copy captures into escaped context storage.
- Treat escaped delegate captures as read-only from inside the closure.
- Reject non-escaped references captured into escaped delegate contexts unless
  ordinary container rules prove the relationship safe.
- Enforce fixed-size array capture restrictions for escaped callable contexts.
- Apply generated-context lifetime rules to async frames.
- Require values used after suspension to outlive the async frame.
- Apply the same model to `async iter` frames and yielded values.
- Add async/postponed context capture tests once async lowering exists.

### Future Completion Examples

```camp
void register(escaped delegate bool(int value) predicate);

void test(int threshold)
{
    register(value => value > threshold); // OK once escaped delegates exist
}
```

```camp
async void process(const char[] text)
{
    await something();
    use(text); // ERROR unless text outlives async frame
}
```

## Suggested Rollout Order

The safest rollout is:

1. Model/binding only.
2. Internal fact tracking with no enforcement.
3. Enforce obvious illegal escapes: fields, globals, returns, delete.
4. Add call-site relation solving.
5. Add constructor/local aggregate lifetime fixing.
6. Add delegates/lambdas.
7. Add iterator generated contexts.
8. Add generics and stdlib hardening.
9. Improve diagnostics and documentation.
10. Later, add escaped delegates, async, and async iterators on top of the same
   lifetime model.

Each stage should land with tests and without weakening existing behavior. If a
stage uncovers a pre-existing unrelated compiler bug, log it in
`tests/OutstandingBugs.md` and defer it unless it blocks the lifetime feature.

## High-Value Test Matrix

### Positive Tests

- allocation results annotated by the selected allocator/free signatures can be
  returned and deleted;
- scoped borrow can be used during a call;
- unscoped argument can be stored in receiver;
- scoped return from escaped argument is treated as escaped at call site;
- scoped return from local argument cannot escape;
- `List<int>` unaffected by lifetime checks;
- `List<const char[]>` accepts values that outlive the list;
- scoped delegate captures local by reference in valid scope;
- escaped delegate use reports a deferred-feature diagnostic until escaped
  delegates are implemented;
- `Array.resize` accepts escaped arrays and returns escaped arrays.

### Negative Tests

- scoped argument stored into `this`;
- scoped argument stored into global/static;
- local fixed-array span returned;
- iterator fixed-array span yields stay valid after generator state lifting;
- pointer-form delete of local address when the selected `free` contract
  requires escaped storage;
- escaping lambda captures local by reference;
- escaped delegate capture does not silently compile before escaped delegates
  are implemented;
- `in T` address returned;
- stack span passed to API that retains relative to longer-lived receiver;
- non-escaped `FileHandle` writer stored beyond handle lifetime.

### Diagnostic Quality Tests

- message includes source line and column;
- message names anchor/receiver causing requirement;
- message explains inferred default when source lacks annotation;
- message distinguishes callee contract from call-site facts;
- message suggests `unscoped`, `escaped`, copy allocation, or narrower scope only
  when that is actually applicable.

## Complexity Control Rules

To prevent unbounded analysis:

- Do not perform whole-program lifetime graph solving.
- Analyze one function body at a time.
- Treat called functions through bound lifetime signatures.
- Substitute actual call-site facts into finite templates.
- Cap joined anchor sets.
- Collapse overly complex facts to conservative narrow facts.
- Track current value facts per slot, but do not retain unbounded assignment
  histories.
- Recompute or invalidate facts at control-flow joins using conservative joins.
- Prefer false positives with useful diagnostics over unsound retention.

Control-flow joins should use the narrowest safe fact:

```camp
char[] value;
if (condition)
    value = escapedSpan;
else
    value = localSpan;

return value; // lifetime is at most the localSpan branch
```

Loops should converge quickly:

- initialize loop-carried slot facts before loop;
- analyze body;
- join back-edge facts once or to a small fixed point;
- collapse if facts grow.

## Open Design Checks Before Implementation

These should be confirmed before coding enforcement stages:

1. Whether `scoped` return without anchors should be serialized distinctly from
   inferred defaults.
2. How much const-flow preservation should be implemented in v1.
3. Whether lifetime diagnostics should have stable numeric codes.
4. Whether an internal `dump lifetimes` view should be added for testing and
   future tooling development.

The core semantics above should remain unchanged regardless of these choices.
