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

Next bug number: BUG-268.

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

## BUG-267: `within` call to an erased generic with an optional result omits the result storage

Date/Time: 2026-10-07 11:40 EDT

Summary:
A call made under `within (allocator)` to an erased generic whose result is `T?` passes
no result storage. The function is declared with its trailing result pointer, but the
call site passes the allocator in that position, drops one argument and assigns the
returned specified flag to the payload. The same call without `within`, or to a function
whose result is `T`, is correct.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
interface Allocator
{
	escaped void* alloc(nuint size);
	void* realloc(void* ptr, nuint newSize);
	void free(void* ptr);
}
class NullAllocator: Allocator
{
	escaped void* alloc(nuint size): Allocator { return null; }
	void* realloc(void* ptr, nuint newSize): Allocator { return null; }
	void free(void* ptr): Allocator {}
}
T? keep<T: copyable>(in T value, within allocator, sizeof(T)) => value;
export int main()
{
	NullAllocator* nothing = stackalloc NullAllocator();
	Allocator* allocator = nothing;
	int? kept = within (allocator) keep<int>(5);
	return kept.value;
}
```

2. Run `campc run repro.camp --nostdlib`.

Expected:
The program builds and exits with 5.

Actual:
The C compiler reports "too few arguments to function call, expected 4, have 3". The
call is emitted as `int kept = keep(&(int){5}, (void *)(intptr_t)(_allocator1),
sizeof(int));` with the specified flag set to `true` unconditionally.

Known Impact:
An erased generic that takes an allocator cannot return an optional of its type parameter.
