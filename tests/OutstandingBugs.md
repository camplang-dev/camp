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

Next bug number: BUG-386.

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

## BUG-385: Virtual method dispatch call drops an array parameter's length when the method also returns an array

Date/Time: 2026-10-26 20:15 EDT

Summary:
A virtual method that takes an ordinary array parameter and also returns an ordinary array generates a dispatch body that forwards too few arguments to the implementation. The array parameter's length is omitted from the forwarded call, so the generated C does not compile. The same shapes compile and run correctly when the method has only the array parameter or only the array result.

Steps to Reproduce:

1. Declare a `virtual` class with a virtual method `virtual int[] values(int[] input) { return input; }`.
2. Derive a sealed class from it that does not override the method.
3. Construct the derived class with `stackalloc` through a pointer to the base, call `values` on it, and build the program with `campc build` (or `campc run`).

Expected:
The program builds. The dispatch body for `values` forwards the receiver, the input array pointer and its length, and the result length address to the implementation, as it does for a method with only an array parameter.

Actual:
The native compile of the generated C fails: `error: too few arguments to function call, expected 4, have 3`, at the dispatch body's call through the class vtable (`this->_vt->values(this, input, result_length)`). The input array's length argument is missing.

Known Impact:
Any virtual or abstract method with both an array parameter and an array result cannot be compiled. Workaround: return the array through an `out` parameter or take a fixed-size or pointer parameter instead.
