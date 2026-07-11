# Type System Overview

## Type Categories

Camp has primitive scalar types, pointer types, callable types, arrays,
fixed-size arrays, optional values, structs, classes, interfaces, enums,
newtypes, generic constructed types, iterator types, and special expanded forms
such as thrown and grouped parameter carriers.

Primitive examples include `int`, `uint`, `long`, `ulong`, `nint`, `nuint`,
`float`, `double`, `bool`, `char`, `achar`, `wchar`, and `uchar`.

## Type Spelling

Type spelling is compositional:

```camp
const char[] text;
int* values;
delegate void(const char[] message) callback;
Option<int>* result;
```

Qualifiers such as `const`, `volatile`, `escaped`, `scoped`, `unscoped`, and
`constof(anchor)` apply to type forms. Target-specific type specifiers may also
apply where the selected target defines them.

## Value, Reference, And Expanded Forms

Some source types are represented by multiple lowered components. Arrays carry
an element pointer and a length. Delegates carry a target and context.
Iterators, thrown slots, and async callable forms also have expanded shapes.

Users normally write the source type. Compiler writers should use the semantic
supplements for exact component order and ABI details.

## Type Qualifiers

`const` prevents mutation through the qualified view. `volatile` marks values
whose access semantics are target-sensitive. `constof(anchor)` ties constness to
another parameter or receiver. Lifetime qualifiers describe whether a value may
escape a scope or is anchored to another value.

## Target Specifiers And Call Specifiers

A target type specifier describes a target-defined representation domain, such
as an address-space or memory-model qualifier. A call specifier describes how a
concrete callable is invoked. Targets define the valid specifier names.

```camp
extern fn _cdecl int(int value) transform;
char* _far buffer;
```

The compiler supplement documents target files. Conversion details live in
[Pointers, Qualifiers, And Conversions](07-pointers-qualifiers-and-conversions.md).

## When Types Are Nominal

Structs, classes, interfaces, enums, and newtypes introduce nominal boundaries.
Two types with similar storage are not interchangeable unless the language
defines a conversion or the programmer uses an explicit construction or cast
that is valid for that boundary.

## Expanded Forms

Several source types are expanded forms. They behave like one source value, but
the compiler lowers them to multiple components:

| Source form | User-facing model |
|---|---|
| `T[]` | Element pointer plus length. |
| `delegate R(P)` | Callable target plus context. |
| `once R(P)` | Single-use callable target plus context. |
| `iter T` | Iterator state plus protocol surface. |
| `thrown(T)` | Error slot in callable shape. |
| `async` callable | Callback-shaped callable plus completion. |
| `Interface*` | Pointer to an interface-instance slot. |

Expanded forms are still typed values. Casts and conversions operate on the
source value, not on arbitrary individual lowered components. This is why array
element rewrites, delegate signature rewrites, and generic argument rewrites
usually require reconstruction.

## Storage, View, And Ownership

Camp types often describe a view rather than ownership. `T*` is an address.
`T[]` is a counted view. `Interface*` is an interface-instance pointer. A
delegate may or may not own context. Ownership is established by constructors,
allocation, `within`, lifetimes, and API documentation.

When designing an API, decide whether each value is:

- owned by the caller;
- owned by the callee;
- borrowed for the call;
- stored for later use;
- part of a native ABI boundary.

Then choose pointer, lifetime, array, class, interface, and allocator forms that
state that decision in the signature.

## Source Type Versus ABI Shape

The source type is canonical for Camp users. The ABI shape matters when a type
is exported, stored in metadata, passed to C, or used by generic capability
parameters. User-facing docs include ABI intuition where it changes source
correctness, such as interface vtables and array carriers. Exact generated C
names and pass internals belong to semantic supplements.

## Primitive And Library Type Names

Primitive names are built into the language. Library names are ordinary
declarations imported through namespaces.

```camp
int count;
string title;
Std::Time::Date date;
List<int>* values;
```

The difference matters for portability and metadata. A primitive type exists
without an import. A library type exists because a source or package reference
contributes that declaration.

## Pointer And View Types

Pointers and views are explicit:

```camp
Widget* widget;
const byte[] bytes;
IWriter* writer;
```

`Widget*` is an address of a `Widget`. `byte[]` is a counted span. `IWriter*`
is an interface-instance pointer. None of these spellings imply ownership by
themselves.

Ownership is a property of the API contract: allocation, construction,
destruction, `within`, lifetimes, and documentation explain who owns the value
and who must clean it up.

## Value-Like And Identity-Like Types

Structs, enums, newtypes, primitives, arrays, optionals, and callable values
are value-like source forms. Classes and interface instance pointers are
identity-like forms.

This does not mean every value-like form is freely copyable. Fixed structs,
fixed-size arrays, expanded values, and generic `T: any` values may have
copying restrictions. Copyability is a semantic capability, not a guess based
only on whether the spelling looks small.

```camp
struct Point
{
	int x;
	int y;
}

class Window
{
}
```

A `Point` value can be stored directly. A `Window*` points at a class instance
with identity and lifecycle.

## Copyability And Fixed Storage

Some types can be copied by value. Others have stable storage identity or
contain state that must not be copied implicitly.

Examples:

- ordinary scalar primitives are copyable;
- many ordinary structs are copyable when their fields are copyable;
- fixed structs have fixed-in-place semantics;
- fixed-size arrays are inline storage, not ordinary return values;
- classes are used through pointers;
- `T: any` does not imply copying is available.

APIs that copy generic values should say `T: copyable`. APIs that only inspect,
address, or enumerate storage can often use `T: any` with explicit capabilities
such as `sizeof(T)`.

## Materialized Expanded Forms

Expanded forms usually travel as multiple ABI components. When one-address
storage is required, use materialized storage forms where the language permits
them.

```camp
struct(char[]) storedText;
struct(delegate void()) storedCallback;
```

Materialization is useful for arrays of expanded values, fields that need one
address, and interop shapes that need ordinary storage. It does not change the
source-level meaning of the expanded value; it gives the lowered components a
single containing storage object.

## Callable Type Families

Callable forms describe how code is invoked and whether context is carried.

| Form | Model |
|---|---|
| `fn R(P)` | Plain function target, no hidden context. |
| `delegate R(P)` | Target plus context. |
| `once R(P)` | Target plus context intended for one call. |
| `iter T` | Iterator callable protocol. |
| `async R(P)` | Callback-shaped async callable. |

Context-carrying callable forms may spell an explicit callable `this` parameter
to qualify the hidden context:

```camp
delegate void(escaped this, int value)
```

Callable newtypes can name any of these contracts.

## Interface Type Family

An interface declaration defines a nominal vtable shape. Camp distinguishes
the vtable type from the interface-instance pointer form:

```camp
Drawable vtable;
Drawable* drawable;
```

Most ordinary code uses `Drawable*`. The details matter enough to be
user-facing because they affect class fields, struct adapter lifetimes, generic
`vtableof`, and ABI design. See
[Interfaces And Dispatch](12-interfaces-and-dispatch.md).

## Type Equality And Compatibility

Type equality is not the same thing as storage similarity. These are distinct
types even if the target representation is compatible:

```camp
newtype UserId: uint;
newtype GroupId: uint;
```

Compatibility comes from language-defined conversions, explicit casts,
interface implementation, class inheritance, callable compatibility, or generic
constraints. The conversion chapter describes those boundaries.

## Metadata Type Spelling

Metadata preserves source-level type spelling where it matters.

Examples include:

- `constof(source) char*`;
- callable newtype names;
- `this` return types;
- target call specs and type specs;
- generic capability parameters;
- interface implementation relationships.

Metadata is therefore a source contract, not merely a C declaration dump.
