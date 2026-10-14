+++
nav_title = "13. Generics"
+++

# Generics And Capabilities

Camp checks a generic body once. Its type parameters describe how values are
represented; capability parameters supply the extra facts needed for copying,
storage, type names and interface dispatch. Selecting a concrete argument does
not compile a new body or grant it extra operations.

## Borrowing Storage

An omitted representation, `<T>`, means unspecified-size or void
representation. It can hold otherwise legal scalar, aggregate, pointer,
optional and expanded type arguments. Borrow storage through a pointer or view:

```camp
void borrow<T>(T* value)
{
}

class BorrowedSlot<T>
{
    T* value;

    void set(T* value) { this.value = value; }
    T* getValue() => this.value;
}
```

A pointer to T refers to the complete represented storage of the argument.
For an expanded argument such as an optional or array view, that is one
storage object containing every component. Transporting a view does not copy
its elements or grant permission to index them.

Direct T input is invalid under void representation, even with size and a
witness. Direct T fields are invalid because a generic declaration has one
fixed layout. Pointer and view fields still obey normal lifetimes.

## Copying With sizeof

Use `sizeof(T)` when an operation needs checked copy/layout capability:

```camp
T choose<T>(in T left, in T right, bool useLeft, sizeof(T))
{
    return useLeft ? left : right;
}

int selected = choose<int>(10, 20, true);
```

The caller normally supplies size automatically. The argument must be copyable:
ordinary scalars, copyable structs, array views, delegates and ordinary
optionals qualify. Direct classes, fixed structs and fixed-size array values
do not. Pointers to them do, because only the pointer is copied.

This restriction applies even when the body ignores the size slot.
An ordinary concrete sizeof expression is a size query; knowing an object's
size does not make it copyable. An arbitrary byte count or a size for another
operand cannot stand in for the capability.

In/out T requires this checked capability. It also authorizes assignment,
default fill, copied results, indexing, slicing, element stride and permitted
allocation and enumeration:

```camp
void copyTo<T>(const T[] source, T[] destination, sizeof(T))
{
    nuint count = source.length;
    if (destination.length < count) count = destination.length;
    for (nuint index = 0; index < count; index++)
        destination[index] = source[index];
}
```

The size covers the complete represented value, including all expanded
components. Copying a view or delegate copies its components, not the data or
object graph behind them. Size does not grant construction, destruction,
ownership transfer or a longer lifetime.

A generic callback with in/out T declares its own size contract:

```camp
void visitEach<T>(
    T[] values,
    delegate void(in T item, sizeof(T)) visit,
    sizeof(T))
{
    foreach (stackalloc T item in values)
        visit(item);
}
```

The callback's size slot is compiler-supplied at invocation. A matching concrete
callback declares the corresponding operand, such as sizeof(int). A target-typed
lambda receives the target's capability slots along with its ordinary inputs.

## Integer Representations

Write an integer representation when fixed scalar storage is the intended
contract:

```camp
struct WordSlot<T: nint>
{
    T value;
}
```

Supported representations are byte, ushort, short, uint, int, ulong, long,
nuint and nint. Aliases resolve to those representations. Omission always means
void; it never means nint. Explicit `T: void` and the old any/copyable/implements
constraint forms are invalid.

Integer representations admit fitting scalar arguments, retaining their nominal
identity. Their width follows the selected target. Direct classes/structs,
expanded values and function pointers are not integer carrier arguments.
Representation supplies fixed carrier copy/layout facts, not an interface
witness or type-name capability.

A written carrier `R<A>`, such as `long<Color>`, stores and transports A
using R. Its address, array elements and callable signatures retain that
storage type. An ordinary read exposes A. Literals are checked against A;
a wide carrier does not make an out-of-range byte literal valid.

## Forwarding And Representation Replacement

Supplying an argument already carried as S<A> selects the destination's R<A>.
For example, an int-carried byte forwarded to a long parameter becomes
long<byte>, and to a void parameter becomes void<byte>. The compiler preserves
the actual nominal argument and its nested types.

This type rule does not resize existing storage. Same-representation forwarding
preserves it. A pointer or array cannot silently change stride, and out storage
cannot acquire an implicit temporary adapter. An abstract representation change
needs proof of the actual transport; width alone is insufficient.

## Type Names

`typenameof(T)` provides the source type name for exactly its operand:

```camp
string nameOf<T>(typenameof(T)) => typenameof(T);
```

It grants no size, copying, dispatch or reflection. A capability for T does not
also supply names for T*, T[] or const T. A known represented value names its
actual argument, not its carrier. Names are not type identities.

## Interface Witnesses

Use a typed witness when generic code needs interface dispatch:

```camp
interface Reader { int read(); }

int readOne<T>(T* value, vtableof(T: Reader))
{
    return value.read();
}
```

The actual argument must be a direct class or struct implementing Reader.
A pointer to such an object is a different argument and does not qualify as the
witness operand. Witnesses require void representation and provide dispatch,
not copying or size.

Ordinary witness slots receive T*, while ordinary interface-table slots receive
Reader*. Source typing preserves that distinction despite a shared C layout.
Lifecycle witness slots use the declared constructor/destroy and allocator
contracts. A witness-only class algorithm can dispatch without requesting size.
An ordinary implementing copyable struct may combine size and a witness through
legal indirect transport; that still does not permit direct T input.

## Retained Capabilities

Generic class constructors retain requested size, name and witness values for
later instance operations. They do not make unknown-size T fields legal.
Method-local capabilities do not become class-wide state. Generic structs
receive required capabilities per call; ordinary integer/string/raw-pointer
fields do not manufacture evidence.

List, HashMap and HashSet retain checked constructor size capabilities for the
copying and layout work performed by their methods. Their array storage can
hold ordinary optional and expanded values whole.

## Local Storage And Results

Use explicit stackalloc plus size for an exact void T local:

```camp
void swapFirst<T>(T[] values, sizeof(T))
{
    stackalloc T temporary = values[0];
    values[0] = values[1];
    values[1] = temporary;
}
```

An ordinary T local or an auto requiring unknown-size storage is invalid.
Integer-represented T uses an ordinary local. Stack storage is allocated on
entry, including each resume activation; initializer effects occur where the
declaration is written. Reassign named stack storage after yield/await before
reading it; stack addresses do not survive suspension.

A void T result uses caller storage for every component. A direct call reads
it as A, while a callable reference keeps void<A> in its result signature.
A plain A result or trailing source out parameter is not an interchangeable
callable result. Nonexpanded in void<A> may match in A under the ordinary
signature rules; expanded whole storage remains distinct.

T?, void<A>? and R<A>? are invalid optional payloads. An ordinary optional
supplied as A under void is different: void<int?> is valid whole storage.
Use bool plus out T for absence, or a nullable pointer when appropriate.

## Migrating Generic APIs

Choose the new contract from the work the body performs:

| Existing intent | Representation and capability |
|---|---|
| Borrow storage | Omitted representation with pointers/views. |
| Copy/layout | Omitted representation plus checked sizeof on the operation or class constructor. |
| Interface dispatch | Omitted representation plus explicit or retained vtableof(T: I). |
| Fixed machine-word storage | Explicit : nint. |
| Existing integer representation | Retain R and check eligibility/storage boundaries. |
| A copyability restriction without body use | Keep a checked size slot where the restriction is required. |

Do not remove a constraint word and assume the contract is preserved. Review
input modes, callbacks, retained facts, exported source signatures and native
ABI together. Rebuild affected producers and consumers when those contracts
change.
