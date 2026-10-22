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

Next bug number: BUG-336.

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

## BUG-328: A `foreach` loop variable is not scoped to the loop

Date/Time: 2026-10-21 11:15 EDT

Summary:
The loop variable declared in a `foreach` header stays visible after the loop
ends, so a second `foreach` in the same function that reuses the name is
rejected as a redeclaration. The language reference states that the loop
variable is scoped to the loop body.

Steps to Reproduce:

1. In one function, write `foreach (int v in a) use(v);` over an array.
2. After it, in the same function, write a second `foreach (int v in a) use(v);`.
3. Compile.

Expected:
Both loops compile; each `v` exists only within its own loop.

Actual:
The second header reports `Symbol 'v' is already declared in this scope.`

Known Impact:
Sequential loops in one function must use distinct variable names.

## BUG-330: A class derived from one construction of a generic class converts to another construction of it

Date/Time: 2026-10-22 09:28 EDT

Summary:
Constructed generic class types are invariant, so a pointer to `Box<int>` does
not convert to a pointer to `Box<uint>`. That rule holds for a pointer to the
construction itself, but not for a pointer to a class derived from that
construction: the derived class pointer converts implicitly to a pointer to a
different construction of the same generic base.

Steps to Reproduce:

1. Declare `class Box<T> { int tag; }` and `class IntBox: Box<int> { int extra; }`.
2. In a function, write `IntBox* derived = stackalloc IntBox();`.
3. Write `Box<uint>* wrong = derived;`.
4. Compile.

Expected:
The declaration is rejected, as `Box<int>* signed = ...; Box<uint>* wrong = signed;`
is, because the generic arguments differ and the class relationship is only to
`Box<int>`.

Actual:
The program compiles and runs; `wrong` points at the `IntBox` object as if it
were a `Box<uint>`.

Known Impact:
Generic invariance can be bypassed through any derived class, so a base view
with the wrong type arguments reaches code that assumes the other construction.
Writing the exact base (`Box<int>*`) avoids it.

## BUG-332: An `out` argument for a `constof` result is held to input equality instead of being checked after substitution

Date/Time: 2026-10-22 12:05 EDT

Summary:
The language semantics treat a `constof(anchor)` slot in an `out` parameter as
an output position: the caller-visible type is substituted from the anchor
argument, and the `out` target is then checked by ordinary storage conversion,
so a mutable result may be stored into a `const` target. The anchor-equality
rule applies only to non-output parameters. An `out` argument whose target is
`const` is rejected when the anchor argument is mutable.

Steps to Reproduce:

1. Declare `void first(const byte[] source, out constof(source) byte* result)` with a body that assigns `result = source.elements;`.
2. In a function, declare a mutable `byte[] mutableBytes` and a local `const byte* constResult = null;`.
3. Write `first(mutableBytes, out constResult);`.
4. Compile.

Expected:
The call is accepted, as the semantics document shows for this exact shape
("OK by widening"): the substituted result is `byte*` and widens to the `const`
target.

Actual:
The call is rejected with `Argument constness is const, but constof anchor 'source' requires mutable.`

Known Impact:
A caller cannot receive a dependent-const `out` result into a `const` local
when the anchor is mutable. Declaring a mutable local avoids it.

## BUG-333: A callable-typed value with a `constof` signature rejects a function of the identical signature and does not substitute its results

Date/Time: 2026-10-22 12:10 EDT

Summary:
Assigning or converting a function to an `fn` type uses callable compatibility,
and a function declared with `constof(anchor)` slots is compatible with an `fn`
type written with the same slots. The compiler instead checks a declaration or
assignment whose type is such an `fn` as if the initializer were a data value
that must be derived from the anchor, and rejects it. Passing the function as an
argument for a parameter of that `fn` type is rejected as well, because the
parameter type has lost its dependent slot. Calling a value of such an `fn` type
does not substitute the result from the anchor argument, so a mutable argument
still yields a `const` result.

Steps to Reproduce:

1. Declare `constof(source) byte* first(const byte[] source)` returning `source.elements`.
2. Declare `fn constof(source) byte*(const byte[] source) callback = first;` as a global, and again as a local in a function; also declare it with a `null` initializer and then assign `callback = first;`.
3. Declare `void take(fn constof(source) byte*(const byte[] source) callback)` and call `take(first);`.
4. In a function with `byte[] items`, call `byte* result = callback(items);` on a value of that `fn` type.
5. Compile.

Expected:
All of these are accepted: the function and the `fn` type have the same
dependent-const signature, and the call result is substituted to a mutable
`byte*` for the mutable `items`.

Actual:
Each initializer or assignment is rejected with `Global initializer with constof(source) must be derived from 'source' or use an explicit constof(source) cast.` (or the `Declaration initializer` / `Assignment` form). The argument is rejected with `Argument cannot convert 'fn const byte*(const byte[])' to 'fn byte*(const byte[])'`. The call through the value is rejected with `Call result cannot convert 'const byte*' to 'byte*'.`

Known Impact:
A callable value with a `constof` signature cannot be created, stored, passed or
called with its dependent result; only callable newtype ascription carries such a
signature. The same check blocks a `delegate` declaration with a `constof(this)`
result initialized from a bound method.

## BUG-334: A bound method reference does not substitute `constof(this)` from the bound receiver

Date/Time: 2026-10-22 12:15 EDT

Summary:
The semantics state that forming a bound method reference makes the receiver's
constness the call-site anchor fact, so the bound receiver decides whether the
callable's `constof(this)` result is mutable or const. A reference bound to a
mutable receiver still yields a callable whose result is `const`, although
calling the method directly on the same receiver yields a mutable result.

Steps to Reproduce:

1. Declare `constof(this) byte* head(const byte[] this)` returning `this.elements`.
2. In a function, declare a mutable `byte[] items`.
3. Write `byte* direct = items.head();`, which compiles.
4. Write `auto bound = items.head;` and then `byte* viaBound = bound();`.
5. Compile.

Expected:
Step 4 compiles: the receiver is mutable, so the bound callable returns `byte*`.
A reference bound to a `const byte[]` receiver returns `const byte*`.

Actual:
Step 4 is rejected with `Call result cannot convert 'const byte*' to 'byte*'.`
The bound callable's type is `delegate const byte*()` for a mutable receiver,
and the same happens for a method of a struct declared with `constof(this)` and
a `const this` receiver.

Known Impact:
A bound `constof(this)` method loses the mutable result for a mutable receiver.
Calling the method directly instead of binding it avoids it.

## BUG-335: Callable ascription does not preserve the overload selector name or the pre-selector shape

Date/Time: 2026-10-22 12:20 EDT

Summary:
For a callable surface that participates in an overload family, the semantics
require a compatible candidate to preserve whether the declaration is an
overload-family entry, the selector parameter name, the selector position, and
the pre-selector shape including parameter names. Virtual overrides enforce the
selector name and the family category, but callable newtype ascription does not:
a candidate with a renamed selector, with an ordinary parameter in the selector
position, or with renamed pre-selector parameters is accepted. A moved selector
and a changed pre-selector type are rejected.

Steps to Reproduce:

1. Declare `newtype fn void Setter(const char[] key, overload int value);`.
2. Declare `void renamedSelector(const char[] key, overload int other) : Setter {}`.
3. Declare `void ordinaryEntry(const char[] key, int value) : Setter {}`.
4. Declare `void renamedBefore(const char[] name, overload int value) : Setter {}`.
5. Compile.

Expected:
Each of steps 2 to 4 is rejected: the selector is renamed, the declaration is
not an overload-family entry, and the pre-selector parameter name changed.

Actual:
All three declarations are accepted. An override that renames the selector is
rejected with `Override 'setInt' must preserve the base declaration's overload selector name.`

Known Impact:
A callable newtype for an overload family can be satisfied by a declaration
with a different selector contract, so the shared keys and metadata the
selector is meant to keep stable can differ between the target and the
candidate.

