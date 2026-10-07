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

Next bug number: BUG-271.

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
