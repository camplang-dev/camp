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

Next bug number: BUG-373.

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


## BUG-373: A sealed method can be overridden again in a derived class

Date/Time: 2026-10-26 13:06 EDT

Summary:
A method declared `sealed` in a virtual class is documented as the point where overriding stops. The compiler accepts a later `override` of the sealed method in a class derived from the sealing class, with no diagnostic. The override replaces the sealed implementation at run time.

Steps to Reproduce:

1. Declare `virtual class Base { virtual int get() { return 1; } }`.
2. Declare `virtual class Middle: Base { sealed int get() { return 2; } }`.
3. Declare `sealed class Leaf: Middle { override int get() { return 3; } }`.
4. Compile a program whose `main` creates a `Leaf` with `stackalloc` and returns `get()`.

Expected:
The compiler rejects the override in `Leaf`, because `get` was sealed by `Middle` and can no longer be overridden.

Actual:
The program compiles without any diagnostic and returns 3, so the sealed method is replaced.

Known Impact:
`sealed` on a method has no effect on classes that derive from the class that seals it, so a base class cannot protect a method from being overridden further down the hierarchy.
