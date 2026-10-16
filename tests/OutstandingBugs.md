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

Next bug number: BUG-296.

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

## BUG-295: Iterator generators accept yielding the address of stackalloc storage

Date/Time: 2026-10-15 20:20 EDT

Summary:
A source iterator generator may declare `stackalloc` locals, whose storage lives
only in a single resume activation and is not lifted into the iterator state.
Yielding the address of such a local (directly, or through an ordinary local
pointer that still holds the address) is accepted. The generated `next` function
stores that activation-stack address into the consumer's result slot and returns,
so the consumer receives a dangling pointer. Generic (`stackalloc T`) and
concrete erased (`stackalloc void<V>`) storage are both affected.

Steps to Reproduce:

1. Compile a generator that yields `&local` of a `stackalloc` local, for example
   `struct iter T* f<T>(const T[] values, sizeof(T)) { stackalloc T local = values[0]; yield &local; }`.
2. Repeat with an alias: `T* p = &local; yield p;`, and with
   `stackalloc void<int?> local = 3; yield &local;`.

Expected:
Each yield is diagnosed, because the storage does not outlive the activation. Yielding the value by copy, or a pointer to storage that outlives the iterator, remains valid.

Actual:
All of these compile without a diagnostic. The generated code stores an
`alloca`-backed address into the output slot.

Known Impact:
Generators over unknown-size or erased storage can silently produce dangling
pointers. Workaround: yield the value instead of its address.
