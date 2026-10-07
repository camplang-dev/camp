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

Next bug number: BUG-267.

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


## BUG-266: Erased `in` array argument is misplaced when the call also passes an `out`

Date/Time: 2026-10-07 09:05 EDT

Summary:
A call to an erased generic that passes an ordinary array to an `in T` parameter and
also passes an `out` argument packs the array argument incorrectly. The temporary meant
to hold the array's elements and length is initialized with the `sizeof(T)` value, and
the array's elements pointer is passed in the size capability's place, so the generated
C does not compile. The same call shape with an optional argument, or without the `out`
argument, works.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
void store<T: copyable>(out T target, in T value, sizeof(T)) { target = value; }
export int main()
{
	int[] source = [1, 2, 3];
	int[] destination = [9];
	store<int[]>(out destination, source);
	return (int)destination.length;
}
```

2. Run `campc run repro.camp --nostdlib`.

Expected:
The program builds and exits with 3. Moving the `in T` parameter before the `out T`
parameter gives the same result.

Actual:
The C compiler reports "incompatible integer to pointer conversion initializing 'int *'
with an expression of type 'unsigned long'" for the packed argument and "incompatible
pointer to integer conversion passing 'int *' to parameter of type 'uintptr_t'" for the
size argument, in either parameter order.

Known Impact:
An array cannot be passed by `in` to an erased generic that also writes an `out`
argument.
