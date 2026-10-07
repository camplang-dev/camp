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

Next bug number: BUG-270.

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

## BUG-269: An allocator selector inherits the result type of a `within` expression

Date/Time: 2026-10-07 13:33 EDT

Summary:
An allocator-returning call used as the context of a `within` expression is
checked against the destination type of the wrapped expression. Allocator
selection and the wrapped call have independent result types.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
struct Allocator {}
Allocator* select(Allocator* allocator) => allocator;
T? keep<T: copyable>(in T value, within allocator, sizeof(T)) => value;
export int main()
{
	Allocator* allocator = null;
	int? result = within (select(allocator)) keep<int>(5);
	return result.value;
}
```

2. Run `campc build repro.camp --nostdlib --artifact none`.

Expected:
The program compiles. The allocator expression has type `Allocator*` and the
wrapped result has type `int?`.

Actual:
The compiler reports "Call result cannot convert 'Allocator*' to 'int?'."

Known Impact:
Allocator selector calls are rejected when their enclosing expression has a
different destination type. Evaluate the selector into a local first as a
workaround.
