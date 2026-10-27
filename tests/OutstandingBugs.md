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

Next bug number: BUG-389.

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

## BUG-388: Diagnostics for a missing or mismatched base constructor call mention `op_initnew()` and suggest constructor overloads

Date/Time: 2026-10-26 23:00 EDT

Summary:
Constructors cannot be overloaded, but the diagnostics for a derived class that does not call, or wrongly calls, a base class constructor are worded as if a base class could have several constructors, and one of them names the hidden constructor symbol `op_initnew()`. They also point at the wrong place: the missing-call errors are reported on the base class constructor instead of on the derived declaration that has to change.

Steps to Reproduce:

Declare `class Root { Root(int x) { } }` (with the usual `__intrinsic_log_i32`, `malloc` and `free` externs) and build each of these with `campc run`:

1. `sealed class A: Root { A() { } }`
2. `sealed class B: Root { }`
3. `sealed class D: Root { D() { base(); } }`
4. `sealed class F: Root { F() { base(1, 2); } }`

Expected:
Constructors are never called `op_initnew()` in a diagnostic; the message calls it the base class constructor and does not suggest that more than one exists.

1. On the derived constructor: "The constructor for `Root` has arguments, so this constructor must start with a base(...) statement to call it."
2. On the base class name in the derived class declaration: "The constructor for base class `Root` has arguments, so this constructor must declare a constructor that calls it."
3. and 4. The same diagnostic as a call to a method with the wrong arguments, naming the base class constructor, on `base`.

Actual:
1. and 2. Both report "There is no argument given that corresponds to the required parameter 'x' of 'op_initnew()'." on the name of the base class constructor `Root`.
3. "No accessible constructor on base class 'Root' accepts 0 argument(s)." on `base`.
4. "No accessible constructor on base class 'Root' accepts 2 argument(s)." on `base`.

Known Impact:
The messages are confusing and point away from the code that must change. No workaround is needed; the programs are correctly rejected.
