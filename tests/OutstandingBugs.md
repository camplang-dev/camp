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

Next bug number: BUG-287.

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
