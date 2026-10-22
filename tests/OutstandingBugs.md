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

Next bug number: BUG-332.

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

## BUG-328: A `foreach` loop variable is not scoped to the loop

Date/Time: 2026-10-21 11:15 EDT

Summary:
The loop variable declared in a `foreach` header stays visible after the loop
ends, so a second `foreach` in the same function that reuses the name is
rejected as a redeclaration. The language reference states that the loop
variable is scoped to the loop body.

Steps to Reproduce:

1. In one function, write `foreach (int v in a) use(v);` over an array.
2. After it, in the same function, write a second `foreach (int v in a) use(v);`.
3. Compile.

Expected:
Both loops compile; each `v` exists only within its own loop.

Actual:
The second header reports `Symbol 'v' is already declared in this scope.`

Known Impact:
Sequential loops in one function must use distinct variable names.

## BUG-330: A class derived from one construction of a generic class converts to another construction of it

Date/Time: 2026-10-22 09:28 EDT

Summary:
Constructed generic class types are invariant, so a pointer to `Box<int>` does
not convert to a pointer to `Box<uint>`. That rule holds for a pointer to the
construction itself, but not for a pointer to a class derived from that
construction: the derived class pointer converts implicitly to a pointer to a
different construction of the same generic base.

Steps to Reproduce:

1. Declare `class Box<T> { int tag; }` and `class IntBox: Box<int> { int extra; }`.
2. In a function, write `IntBox* derived = stackalloc IntBox();`.
3. Write `Box<uint>* wrong = derived;`.
4. Compile.

Expected:
The declaration is rejected, as `Box<int>* signed = ...; Box<uint>* wrong = signed;`
is, because the generic arguments differ and the class relationship is only to
`Box<int>`.

Actual:
The program compiles and runs; `wrong` points at the `IntBox` object as if it
were a `Box<uint>`.

Known Impact:
Generic invariance can be bypassed through any derived class, so a base view
with the wrong type arguments reaches code that assumes the other construction.
Writing the exact base (`Box<int>*`) avoids it.
