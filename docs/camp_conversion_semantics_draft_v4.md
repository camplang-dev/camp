# Camp Conversion Semantics: `unsafe`, Raw Carriers, and Fence Casts

Status: draft working note.

This note defines when a conversion is implicit, when it uses an ordinary explicit cast, when it requires the `unsafe` cast marker, and when it requires a raw fence.

`unsafe` is a reserved keyword. Its primary use in this note is the cast form:

```camp
(unsafe T)value
```

`unsafe` does **not** mean “this is the only dangerous conversion.” Fence casts are also dangerous. The distinction is about how much type information remains visible to the compiler.

| Category | Surface form | Meaning |
|---|---|---|
| Implicit | `T target = value;` | The conversion is known and value-preserving enough to require no cast syntax. |
| Explicit | `(T)value` | The conversion is known, but the programmer must acknowledge it. |
| Unsafe | `(unsafe T)value` | The source and target are still related enough for a direct cast, but the cast breaks a contract the type system normally protects. |
| Fence | `(T)(void*)value`, `(T)(fn*)value`, `(T)(untyped)value`, etc. | The programmer first erases type information through a raw carrier, then recovers a target type. |
| Reconstruct | build a new value | The source is multivalue or shape-changing, so a cast is the wrong operation. |

A fence means the programmer deliberately erased type information. After a fence, recovery is usually an ordinary explicit cast. The main exceptions are typed-carrier rules that still matter after a `void*` or `fn*` fence, especially const/volatile removal and physical-indirection changes.

`untyped` is the ultimate fence. Once a value is converted to `untyped`, Camp treats its type information as erased.

## 1. Target specifier placement

Current Camp examples place target specifiers in two styles:

| Type family | Canonical placement | Example |
|---|---|---|
| Concrete function type | after `fn`, before the return type | `fn _cdecl int(int value)` |
| Concrete function type with function-pointer typespec and callspec | after `fn`, before the return type | `fn _far _pascal nint()` |
| Data pointer type | after the pointer type form | `char* _far` |
| `void*` fence | after `void*` | `void* _far` |
| `fn*` fence | after `fn*` | `fn* _far` |
| `nint` / `nuint` | after the integer type | `nint _far` |
| Expanded array carrier | after the array type form | `int[] _near` |

A **typespec** describes target-specific representation, address-space, storage-location, or natural-integer domain. Examples include `_near`, `_far`, `_huge`, `_rom`, `_flash`, and target-defined equivalents.

A **callspec** describes how a concrete callable is invoked. Examples include `_cdecl`, `_stdcall`, and `_pascal`. A callspec is not a typespec.

`fn*` may carry a target typespec but may not carry a callspec:

```camp
fn* _far rawFarFunction;      // OK: function-pointer representation domain
fn* _stdcall badFunction;    // ERROR: _stdcall is a callspec, not a typespec
```

Concrete callable types may carry callspecs:

```camp
fn _stdcall int(int value) callback;
fn _far _pascal nint() farProc;
```

`void*`, `nint`, `nuint`, and `untyped` may not carry callspecs:

```camp
void* _far rawData;       // OK: data-pointer representation domain
void* _stdcall badData;   // ERROR
nint _far farInteger;     // OK if the target defines it
nint _stdcall badInteger; // ERROR
```

`untyped` may not carry either a typespec or a callspec:

```camp
untyped raw;          // OK
untyped _far bad1;    // ERROR
untyped _stdcall bad2; // ERROR
```

### Prefix spelling and expanded forms

The canonical spelling for a data pointer with a typespec is postfix:

```camp
int* _near p;
```

When the language accepts a prefix typespec spelling, the prefix specifier binds to the next target-spec-capable carrier inside the type to its right. Thus these are equivalent in meaning:

```camp
_near int*[]     // readable prefix form, if accepted
(int* _near)[]   // canonical explicit grouping
```

Both mean:

> a normal/default array carrier whose elements are `_near int*` values.

The array value itself still has the default array carrier: its `.elements` component is a default-domain pointer to an array of `_near int*` elements, and its `.length` component is a default-domain `nuint`.

This is different from:

```camp
int[] _near
```

which means:

> a `_near` array carrier over ordinary `int` values.

Conceptually:

| Type | Meaning |
|---|---|
| `_near int*[]` or `(int* _near)[]` | Default-domain array carrier; elements are `_near int*`. |
| `int[] _near` | `_near` array carrier; elements are ordinary `int`. |

For `int[] _near`, the expanded array components use the `_near` domain:

| Component | Conceptual component type |
|---|---|
| `.elements` | `int* _near` |
| `.length` | `nuint _near` |

The exact materialized spelling is target/compiler-defined. The important rule is that a typespec applied to an expanded array applies to the array carrier components, not to the element type.

## 2. Value conversion versus type rewrite

Camp should preserve convenient target-defined value conversions, such as passing a near pointer to a function that accepts a far pointer.

```camp
void consume(const byte* _far data);

byte* _near local;
consume(local); // OK when the target defines _near -> _far as implicit
```

But that convenience applies at **value boundaries**. It does not automatically rewrite generic arguments, array element types, delegate signatures, or function pointer signatures.

| Case | Example | Conversion model |
|---|---|---|
| Value boundary | `byte* _near -> byte* _far` | Target-defined value conversion. May be implicit. |
| Generic argument rewrite | `Container<byte* _near>* -> Container<byte* _far>*` | Not a value conversion. Use class cast rules or a fence. |
| Array element rewrite | `(byte* _near)[] -> (byte* _far)[]` | No cast conversion. Reconstruct the array. |
| Delegate signature rewrite | `delegate void(byte* _near) -> delegate void(byte* _far)` | No cast conversion. Reconstruct the delegate. |
| Function signature rewrite | `fn void(byte* _near) -> fn void(byte* _far)` | A direct callable cast does not insert argument conversions. Use `fn*`/`untyped` if raw reinterpretation is intended. |

A short rule:

> Value conversions happen to values. They do not tunnel through type constructors.

## 3. Raw carrier types

Camp has four raw carrier concepts.

| Carrier | Kind | May carry target typespec? | May carry callspec? | Main purpose |
|---|---:|---:|---:|---|
| `void*` | data pointer | yes | no | Erases data pointee type/category. |
| `fn*` | function pointer | yes | no | Erases function signature. |
| `nint` / `nuint` | integer | yes, target-defined | no | Integer carrier for data pointer values; arithmetic allowed. |
| `untyped` | universal raw scalar | no | no | Erases data/function/integer carrier identity. |

### `void*`: data-pointer fence

`void*` is the raw carrier for **data pointer values**. It erases the pointee type and the data-pointer family, but it remains a data pointer.

A `void*` fence is physical-depth preserving. For a two-level data pointer, the matching void carrier is `void**`, not `void*`.

```camp
byte* bytes;
PacketHeader* header = (PacketHeader*)(void*)bytes;

Widget** pp;
Other** qq = (Other**)(void**)pp;
```

After a value has passed through a matching-depth `void*`, recovery to any same-depth data pointer type in the same target pointer domain is ordinarily explicit.

Two rules still override a `void*` fence:

| Operation | Required level |
|---|---:|
| Remove `const` or `volatile` | `unsafe` |
| Change physical pointer depth | at least `unsafe` |

```camp
const Widget* source;
const void* raw = (const void*)source;
Widget* target = (unsafe Widget*)raw; // const removal

void* oneLevel;
Widget** twoLevel = (unsafe Widget**)oneLevel; // physical-depth change
```

A `void*` value may carry a target typespec such as `_near`, `_far`, or `_huge` when the target supports that spelling:

```camp
void* _far rawFar;
void* _near rawNear;
```

`void* _far` means “a raw data pointer in the `_far` data-pointer domain.” It is not the same type as `void* _near` unless the target says those domains are interchangeable. Recovering from `void* _far` normally produces a `_far` data pointer; recovering to another data-pointer domain requires a target-defined conversion or `untyped`.

### `fn*`: function-pointer fence

`fn*` is the raw carrier for **function pointer values**. It means “any function type.” In C terms it is roughly comparable to `void (*)()`, but it is a Camp type with defined conversion rules.

`fn*` is not callable. It must be cast back to a concrete function type before it can be called.

```camp
fn int(int) f;
fn* raw = (fn*)f;
fn short(short) g = (fn short(short))raw;
```

A cast to `fn*` erases the function signature, including result type, parameter types, callable newtype identity, callspec, and callable lifetime annotations. Recovering from `fn*` to a concrete function type is ordinarily explicit when the target function-pointer domain matches the `fn*` domain. Recovering to another function-pointer domain requires a target-defined conversion or `untyped`.

`fn*` may carry a target **typespec**, because function pointer representations may differ by target domain:

```camp
fn* _near nearFunction;
fn* _far farFunction;
```

`fn*` may **not** carry a callspec. A callspec describes how to call a concrete callable. `fn*` has no callable signature and cannot be called.

```camp
fn* _stdcall bad; // ERROR: _stdcall is a callspec, not a typespec
fn* _cdecl bad2;  // ERROR
```

Use callspecs only on concrete callable types:

```camp
fn _stdcall int(int value) callback;
fn _cdecl int(int value) other;
```

### `nint` / `nuint`: natural integer carriers for data pointers

`nint` and `nuint` are natural integer types for the target's data-pointer model. Their width is target-defined. On some targets they may be 16 bits.

A target may allow typespecs on `nint` and `nuint`:

```camp
nint _near nearAddress;
nint _far farAddress;
```

The intended meaning is target-defined, but a common model is:

| Type | Meaning |
|---|---|
| `nint _near` | signed integer carrier for `_near` data pointer values |
| `nint _far` | signed integer carrier for `_far` data pointer values |
| `nint _huge` | signed integer carrier for `_huge` data pointer values |

Converting between a data pointer and a compatible `nint` or `nuint` is an ordinary explicit cast, not an unsafe cast.

```camp
Widget* p;
nuint bits = (nuint)p;
Widget* q = (Widget*)bits;
```

The conversion discards pointer type information, constness, and lifetime information. That loss is part of converting to an integer carrier.

`nint` and `nuint` are ordinary integer types. They support arithmetic and integer operations according to ordinary integer rules.

`nint` and `nuint` do **not** portably hold function pointer values. Use `fn*` for function-pointer erasure or `untyped` for the universal raw carrier.

### `untyped`: universal raw scalar carrier

`untyped` is the universal raw scalar carrier. It can hold any value that fits in the carrier, including:

- any data pointer;
- any function pointer;
- `void*`;
- `fn*`;
- `nint` / `nuint`;
- any primitive or value newtype whose representation fits in `untyped`.

Camp does not define a minimum bit width for `untyped`. It is required only to be large enough to hold any value of type `nint`, `void*`, or `fn*` for the selected target.

Likely C representations are target-dependent:

| Target shape | Possible C representation for Camp `untyped` |
|---|---|
| Data pointers and function pointers have the same size and representation | `void*` |
| Data pointers and function pointers differ, but one pointer representation can hold both | the larger pointer representation or a target-specific opaque carrier |
| 16-bit or segmented memory models with incompatible data/function pointers | a union or target-defined struct containing both data-pointer and function-pointer carriers |

The C representation is an implementation detail. The source-level rule is that `untyped` can carry `nint`, `void*`, or `fn*` without loss of carrier capacity.

`untyped` has no target typespec, no callspec, no constness, no volatility, no lifetime, and no pointer depth. It cannot be decorated with `_near`, `_far`, `_huge`, `_rom`, `_stdcall`, or similar specifiers.

```camp
untyped raw;         // OK
untyped _far x;      // ERROR
untyped _stdcall y;  // ERROR
```

Once a value has been converted to `untyped`, type information is gone. Recovery from `untyped` to any representable scalar, data pointer, or function pointer type is ordinarily explicit. No additional `unsafe` is required merely because the recovered type has different constness, lifetime, indirection, target typespec, or callable signature. The `untyped` conversion is the ultimate raw fence.

```camp
const Widget* _near source;
untyped rawObject = (untyped)source;
Widget** _far p = (Widget** _far)rawObject;

fn _stdcall int(int) f;
untyped rawFunction = (untyped)f;
fn _cdecl short(short) g = (fn _cdecl short(short))rawFunction;
```

`untyped*`, if used, means a pointer to storage whose element type is `untyped`. It is not the universal fence. The fence type is `untyped` itself.

## 4. Target typespecs and callspecs

Camp targets may define target-specific type specifiers and calling-convention specifiers. They are separate concepts.

| Concept | Applies to | Examples | Meaning |
|---|---|---|---|
| Typespec | data pointers, function pointers, `nint`/`nuint`, expanded carriers, and other target-defined type positions | `_near`, `_far`, `_huge`, `_rom`, `_flash` | Representation domain, address space, storage location, or other target-specific type meaning. |
| Callspec | concrete callable types only | `_cdecl`, `_stdcall`, `_pascal` | How a concrete callable is invoked. |
| Fence carrier | raw carrier type | `void*`, `fn*`, `untyped` | How much type information is erased before recovery. |

`_stdcall` is a callspec, not a typespec. It cannot be used with `fn*`, `void*`, `nint`, `nuint`, or `untyped`.

### Target-defined typespec conversion policy

The target defines conversion policy between typespec domains for each relevant carrier kind.

For example, an x86-like memory model might define:

| Source | Target | Data-pointer value conversion |
|---|---|---:|
| `T* _near` | `T* _far` | implicit |
| `T* _near` | `T* _huge` | implicit |
| `T* _far` | `T* _near` | unsafe, fence-only, or forbidden; target-defined |
| `T* _huge` | `T* _near` | unsafe, fence-only, or forbidden; target-defined |

A storage-location model might instead define:

| Source | Target | Data-pointer value conversion |
|---|---|---:|
| `const T* _rom` | `const T* _ram` | no typed conversion |
| `const T* _flash` | `T* _ram` | no typed conversion |
| `T* _io` | ordinary `T*` | target-defined |

The language does not assume `_near`, `_far`, `_huge`, `_rom`, or any other spelling has one universal meaning. The selected target defines the conversion graph.

### Typespec conversions do not tunnel

A target-defined value conversion may be implicit for a direct value, but it does not automatically make constructed types interchangeable.

| Conversion | Should ordinary value conversion apply? | Reason |
|---|---:|---|
| `byte* _near -> byte* _far` | yes | Direct pointer value conversion. |
| `nint _near -> nint _far` | target-defined | Direct integer carrier conversion. |
| `Container<byte* _near>* -> Container<byte* _far>*` | no | Generic arguments are invariant. |
| `(byte* _near)[] -> (byte* _far)[]` | no | Arrays are invariant. |
| `delegate void(byte* _near) -> delegate void(byte* _far)` | no | Delegates are invariant multivalue callable values. |
| `fn void(byte* _near) -> fn void(byte* _far)` | no direct safe cast | Callable casts do not insert argument conversions. |

### Fences and target domains

`void*` and `fn*` are typed raw carriers. They may still carry a target typespec.

| Fence | Erases | Still remembers |
|---|---|---|
| `void* _far` | data pointee type/category | data-pointer carrier, `_far` domain, physical depth, qualifiers/lifetime unless explicitly discarded |
| `fn* _far` | function signature and callspec | function-pointer carrier, `_far` domain |
| `untyped` | all scalar carrier type information | nothing except raw carrier bits/value |

A `void*` or `fn*` fence can be used to cross target typespec domains only when the target defines such a fence or value conversion. If the target declares two domains disjoint, use `untyped` for a deliberate raw escape.

```camp
byte* _near nearData;
void* _far farRaw = (void* _far)nearData; // OK if target permits _near data pointer -> _far data pointer

const byte* _rom romData;
void* _ram ramRaw = (void* _ram)romData;  // ERROR if target declares ROM/RAM disjoint

untyped raw = (untyped)romData;
byte* _ram ramPretend = (byte* _ram)raw;  // raw escape through untyped
```

## 5. Overriding rules

These rules apply before family-specific rules.

### Const and volatile

Adding `const` or `volatile` is implicit when the rest of the conversion is valid.

Removing `const` or `volatile` requires `unsafe` for typed pointer conversions and `void*`/`fn*`-based fence conversions.

```camp
const Widget* source;
const void* raw = (const void*)source;
Widget* target = (unsafe Widget*)raw;
```

`nint`, `nuint`, and `untyped` are scalar raw carriers. Casting a pointer through one of them discards qualifiers as part of the explicit scalar conversion and does not require a second `unsafe` solely for const removal.

```camp
const Widget* source;
nuint bits = (nuint)source;
Widget* target1 = (Widget*)bits;

untyped raw = (untyped)source;
Widget* target2 = (Widget*)raw;
```

### Data-pointer lifetimes

Changing a lifetime annotation on a data pointer is an ordinary explicit cast.

```camp
Widget* p;
escaped Widget* e = (escaped Widget*)p;
```

Raw scalar carriers such as `nint`, `nuint`, and `untyped` discard data-pointer lifetime information. Recovery uses the target type's written lifetime.

### Callable lifetimes

A direct cast between concrete callable types that changes any callable lifetime annotation requires `unsafe`.

This is more dangerous than a data-pointer lifetime cast. Callable lifetime annotations can describe hidden context lifetime, receiver lifetime, scoped result provenance, and relationships among other arguments and results.

```camp
fn scoped(buffer) byte*(Buffer* buffer) f;
fn byte*(Buffer* buffer) g = (unsafe fn byte*(Buffer*))f;
```

A cast through `fn*` or `untyped` is a raw function fence. After that fence, recovery to a concrete function type is ordinarily explicit.

### Physical indirection

A direct pointer-to-pointer cast that changes physical pointer depth is at least `unsafe`.

```camp
Widget* p;
Widget** pp = (unsafe Widget**)p;

void* raw;
Widget** pp2 = (unsafe Widget**)raw;
```

This rule applies to typed pointer casts and `void*`-based casts. It does not apply to scalar raw carriers (`nint`, `nuint`, `untyped`) because those carriers already erase pointer shape.

Expanded or multivalue forms cannot change indirection level by cast. Materialize or reconstruct them instead.

## 6. Data pointer families

For same-depth data pointer casts, the source and target belong to one of these families.

| Family | Examples | Direct conversion rule |
|---|---|---|
| Primitive / value-newtype pointers | `int*`, `byte*`, `UserId*` | Explicit between any same-depth pointers in this family, subject to qualifiers and target typespec policy. |
| Struct pointers | `PacketHeader*`, `NetworkHeader*` | Explicit between any same-depth struct pointers, subject to qualifiers and target typespec policy. |
| Class pointers | `Control*`, `Container<int>*` | Class hierarchy rules. |
| Interface pointers | `Readable*`, `Seekable*` | Interface slot/vtable rules. |
| `void*` at matching depth | `void*`, `void**`, `void* _far` | Data-pointer fence and recovery carrier. |

Casts within a family use that family's rules. Casts between families require a matching-depth `void*` fence unless a more specific language conversion exists.

### Primitive and value-newtype pointers

Pointers to primitive types and pointers to value newtypes explicitly convert to each other within the same physical indirection depth.

```camp
int* values;
uint* bits = (uint*)values;
```

Changing to another data-pointer family requires a `void*` fence.

```camp
byte* bytes;
PacketHeader* header = (PacketHeader*)(void*)bytes;
```

### Struct pointers

Struct pointers explicitly convert to other struct pointers within the same physical indirection depth.

```camp
PacketHeader* header;
NetworkHeader* network = (NetworkHeader*)header;
```

Changing to or from another data-pointer family requires a `void*` fence.

### Class pointers

Constructed generic class types are invariant. `Box<int>` and `Box<uint>` are unequal types.

A pointer to a class implicitly converts to a pointer to any exact constructed base class, provided the pointer carrier conversion is also implicit.

```camp
SuperList<int>* derived;
ListBase<int>* base = derived;
```

A near-to-far pointer conversion can combine with exact-base widening when the target defines the pointer conversion as implicit.

```camp
Derived* _near d;
Base* _far b = d; // OK if _near class pointer -> _far class pointer is implicit and Derived : Base
```

Any other class-pointer cast is `unsafe` when the source and target share an equal constructed base class, or when the source class is an exact constructed base of the target class.

```camp
ListBase<int>* base;
SuperList<int>* derived = (unsafe SuperList<int>*)base;

Container<int>* ints;
Container<Widget*>* widgets = (unsafe Container<Widget*>*)ints;
```

The second example is allowed only if `Container<int>` and `Container<Widget*>` share an equal constructed base, such as a non-generic `ContainerBase`.

If the source and target do not share an equal constructed base class, there is no direct class cast. Use a `void*` fence when the target pointer domain permits it, or `untyped` for the universal raw escape.

```camp
FileHandle* file;
EditControl* edit = (EditControl*)(void*)file;
```

Hidden generic capabilities such as stored `sizeof(T)` or `vtableof(T: I)` do not change the cast category. They affect whether the cast is correct, not which syntax class it belongs to.

### Interface pointers

An interface pointer has built-in physical indirection. Source `IFoo*` is an interface-instance slot pointer, physically comparable to `IFoo**`.

Therefore:

```camp
IFoo* iface;
void** slot = (void**)iface;          // same physical depth: explicit
void* raw = (unsafe void*)iface;      // drops one physical level: unsafe
IFoo* again = (unsafe IFoo*)raw;      // adds one physical level: unsafe
```

A class pointer implicitly converts to an interface pointer when the class implements that interface. This is not an object-pointer reinterpretation; the compiler produces the correct interface-instance slot pointer.

```camp
EditControl* edit;
IEditable* editable = edit;
```

Casting a class pointer to a non-implemented interface requires a raw fence and `unsafe`, because the cast invents an interface-instance slot relationship.

```camp
Object* obj;
IEditable* editable = (unsafe IEditable*)(void*)obj;
```

Interface-to-interface widening is implicit when the target is an exact base interface and the compiler can perform the required interface conversion.

Interface narrowing or side-casting is `unsafe` only when the source and target have a common interface relationship with identical slot/vtable layout for the cast. Otherwise use an explicit projection/conversion helper or a same-depth raw fence.

```camp
Readable* readable;
Seekable* seekable = (unsafe Seekable*)readable; // only if layout-compatible
```

## 7. Integer and pointer conversions

Data pointer values explicitly convert to and from compatible `nint` and `nuint` forms.

```camp
Widget* p;
nint signedBits = (nint)p;
nuint unsignedBits = (nuint)p;
Widget* q = (Widget*)unsignedBits;
```

This includes data pointer values at any source-level indirection depth, because the value being carried is still one data pointer value.

```camp
Widget** pp;
nuint bits = (nuint)pp;
Widget** again = (Widget**)bits;
```

Target typespecs on `nint` / `nuint` follow the target's data-pointer carrier policy.

```camp
byte* _near nearData;
nint _near nearBits = (nint _near)nearData;
nint _far farBits = (nint _far)nearData; // OK only if target permits _near data pointer -> _far data-pointer integer carrier
```

Function pointer values do not portably convert to `nint` or `nuint`. Use `fn*` when the value remains in the function-pointer family, or `untyped` when the value must cross families.

```camp
fn int() f;
fn* rawFunction = (fn*)f;
untyped rawUniversal = (untyped)f;
```

## 8. Function targets and callable types

This section applies to plain function targets, anonymous `fn` types, `fn*`, and callable newtypes whose underlying carrier is a function target.

### Function carrier layers

| Layer | Example | Meaning |
|---|---|---|
| Concrete anonymous function type | `fn int(int)` | Function pointer with known signature. |
| Callable newtype | `IntParser` over `fn bool(const char[], out int)` | Nominal function/callable contract. |
| `fn*` | `fn* _far` | Function-pointer fence; signature and callspec erased. |
| `untyped` | `untyped` | Universal scalar fence; function/data/integer identity erased. |

A cast from a concrete function type to `fn*` is an explicit function fence. A cast from `fn*` to a concrete function type is also explicit.

```camp
fn int(int) f;
fn* raw = (fn*)f;
fn int(int) same = (fn int(int))raw;
fn short(short) different = (fn short(short))raw;
```

No extra `unsafe` is required after the `fn*` fence merely because the recovered signature or callspec differs. The fence is the point where that information was erased.

### Widening-compatible direct callable signatures

For direct callable-to-callable conversion without a `fn*` or `untyped` fence, a source callable signature is widening-compatible with a target callable signature when all of the following hold:

1. The callable carrier is compatible.
2. The callspec is identical or the target explicitly classifies the callspec conversion as compatible.
3. Each callable slot is ABI-slot compatible. Ordinary value conversions that would require inserted caller-side or callee-side conversion code do not count.
4. For each input parameter, the target parameter type is acceptable to the source parameter slot, using contravariant checking over ABI-compatible slot types.
5. For each return, `out`, or other produced result slot, the source result type is acceptable to the target result slot, using covariant checking over ABI-compatible slot types.
6. Callable lifetime annotations are unchanged.

Input parameters are checked contravariantly. Produced values are checked covariantly.

```camp
fn void(const Widget*) acceptsConst;
fn void(Widget*) acceptsMutable = acceptsConst;

fn Widget*() returnsMutable;
fn const Widget*() returnsConst = returnsMutable;
```

Removing `const` from a produced value is not widening.

```camp
fn const Widget*() returnsConst;
fn Widget*() returnsMutable = (unsafe fn Widget*())returnsConst;
```

A near-to-far pointer conversion may be implicit as a value conversion, but it does not make these function types widening-compatible if the ABI slot differs:

```camp
fn void(byte* _far) farConsumer;
fn void(byte* _near) nearConsumer = farConsumer; // ERROR unless the target says the slots are ABI-compatible
```

Use `fn*` or `untyped` when raw reinterpretation is intended:

```camp
fn void(byte* _far) farConsumer;
fn void(byte* _near) nearConsumer = (fn void(byte* _near))(fn*)farConsumer;
```

### Callable conversion levels

| Conversion | Level |
|---|---:|
| Concrete callable to same concrete callable type | implicit or explicit depending on context; no semantic change |
| Concrete callable to widening-compatible anonymous callable type | implicit when no nominal boundary is crossed |
| Anonymous callable to compatible callable newtype | explicit |
| Same carrier but not widening-compatible | unsafe, unless the target requires a fence |
| Function-pointer target typespec changes | `fn*` or `untyped` fence, subject to target policy |
| Function/data family crossing | `untyped` preferred; direct `void*` crossing, if allowed, is unsafe |

A function target converted to `void*` crosses from the function-pointer family to the data-pointer family and requires `unsafe`. Prefer `fn*` for a function-only fence and `untyped` for a universal fence.

```camp
fn int() f;
void* p = (unsafe void*)f;

fn* rawFunction = (fn*)f;
untyped rawUniversal = (untyped)f;
```

## 9. Arrays

Array types are invariant. Casts do not convert array element types.

```camp
int[] ints;
uint[] uints = (uint[])ints; // invalid
```

Allowed array conversions:

| Operation | Level |
|---|---:|
| Add const to element view | implicit |
| Remove const from element view | unsafe |
| Change array lifetime annotation | explicit |
| Change array carrier typespec, when target defines it | target-defined value conversion |
| Change element type, element target typespec, or element pointer family | reconstruct |

All other conversions require reconstruction of the array components.

```camp
byte[] bytes;
uint[] words = {
    .elements = (uint*)(void*)bytes.elements,
    .length = bytes.length / sizeof(uint),
};
```

Reconstruction is required because element reinterpretation may also require changing the meaning of `.length`.

## 10. Optionals

Optional conversions follow the payload conversion only when the payload conversion exists.

`T?` converts to `U?` only when `T` converts to `U` at the same or lower safety level. The `.specified` component is copied unchanged.

```camp
int? a;
long? b = a;          // if int -> long is implicit
uint? c = (uint?)a;   // if int -> uint is explicit
```

If the payload type has no cast conversion, the optional has no cast conversion. Reconstruct the optional explicitly.

```camp
byte[]? maybeBytes;
uint[]? maybeWords = (uint[]?)maybeBytes; // invalid: array payload conversion does not exist
```

## 11. Delegates and other multivalue callable values

Delegate values are invariant. Casts do not convert delegate signatures.

Allowed delegate conversions:

| Operation | Level |
|---|---:|
| Add const where otherwise valid | implicit |
| Remove const | unsafe |
| Change any lifetime annotation | unsafe |
| Change signature, target typespec inside signature, result shape, or context shape | reconstruct |

All other conversions require reconstructing the delegate value, usually by creating a new callable wrapper or by explicitly rebuilding its components.

```camp
delegate int(short) small;
delegate int(int) widened = value => small((short)value);
```

A delegate value is multivalue state, not a scalar function pointer. It does not cast as a whole to `fn*`, `nint`, or `untyped`. If low-level code needs to manipulate the stored call target, operate on the delegate's `call` component explicitly and preserve or rebuild the `context` component deliberately.

## 12. Generic constructed types

Constructed generic types are invariant. Equal generic type definitions with different arguments are unequal constructed types.

```camp
Box<int>  // not equal to Box<uint>
```

Value conversions do not tunnel through generic arguments.

```camp
byte* _near nearData;
byte* _far farData = nearData; // may be implicit

Container<byte* _near>* nearContainer;
Container<byte* _far>* farContainer = nearContainer; // invalid
```

For class pointers, use the class-pointer rules:

| Source and target relationship | Level |
|---|---:|
| Target is exact constructed base of source | implicit |
| Source is exact constructed base of target | unsafe |
| Source and target share an equal constructed base | unsafe |
| No equal constructed common base | `void*` fence or `untyped` raw escape |

Examples:

```camp
class ContainerBase {}
class Container<T>: ContainerBase {}

Container<int>* ints;
Container<Widget*>* widgets = (unsafe Container<Widget*>*)ints; // allowed because of ContainerBase
```

Without a shared equal base, cross-instantiation casts require a raw fence.

Hidden generic capabilities such as `sizeof(T)` or `vtableof(T: I)` do not grant cast compatibility. They are capabilities for a particular constructed instantiation. Rebinding a generic argument may make such capabilities wrong, but it does not change the syntax category.

## 13. Summary tables

### Raw carriers

| Source intent | Preferred carrier | Notes |
|---|---|---|
| Erase data pointee/category | `void*` | May carry data pointer typespec; preserves typed-carrier rules such as const removal and physical depth. |
| Erase function signature | `fn*` | May carry function pointer typespec; cannot carry callspec. |
| Store data pointer as integer | `nint` / `nuint` | Explicit, not unsafe; arithmetic allowed; not portable for function pointers. |
| Erase everything representable | `untyped` | No typespec/callspec/const/lifetime/depth; ordinary explicit recovery to any representable target. |

### Typespec effects by context

| Context | Target-defined value conversion applies? | If not directly valid |
|---|---:|---|
| Direct data pointer value | yes | `void*` fence if target permits; otherwise `untyped` raw escape. |
| Direct `nint` / `nuint` value | yes | `untyped` raw escape. |
| Direct function pointer value | target-defined | `fn*` fence if target permits; otherwise `untyped`. |
| Generic argument | no | class rules if pointer to class; otherwise reconstruct/fence. |
| Array element type | no | reconstruct array. |
| Delegate signature | no | reconstruct delegate. |
| Function signature | only ABI-slot-compatible changes | `unsafe`, `fn*`, or `untyped` depending on carrier/target policy. |

### Common operations

| Operation | Level |
|---|---:|
| `T* _near -> T* _far` | implicit if target defines it so |
| `T* _far -> T* _near` | target-defined; often unsafe, fence-only, or invalid |
| `T* -> nint` / `nuint` | explicit |
| `nint` / `nuint -> T*` | explicit |
| function pointer -> `nint` | not portable; use `fn*` or `untyped` |
| concrete function pointer -> `fn*` | explicit |
| `fn*` -> concrete function pointer | explicit |
| function pointer -> `void*` | unsafe |
| pointer/function/integer -> `untyped` | explicit universal fence |
| `untyped` -> representable pointer/function/integer | explicit |
| remove `const` through typed pointer/`void*`/`fn*` | unsafe |
| change data-pointer lifetime | explicit |
| change callable lifetime | unsafe |
| change physical pointer depth through typed pointer/`void*` | unsafe |
| change array or delegate element/signature type | reconstruct |

## 14. Short examples

```camp
SuperList<int>* superListInt;
Container<int>* containerInt;
FileHandle* fileHandle;
byte[] bytes;
fn int(int) someFunction;

// implicit class upcast
ListBase<int>* b = superListInt;

// unsafe class downcast
SuperList<int>* s = (unsafe SuperList<int>*)b;

// unsafe class side-cast through a shared equal base
Container<Widget*>* cw = (unsafe Container<Widget*>*)containerInt;

// fence between unrelated class families
EditControl* edit = (EditControl*)(void*)fileHandle;

// const removal still requires unsafe through void*
const FileHandle* cfile;
const void* craw = (const void*)cfile;
EditControl* cedit = (unsafe EditControl*)craw;

// data pointer <-> nint/nuint is explicit, not unsafe
nuint address = (nuint)fileHandle;
FileHandle* restored = (FileHandle*)address;

// interface physical indirection
IEditable* iface;
void** slot = (void**)iface;
void* rawSlotWrongDepth = (unsafe void*)iface;

// fn* is the function-pointer fence
fn* rawFn = (fn*)someFunction;
fn short(short) otherSignature = (fn short(short))rawFn;

// untyped is the universal scalar fence
untyped raw = (untyped)someFunction;
fn int(int) callback = (fn int(int))raw;

// arrays reconstruct instead of casting element type
uint[] words = {
    .elements = (uint*)(void*)bytes.elements,
    .length = bytes.length / sizeof(uint),
};
```

### Target-typespec examples

```camp
void consumeFar(const byte* _far data);

byte* _near nearData;
consumeFar(nearData); // OK when target defines _near -> _far as implicit
```

```camp
fn void(byte* _far) farConsumer;

// Invalid unless the target says the parameter slots are ABI-compatible.
fn void(byte* _near) nearConsumer = farConsumer;

// Raw function-pointer fence.
fn void(byte* _near) rawNearConsumer =
    (fn void(byte* _near))(fn*)farConsumer;
```

```camp
const byte* _rom table;

// Invalid if the target declares ROM/RAM disjoint.
byte* _ram writable = (byte* _ram)table;

// Universal raw escape.
untyped rawTable = (untyped)table;
byte* _ram pretendWritable = (byte* _ram)rawTable;
```

### Array carrier versus element pointer domain

```camp
_near int*[] a;
```

Means:

```camp
(int* _near)[] a;
```

The array carrier is default-domain. The array elements are `_near int*` values.

```camp
int[] _near b;
```

The array carrier is `_near`. The array elements are ordinary `int` values.
