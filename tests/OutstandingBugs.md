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

Next bug number: BUG-370.

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

## BUG-369: A stackalloc array or pointer local is rejected in an iterator

Date/Time: 2026-10-24 20:45 EDT

Summary:
A `stackalloc` array or pointer local in an iterator is rejected at its declaration, whether or not it is ever
yielded and however it is used. Owner ruling: the analysis of a `stackalloc` array or pointer is not the same
as the analysis of a named `stackalloc` storage local, and the storage of an array or pointer is hard to track
precisely. The only check wanted for them in an iterator is that a scoped view of the storage cannot be
yielded, the same restriction that stops such a view being returned. What an iterator does with the array or
pointer after a `yield` is left to the author.

Steps to Reproduce:

1. Compile the following program:

   ```camp
   struct W { int n; }
   int sum(const char[] text) { return (int)text.length; }
   struct iter int onlyBeforeYield()
   {
   	char[] scratch = stackalloc char[16];
   	scratch[0] = 'a';
   	int value = (int)scratch[0];
   	yield value;
   }
   struct iter int passedToHelper()
   {
   	char[] scratch = stackalloc char[16];
   	yield sum(scratch);
   }
   struct iter int pointerLocal()
   {
   	W* w = stackalloc W();
   	w.n = 3;
   	int value = w.n;
   	yield value;
   }
   struct iter int reassignedAfterYield()
   {
   	char[] scratch = stackalloc char[16];
   	scratch[0] = 'a';
   	yield (int)scratch[0];
   	scratch = stackalloc char[16];
   	scratch[0] = 'b';
   	yield (int)scratch[0];
   }
   export int main() { return 0; }
   ```

Expected:
The program compiles. Each iterator yields a copy or a plain value, not a view of the storage. A `yield` of the
array or of an alias of it is still rejected (BUG-368 is about where that error is reported).

Actual:
Every `stackalloc` initializer or assignment is rejected:

```text
error: Assignment cannot store a scoped pointer-bearing value in storage tied to 'this'.
```

at the first iterator's `stackalloc char[16]`, the helper iterator's, the pointer iterator's, and both
`stackalloc` expressions of the last iterator.

Known Impact:
Iterators cannot use `stackalloc` scratch arrays or pointers at all, even for work that finishes before the next
`yield`. Workaround: do the work in a helper function called from the iterator and yield its result.
