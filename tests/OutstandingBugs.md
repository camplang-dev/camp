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

Next bug number: BUG-283.

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

## BUG-285: Passing an element of a `void<V>` array to an `in void<V>` parameter fails in C

Date/Time: 2026-10-10 23:18 EDT

Summary:
Passing `items[i]`, an element of a `void<int?>[]`, to an `in void<int?>`
parameter builds a compound literal of the storage type initialized from the
element, which is invalid C.

Steps to Reproduce:

1. Declare `int read(in void<int?> value)`.
2. Declare `void<int?>[] slots = stackalloc void<int?>[2];` and assign
   `slots[1] = 4;`.
3. Call `read(slots[1])` and build the file.

Expected:
The element storage is passed by address.

Actual:
The generated C calls `read(&(storage){ ((storage *)(slots))[1] })` and the
native compiler rejects initializing an `int` member from a structure.

Known Impact:
An array element cannot be passed to an `in void<V>` parameter. Programs must
read it into a `V` local or pass `&items[i]` with a `void<V>*` parameter.
