# Outstanding Bugs

Before adding a bug, verify the behavior against the language semantics and
confirm that it is in fact a bug. The explanation should include repro
instructions and should be written in general terms, without reference to a
particular file, package, or product.

Bugs should be added to this file one at a time. After each bug is added, commit
`OutstandingBugs.md` to the repo and include the new bug number in the commit
message. Add new bugs to the bottom of this file so existing bug numbers and
review history remain stable.

When committing a change that fixes a bug, or is related to a bug, reference the
bug number in the commit message. The final commit that fixes a bug, or the only
commit if there is just one, should delete the bug from this file and include
that `OutstandingBugs.md` change in the same commit.

Next bug number: BUG-283.

## Bug Template

Use this shape when adding a new bug:

```md
## BUG-###: <brief summary>

Date/Time: YYYY-MM-DD HH:MM <timezone>

Summary:
<One or two paragraphs describing the bug in generalized terms.>

Steps to Reproduce:

1. <Step one.>
2. <Step two.>

Expected:
<What should happen according to the language semantics or documented compiler behavior.>

Actual:
<What actually happens. Include diagnostics or observed output when useful.>

Known Impact:
<Who or what is affected, and any known workaround if one exists.>
```

## BUG-280: Target-typed enum member lookup fails when a global is the left comparison operand

Date/Time: 2026-10-10 20:54 EDT

Summary:
In an equality comparison, an unqualified enum member on the right resolves from
the left operand's enum type when the left operand is a local variable, but not
when the left operand is a global variable of the same enum type.

Steps to Reproduce:

1. Declare `enum Color: int { RED, GREEN }` and a global `Color g;`.
2. In a function, write `Color l = RED; if (l != RED) return 1;` and
   `if (g != RED) return 2;`.
3. Build the file.

Expected:
Both comparisons resolve `RED` through the enum type of the left operand.

Actual:
The local comparison compiles. The global comparison reports
`Symbol 'RED' could not be found.`

Known Impact:
Programs must copy the global to a local or qualify the member (`Color.RED`).

## BUG-281: Representation-constraint type arguments are not checked for width or kind

Date/Time: 2026-10-10 21:00 EDT

Summary:
The language documents that a type argument for a representation constraint
`T: C` must be an integer of equal or smaller width, a compatible enum or value
newtype, or (for `nint` and `nuint`) a pointer. The compiler checks only that the
argument is not expandable; any other argument is accepted.

Steps to Reproduce:

1. Declare `struct Slot<T: int> { T value; }`.
2. Declare locals `Slot<ulong> a;`, `Slot<float> b;` and `Slot<int*> c;`.
3. Build the file.

Expected:
Each declaration is rejected, naming the constraint the argument fails.

Actual:
No diagnostic is reported.

Known Impact:
A wider or non-integer argument silently receives a representation of the wrong
size or kind.

## BUG-282: An optional of an alias to a copyable type is rejected as non-copyable

Date/Time: 2026-10-10 22:40 EDT

Summary:
An optional whose payload is written through an `alias` reports that optional
payloads must be copyable, although the aliased type is copyable. The same
optional written with the aliased type directly is accepted.

Steps to Reproduce:

1. Declare `alias Count = int;`.
2. Declare `void take(Count? value) { }` and `void direct(int? value) { }`.
3. Build the file.

Expected:
Both declarations are accepted, because `Count` names `int`.

Actual:
`take` reports `Optional payloads must be copyable; use a pointer to a
non-copyable value instead.` and `direct` is accepted.

Known Impact:
An alias cannot be used as an optional payload. Programs must write the aliased
type directly.

## BUG-283: A null initializer for a pointer to an optional lowers a component to `true`

Date/Time: 2026-10-10 23:18 EDT

Summary:
Initializing a pointer to an optional with `null` expands the pointer into its
component pointers, but the presence component receives the optional's
presence value instead of a null pointer, so the generated C does not compile.

Steps to Reproduce:

1. Declare `export int main() { int?* pointer = null; return 0; }`.
2. Build the file.

Expected:
Both component pointers (`pointer` and `pointer_specified`) are null.

Actual:
The generated C declares `bool *pointer_specified = true;` and the native
compiler rejects the integer to pointer conversion.

Known Impact:
A pointer to an optional cannot be initialized with `null`. Programs must take
the address of an existing optional instead.

## BUG-284: Declaring `vtableof` before `sizeof` drops the vtable argument at calls

Date/Time: 2026-10-10 23:18 EDT

Summary:
A generic function that declares `vtableof(T: I)` before `sizeof(T)` is called
without its vtable argument. With the order reversed, the call is correct.

Steps to Reproduce:

1. Declare an interface `Reader`, a struct `Box: Reader`, and
   `int read<T: implements Reader>(T* source, vtableof(T: Reader), sizeof(T)) => source.read();`.
2. Call `read(&box)` from `main`.
3. Build the file.

Expected:
The call passes the `Box` vtable and `sizeof(Box)`.

Actual:
The generated C calls `read(&box, sizeof(Box))` and the native compiler reports
too few arguments. Declaring `sizeof(T)` first then `vtableof(T: Reader)`
produces `read(&box, sizeof(Box), Box_Reader)`.

Known Impact:
Capability parameters must be declared with `sizeof(T)` before `vtableof`, with
no diagnostic when they are not.

## BUG-285: Passing an element of a `void<V>` array to an `in void<V>` parameter fails in C

Date/Time: 2026-10-10 23:18 EDT

Summary:
Passing `items[i]`, an element of a `void<int?>[]`, to an `in void<int?>`
parameter builds a compound literal of the storage type initialized from the
element, which is invalid C.

Steps to Reproduce:

1. Declare `int read(in void<int?> value)`.
2. Declare `void<int?>[] slots = stackalloc void<int?>[2];` and assign
   `slots[1] = 4;`.
3. Call `read(slots[1])` and build the file.

Expected:
The element storage is passed by address.

Actual:
The generated C calls `read(&(storage){ ((storage *)(slots))[1] })` and the
native compiler rejects initializing an `int` member from a structure.

Known Impact:
An array element cannot be passed to an `in void<V>` parameter. Programs must
read it into a `V` local or pass `&items[i]` with a `void<V>*` parameter.
