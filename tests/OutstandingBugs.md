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

Next bug number: BUG-273.

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

## BUG-270: delegate call with a generic array argument passes one packed value

Date/Time: 2026-10-07 15:10 EDT

Summary:
Inside a generic function, calling a delegate whose parameter is `T[]` with a `T[]`
argument emits one packed struct holding the elements and the length, instead of the two
separate components the delegate's C function type takes. The generated C does not compile.
Calling a delegate with a `T*` argument, or a non-generic `int[]` delegate, works.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
int countOf(int[] values) => (int)values.length;
int across<T: copyable>(T[] values, delegate int(T[]) look) => look(values);
export int main()
{
	int[] numbers = [6, 7];
	return across<int>(numbers, countOf);
}
```

2. Run `campc run repro.camp --nostdlib --show-errorlevel`.

Expected:
The program runs and exits with 2.

Actual:
The C compiler reports `too few arguments to function call, expected 3, have 2` at the
delegate call, which passes the context and a struct `{ elements, length }`.

Known Impact:
A generic cannot pass its array parameter to a delegate. Workaround: pass the elements
pointer and the length separately.

## BUG-271: generic interface dispatch passes an `in` scalar argument by value

Date/Time: 2026-10-07 13:50 EDT

Summary:
Inside a generic constrained by an interface, calling an interface method through the
`vtableof` capability passes a scalar `in` argument by value, although the method's C
function takes a pointer for that parameter. The generated C does not compile. Calling the
same method directly on the concrete type works.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
interface IScale { int scale(in int x); }
struct Doubler: IScale
{
	int factor;
	int scale(in int x): IScale => x * this.factor;
}
int viaGeneric<T: implements IScale>(T* s, in int x, vtableof(T: IScale)) => s.scale(x);
export int main()
{
	Doubler d = { 3 };
	return viaGeneric<Doubler>(&d, 5);
}
```

2. Run `campc run repro.camp --nostdlib --show-errorlevel`.

Expected:
The program runs and exits with 15.

Actual:
The C compiler reports `incompatible integer to pointer conversion passing 'int' to
parameter of type 'int *'` at the vtable call. Declaring the generic's own parameter as a
plain `int` gives the same error.

Known Impact:
A generic cannot call an interface method that takes a scalar `in` parameter. Workaround:
declare the interface parameter without `in`.

## BUG-272: a narrower local passed to an `in` parameter is read at the wider type

Date/Time: 2026-10-07 13:55 EDT

Summary:
Passing a local of a narrower integer type to a scalar `in` parameter of a wider type,
through an implicit widening conversion, passes the address of the local itself instead of
the address of a widened temporary. The callee reads bytes beyond the local. A field or
other non-local argument is widened into a temporary correctly.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
extern void __intrinsic_log_i32(int value);
int twice(in int x) => x * 2;
export int main()
{
	short narrow = -1;
	__intrinsic_log_i32(twice(narrow));
	return 0;
}
```

2. Run `campc run repro.camp --nostdlib --show-errorlevel`.

Expected:
The program prints `-2`.

Actual:
The program prints an arbitrary value (for example `-331218946`); the C compiler warns
about passing `int16_t *` to a parameter of type `int *`.

Known Impact:
Any implicitly widened local passed to an `in` parameter reads undefined memory.
Workaround: cast the argument explicitly, as in `twice((int)narrow)`.
