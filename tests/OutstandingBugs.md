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

Next bug number: BUG-295.

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

## BUG-286: Interface call with a written `void<V>` result passes the result storage in the wrong position

Date/Time: 2026-10-11 19:18 EDT

Summary:
A function or method may declare a `void<V>` result, which returns through caller-owned storage placed after the authored parameters. When such a method is called through an interface, the generated C passes the result storage before the authored arguments instead of after them, so the call does not compile.

Steps to Reproduce:

1. Declare an interface with a method that has a written `void<int>` result and one `int` parameter.
2. Implement the interface on a struct, with the method returning an `int` value through the `void<int>` result.
3. Take a pointer to the struct as the interface type and call the method through the interface, for example `source.next(5)`.
4. Build and run the program.

```camp
interface Source { void<int> next(int step); }
struct Counter: Source
{
	int value;
	void<int> next(int step): Source { return this.value + step; }
}
export int main()
{
	Counter counter = { 10 };
	Source* source = &counter;
	int result = source.next(5);
	return 0;
}
```

Expected:
The call compiles and reads the `int` result, as a direct call to the method does. Proposal 025 section 3 places the result storage after the authored parameters.

Actual:
The C compiler rejects the generated call: `(*source)->next(source, &_value1, 5);` passes an `int *` where the `int step` parameter is expected (`incompatible pointer to integer conversion`).

Known Impact:
Any interface method with a written `void<V>` result cannot be called through the interface. Direct calls and generic dispatch to non-interface callables are unaffected. No workaround other than calling the concrete type or returning an ordinary value.

## BUG-287: Distinct callable newtypes with identical shapes convert implicitly

Date/Time: 2026-10-11 19:18 EDT

Summary:
Callable newtypes are nominal: two newtypes with the same calling shape remain different types, and a value of one needs an ascription, cast or source rule to become the other. The compiler accepts an implicit conversion between two distinct callable newtypes that have equal shapes, and also accepts a raw callable value converted to a callable newtype.

Steps to Reproduce:

1. Declare `newtype fn int First(int value);` and `newtype fn int Second(int value);`.
2. Declare `int read(int value): First => value;`.
3. In a function body, write `First first = read;` and then `Second second = first;`.
4. Build the program.

Expected:
`Second second = first;` is rejected with an invalid implicit conversion, because the two newtypes are nominally distinct (see the callable newtype nominal boundary in the conversion semantics: a named newtype requires an ascription, cast or source rule to cross the boundary).

Actual:
The program compiles with no diagnostic. A callable newtype instantiated from a generic callable newtype (`Equals<int>` to `Matches<int>`) and a raw `fn` value assigned to a callable newtype are accepted as well. Conversions between different instantiations of one generic callable newtype (`Equals<int>` to `Equals<long>`) are rejected.

Known Impact:
A callback intended for one role can be passed where another role is required without any diagnostic. A golden that expects the rejection fails against this compiler.

## BUG-288: Unconstrained generic parameter in a callable result emits mismatched C types

Date/Time: 2026-10-11 19:18 EDT

Summary:
A generic parameter with no constraint carries its values as `nint`, so a callable type formed from it uses the carrier `nint<A>` for the argument `A`. The generic body and the call site disagree about the C representation: the body is emitted with `void *` while the callable passed in is emitted with `intptr_t`.

Steps to Reproduce:

1. Declare `enum Color: int { RED = 10, GREEN = 20 }`.
2. Declare `T make<T>(fn T() source) => source();` with no constraint on `T`.
3. Declare `nint<Color> green() { return Color.GREEN; }`.
4. In `main`, call `Color color = make<Color>(green);`.
5. Build and run the program.

Expected:
The program compiles and `make<Color>(green)` produces `Color.GREEN`; an unconstrained parameter is the `nint` carrier in a callable type.

Actual:
The C compiler rejects the generated body: `return source();` converts `intptr_t` to the function's `void *` result type (`incompatible integer to pointer conversion`). The same mismatch occurs for an unconstrained parameter of a callable parameter, for example `int apply<T>(fn int(T) read, T value)`.

Known Impact:
Callable types over an unconstrained generic parameter cannot be used. Declaring the parameter `T: nint` (or another representation constraint) avoids the problem.

## BUG-289: A function without an ascription converts implicitly to a callable newtype

Date/Time: 2026-10-12 02:29 EDT

Summary:
Callable newtypes are nominal. A function becomes a value of a callable newtype by being ascribed to it in its declaration (`: Name`), or through an explicit cast or other source rule. The compiler also accepts a function that carries no ascription wherever the newtype is required, as long as the calling shapes are structurally compatible.

Steps to Reproduce:

1. Declare `newtype fn int First(int value);`.
2. Declare `int read(int value) => value;` with no ascription.
3. In a function body, write `First first = read;` and call `first(5)`.
4. Build and run the program.

Expected:
The initializer is rejected with an invalid implicit conversion, because `read` is not ascribed to `First` and the structural match alone does not cross the nominal boundary (conversion semantics, callable newtypes preserve nominal boundaries).

Actual:
The program compiles and prints `5`. The same acceptance applies to an instantiation of a generic callable newtype (`Equals<int> equal = sameInt;`).

Known Impact:
Ascription carries no checking weight: any compatible function can be used as the newtype, so the newtype no longer says which role a callback fills. No workaround is needed to compile, but code that relies on the rejection cannot be tested.

## BUG-290: Returning a `void<V>*` as the distributive pointer of an expandable `V` reads wrong components

Date/Time: 2026-10-12 02:59 EDT

Summary:
A pointer to erased storage of an expandable `V` (`void<int?>*`, `void<int[]>*`) converts implicitly to the distributive pointer of `V` (`int?*`, `int[]*`), with one pointer per component addressing that component inside the one storage object. The conversion works for arguments, initializers and assignments, but when the converted pointer is the function's result the caller receives pointers that do not address the storage: the extra component pointers are not set from the storage.

Steps to Reproduce:

1. Declare `int peek(int?* source) => source.specified ? source.value : -1;`.
2. Declare `int?* viaReturn(void<int?>* slot) { return slot; }` (an expression body `=> slot` behaves the same).
3. In `main`, declare `stackalloc void<int?> slot = (void<int?>)9;`, then call `peek(viaReturn(&slot))`.
4. Repeat with `int[]* arrayResult(void<int[]>* slot) => slot;` over `stackalloc void<int[]> held = [4, 5, 6];` and read the array length through the returned pointer.

Expected:
The returned pointer addresses the components of the storage, so `peek` returns `9` and the array length is `3`, as when the same pointer is converted into a local first (`int?* p = slot; return p;`) or passed as an argument.

Actual:
`peek` returns `-1` (the specified bit is not read from the storage) and the array length is a garbage value (`185286154` in one run).

Known Impact:
A function cannot return an expanded erased pointer directly. Converting into a local before returning avoids the problem.

## BUG-291: A `const void<V>*` of an expandable `V` does not expand to the `const V*` distributive pointer

Date/Time: 2026-10-12 03:15 EDT

Summary:
A pointer to erased storage of an expandable `V` (`void<int?>*`, `void<int[]>*`) converts implicitly to the distributive pointer of `V`, preserving qualifiers. A mutable source converts to either a mutable or a const destination, but when the pointee of the source is itself const (`const void<int?>*`), the conversion to the matching const destination (`const int?*`) is rejected, so the components of const erased storage cannot be addressed at all. The same conversion is accepted for a non-expandable payload (`const void<int>*` to `const int*`).

Steps to Reproduce:

1. Declare `int read(const int?* source) { int? current = *source; return current.specified ? current.value : -1; }`.
2. Declare `void viaArgument(const void<int?>* readOnly) { read(readOnly); }` and `void viaLocal(const void<int?>* readOnly) { const int?* view = readOnly; }`.
3. Declare `void viaArray(const void<int[]>* readOnly) { const int[]* view = readOnly; }`.
4. For comparison, declare `void viaMutable(void<int?>* mutable) { const int?* view = mutable; read(view); }` and `void viaMutableArray(void<int[]>* mutable) { const int[]* view = mutable; }`.

Expected:
All of the conversions are accepted: no qualifier is dropped, and the const components alias the const storage.

Actual:
The argument, local-initializer and array forms report `Argument cannot convert 'const void<int?>*' to 'const int?*'` and the matching declaration-initializer errors for `const void<int?>*` and `const void<int[]>*`. The mutable-source comparisons compile.

Known Impact:
Const erased storage of an optional or array cannot be passed to a function taking `const V*`; the storage has to be reached through a mutable pointer instead.

## BUG-292: A brace initializer of a distributive pointer field from a `void<V>*` emits invalid C

Date/Time: 2026-10-12 03:15 EDT

Summary:
A pointer to erased storage of an expandable `V` (`void<int?>*`) converts implicitly to the distributive pointer of `V` (`int?*`). The conversion is accepted for arguments, local initializers and assignments, including assignment to a field. When the same address initializes a distributive pointer field in a brace initializer, the compiler accepts the program but emits C that does not compile: the field's extra component slot receives a boolean constant instead of the component pointer.

Steps to Reproduce:

1. Declare `struct Handle { int?* where; }` and `int read(const int?* source) { int? current = *source; return current.specified ? current.value : -1; }`.
2. In `main`, declare `stackalloc void<int?> slot = (void<int?>)1;` and `Handle handle = { &slot };`, then call `read(handle.where)`.
3. For comparison, replace the initializer with `Handle handle = default; handle.where = &slot;`, with `int?* local = &slot; Handle handle = { local };`, or with an address of an ordinary `int?` local (`Handle handle = { &plain };`).

Expected:
The initializer expands `&slot` into the component pointers, as an assignment does, and `read` returns `1`.

Actual:
The native build fails with `incompatible integer to pointer conversion initializing 'bool *' with an expression of type 'int'` on `(Handle){ .where = (int *)(&slot), .where_specified = true }`. The three comparison forms compile and return the stored value.

Known Impact:
A distributive pointer field cannot be brace-initialized directly from erased storage. Assigning the field afterwards, or expanding into a local first, avoids the problem.

## BUG-293: A pointer to an integer carrier does not convert to a pointer to a const carrier

Date/Time: 2026-10-12 19:03 EDT

Summary:
Adding `const` to the pointee is an implicit conversion for every pointer type (`int*` to `const int*`). For a pointer to an integer carrier (`long<Color>*`) the same conversion is rejected in every context, so a carrier cannot be passed, returned or stored through a read-only pointer.

Steps to Reproduce:

1. Declare `enum Color: int { RED, GREEN }`.
2. Declare `const long<Color>* viaReturn(long<Color>* p) => p;`.
3. Declare `void viaLocal(long<Color>* p) { const long<Color>* q = p; }`.
4. Declare `void viaArgument(const long<Color>* p) {}` and `void caller(long<Color>* p) { viaArgument(p); }`.
5. For comparison, `const int* plain(int* p) => p;` compiles.

Expected:
All three forms are accepted, as for `int*` to `const int*`.

Actual:
Each reports that it cannot convert `long<Color>*` to `const long<Color>*` (return expression, declaration initializer, argument).

Known Impact:
A carrier-typed value cannot be exposed read-only by pointer. Passing the mutable pointer avoids the problem.

## BUG-294: A generated project API omits the interface list of an exported struct

Date/Time: 2026-10-12 20:11 EDT

Summary:
A project that exports an interface and a struct implementing it produces an API declaration in which the struct no longer names the interface. The struct's method keeps its `: Reader` implementation marker, so the API contradicts itself, and a consuming project can neither convert the struct to the interface nor build against the API at all when the method is exported.

Steps to Reproduce:

1. In a producer project, declare `export interface Reader { int read(); }` and `export struct Cell: Reader { int value; int read(): Reader { return this.value; } }`.
2. In a second project that references the producer as a static project reference, write `Cell cell = { 5 }; Reader* r = &cell;`.
3. Build the second project.

Expected:
The generated API declares `export struct Cell : Reader`, so the conversion compiles and `r.read()` dispatches to the struct's method.

Actual:
The generated API declares `export struct Cell` with no interface list and `export extern int read() : Reader;` inside it. The consumer reports `Declaration initializer cannot convert 'Cell*' to 'Reader*'.` If the method is declared `export int read(): Reader`, the generated API itself fails to build with `Method 'read' cannot implement 'Reader' because type 'Cell' does not implement that interface.`

Known Impact:
An exported type cannot be used as an interface, directly or through a generic `implements` constraint, from another project. Keeping the implementing type and its interface in the same project avoids the problem.
