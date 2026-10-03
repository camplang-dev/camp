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

Next bug number: BUG-217.

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

## BUG-216: A method called through a const-qualified pointer receives the pointer's address

Date/Time: 2026-10-03 17:14 EDT

Summary:
When a method is called through a receiver whose type is a pointer with a
top-level `const` (`T* const`, either declared that way or produced by
dereferencing a `T* const*`), the generated call passes the address of the
pointer value instead of the pointer itself, as if the receiver were a struct
value. The method then reads its fields from the wrong storage. Field reads
through the same pointer are correct, and a method called through
`const T* const` is correct. A top-level `const` only stops the pointer from
being reassigned; member access through it follows the ordinary pointer rule.

Steps to Reproduce:

1. Declare `struct Pair { int a; int getA() { return this.a; } }`.
2. Declare `int methodTop(Pair* const pair) => pair.getA();`.
3. In `main`, set a `Pair`'s `a` to 3, log `methodTop(&pair)`, and run.

Expected:
The program logs `3`, the same as calling `getA()` through a plain `Pair*`.

Actual:
The program logs an unrelated value. The generated C passes `&pair` (the
address of the `Pair* const` parameter) to `Pair_getA`; the same happens for a
`Counter* const` local and for `(*counter).getN()` through `Counter* const*`.

Known Impact:
Methods called through a top-level const pointer read garbage. Copy the
pointer into an unqualified pointer local first, or read the fields directly.
