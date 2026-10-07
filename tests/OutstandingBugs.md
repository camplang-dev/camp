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

Next bug number: BUG-255.

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

## BUG-251: Iterator locals reject scoped pointer-bearing initializers

Date/Time: 2026-10-06 23:33 EDT

Summary:
Inside a `struct iter` generator, declaring a local of a pointer-bearing type
(a view, array or pointer) and initializing it from a scoped value reports that
the assignment cannot store a scoped value in storage tied to `this`. The scoped
value is the iterator's own parameter, or a value derived from it, so the local
has the same lifetime as the parameter that the generated iterator state already
retains. Earlier compilers accepted these declarations, and nothing in the
lifetime semantics forbids them. The rejection began with the change that
introduced named stack-storage handling for generated iterator and async state.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp --nostdlib --artifact none`:

```camp
struct View { const char[] text; }
const char[] span(View this) { return this.text; }

struct iter int a(View this) { const char[] value = this.span(); yield (int)value.length; }
struct iter int b(const char[] text) { const char[] value = text; yield (int)value.length; }
struct iter int c(const int* p) { const int* q = p; yield *q; }

export int main() { return 0; }
```

Expected:
The source compiles. A local copied from a parameter, or from a value derived
from a parameter, is no shorter-lived than the iterator state that holds both.

Actual:
Each local declaration is rejected with `error: Assignment cannot store a scoped
pointer-bearing value in storage tied to 'this'.` Compilers before the stack-storage
suspension change accept the same source.

Known Impact:
Any generator that keeps a view, array or pointer derived from a parameter in a
local no longer compiles. The only workaround is to avoid such locals and use the
parameter expression at every use.

## BUG-252: Returning an array member of a call result drops the call's expanded argument length

Date/Time: 2026-10-06 23:36 EDT

Summary:
When a function returns an array-typed member of a call result, for example
`return make(r).span;`, and the call passes an array or view argument, the generated
C call omits the argument's length component. The native compiler then rejects the
generated C with "too few arguments to function call". Reading a scalar member of the
same call result is generated correctly, as is first storing the call result in a local.
Compilers before the erased-type callable matching and inference change generate the
correct call.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp --nostdlib --out-dir out`:

```camp
extern void __intrinsic_log_i32(int value);
struct Scan { const char[] span; nuint consumed; }
Scan scanOf(const char[] t) { Scan s = { t, 3 }; return s; }
const char[] fromParam(const char[] r) { return scanOf(r).span; }
export int main()
{
	const char[] text = "abcdef";
	__intrinsic_log_i32((int)fromParam(text).length);
	return 0;
}
```

Expected:
The source builds and logs 6. The generated call is `scanOf(r, r_length)`.

Actual:
The generated C declares `Scan _paramsReceiver0 = scanOf(r);`, passing only the
elements pointer, and the C compiler fails with "too few arguments to function call,
expected 2, have 1".

Known Impact:
Any function returning an array member of a call result whose arguments include an
array or view fails to compile natively. Store the call result in a local, then return
its member.

## BUG-253: Nested conditional of a const array value emits an assignment to a const temporary

Date/Time: 2026-10-06 23:39 EDT

Summary:
A conditional expression whose arms are const array or view values, such as string
literals, and whose condition is not a plain local comparison, is emitted with
separate branch assignments to temporaries. The temporary that holds the view's length
is declared `const uintptr_t` and then assigned in both branches, so the native C
compiler rejects the generated code. A single conditional, and a nested conditional
whose conditions only compare locals, are emitted as ternary expressions and are
unaffected.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp --nostdlib --out-dir out`:

```camp
extern void __intrinsic_log_i32(int value);
struct Param { int kind; }
int pick(Param p)
{
	const char[] s = p.kind == 1 ? "aa" : p.kind == 2 ? "bbb" : "c";
	return (int)s.length;
}
export int main() { __intrinsic_log_i32(pick({ 2 })); return 0; }
```

Expected:
The source builds and logs 3.

Actual:
The generated C contains `const uintptr_t _conditionalValue3;` followed by
`_conditionalValue3 = 2;` and the C compiler fails with "cannot assign to variable
'_conditionalValue3' with const-qualified type 'const uintptr_t'". The same happens when
the conditions call a function instead of reading a struct member.

Known Impact:
Any nested conditional producing a const view whose conditions read a member or call a
function fails to compile natively. Compute the value with an `if` chain or a helper
function that returns it.

## BUG-254: Type name shadowed by a same-named property in a static method

Date/Time: 2026-10-06 23:45 EDT

Summary:
Inside a static method of a class, a qualified call on a type whose name matches a
property of that class (an accessor `getCatalog` declares the property `Catalog`) is
rejected with a message saying the property requires explicit `this.` qualification.
A static method has no `this`, and the name is used as the qualifier of a static call,
so it must resolve to the type. Earlier compilers resolve it to the type.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp --artifact none`:

```camp
public class Catalog { public static int begin() { return 1; } }
public class Holder
{
	Catalog* catalog;
	public Catalog* getCatalog() => this.catalog;
	public static int make() { return Catalog.begin(); }
}
export int main() { return Holder.make() - 1; }
```

Expected:
The source compiles. `Catalog.begin()` in the static method calls the static method of
the type `Catalog`.

Actual:
`error: Member property 'Catalog' requires explicit 'this.' qualification.` at the use
of `Catalog` in the static method.

Known Impact:
Any static method that calls a static member through a type name equal to a property
name of its own class fails to compile. Qualify the type with its namespace as a
workaround.
