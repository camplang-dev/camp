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

Next bug number: BUG-411.

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

## BUG-410: Shadow classes are not treated as escaped types for in-frame storage and stackalloc

Date/Time: 2026-10-28 10:15 EDT

Summary:
The shadow-class semantics say a shadow class is an escaped class type and may
not be constructed directly with selected construction or `stackalloc`. The
compiler rejects an in-frame declaration, a selected construction and a
`stackalloc` of an ordinary `escaped` class, but accepts all three for a shadow
class. The semantic phase reports nothing, and the generated C then fails to
build because the shadow class is only a forward-declared struct there.

Steps to Reproduce:

1. Declare an ordinary class with a constructor that has `@getshadow` and
   `@setshadow` methods, and a `shadow` class that derives from it and declares
   a constructor taking one `int`.
2. In a function, write `S b = S(2);`, `auto c = S(3);`, `S* f = stackalloc S(1);`
   and `S g;`, where `S` is the shadow class.
3. Build the program.
4. Replace the shadow class with an `escaped` class and build again.

Expected:
Each of the four statements is rejected during semantic analysis, as it is for
the `escaped` class in step 4, with the escaped-type diagnostics for an
in-frame declaration (three statements) and for `stackalloc` (one statement).

Actual:
The `escaped` class produces the expected semantic errors, "Storage of an
escaped type cannot be declared in an activation frame; use static storage or
new." and "An escaped type cannot be allocated on the stack; use static storage
or new." The shadow class produces no semantic diagnostic, and the native build
fails with errors such as "variable has incomplete type" and "invalid
application of 'sizeof' to an incomplete type" for the shadow class.

Known Impact:
Invalid direct construction of a shadow class is reported as a native compiler
failure instead of a source diagnostic. Allocating with `new` is unaffected.
