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

Next bug number: BUG-330.

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

## BUG-329: A pointer to `const` or `volatile` data converts to `void*` without `unsafe`

Date/Time: 2026-10-22 08:45 EDT

Summary:
Converting a pointer whose pointee is `const` or `volatile` to a plain `void*`
drops the qualifier without `unsafe`, both with an ordinary cast and implicitly.
The conversion rules require `unsafe` to remove `const` or `volatile` from a
typed pointer conversion, and state that converting through `void*` does not
permit removing either qualifier. Recovering a typed pointer from `const void*`
is correctly rejected, so the qualifier can be laundered away in two steps that
never mention `unsafe`.

Steps to Reproduce:

1. Declare `int value = 3;`, `const int* readonlyValue = &value;` and
   `volatile int* observed = &value;`.
2. Write `void* a = (void*)readonlyValue;`, `void* b = (void*)observed;` and
   `void* c = readonlyValue;`.
3. Write `const void* readonlyRaw = readonlyValue;` and `void* d = (void*)readonlyRaw;`.
4. Write `int* e = (int*)(void*)readonlyValue;`.
5. Compile.

Expected:
Every statement in steps 2 to 4 is rejected with the same diagnostic given for
`int* f = (int*)readonlyValue;`, which reports that the cast removes `const` and
requires `(unsafe T*)`. A pointer to `const` data converts to `const void*`, not
to `void*`.

Actual:
All of the statements compile without diagnostics. Only recovering a typed
pointer from `const void*` or `volatile void*`, as in `int* g = (int*)readonlyRaw;`,
reports that the cast removes `const` or `volatile`.

Known Impact:
`const` and `volatile` protection on pointee data can be removed silently by
passing the pointer through `void*`, so a read-only view becomes writable with
no `unsafe` in the source. Code that needs the protection must avoid plain
`void*` parameters and use `const void*`.
