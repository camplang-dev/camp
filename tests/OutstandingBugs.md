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

Next bug number: BUG-275.

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

## BUG-273: a generic `T?` result call omits its result storage when a delegate is passed

Date/Time: 2026-10-07 16:40 EDT

Summary:
Calling an erased generic whose result is `T?` and which takes a delegate parameter emits
the call without the trailing result storage argument that the callee's C function takes.
The generated C does not compile. The same generic with an `fn` parameter instead of a
delegate, or with no callable parameter, is called correctly.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
T? maybe<T: copyable>(delegate bool(int) look, int index, sizeof(T)) => default;
bool yes(int index) => true;
export int main()
{
	int? hit = maybe<int>(yes, 6);
	return hit.specified ? 1 : 2;
}
```

2. Run `campc run repro.camp --nostdlib --show-errorlevel`.

Expected:
The program runs and exits with 2.

Actual:
The C compiler reports `too few arguments to function call, expected 5, have 4` at the call
of `maybe`; the result storage pointer is missing.

Known Impact:
A generic that returns `T?` cannot take a delegate. Workaround: take an `fn` value instead.

## BUG-274: an `fn T?` parameter of an erased generic has the wrong C function type

Date/Time: 2026-10-07 16:50 EDT

Summary:
In an erased generic, an `fn` parameter whose result is `T?` is given the C type
`void *(*)(..., bool *specified)` instead of the erased optional result shape, a `bool`
result with a trailing `void *` result storage. A function with a `bool` result and a
trailing `out` parameter, which the callback rules accept for it, then has an incompatible
C type and the generated C does not compile.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
T? maybe<T: copyable>(fn T?(int) look, int index, sizeof(T)) => look(index);
bool intOut(int index, out int result)
{
	result = index;
	return index > 0;
}
export int main()
{
	int? hit = maybe<int>(intOut, 6);
	return hit.specified ? hit.value : 99;
}
```

2. Run `campc run repro.camp --nostdlib --show-errorlevel`.

Expected:
The program runs and exits with 6.

Actual:
The C compiler reports `incompatible function pointer types passing 'bool (*)(int, void *)'
to parameter of type 'void *(*)(int, bool *)'` at the call of `maybe`.

Known Impact:
An erased generic cannot take an `fn` callback with an optional erased result. A delegate
callback hits BUG-273 instead.

