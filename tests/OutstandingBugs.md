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

## BUG-372: A static method used as an interface slot default fails to compile

Date/Time: 2026-10-25 14:10 PDT

Summary:
The diagnostics for an interface method initializer say it may be null, default, a free function or a static method. A free function works. A static method of a struct or class passes semantic analysis and then aborts C emission because the qualified name of the owning type has no resolved type. A static method of a static class is rejected during semantic analysis with the message that lists a static method as allowed.

Steps to Reproduce:

1. Declare `interface LineSink { int write(int value); int writeTwice(int value) = Helper.twice; }`.
2. Declare `struct Helper { static int twice(LineSink* sink, int value) { return sink.write(value) * 2; } }` after the interface.
3. Declare a struct implementing `LineSink` with a marked `write` and call `writeTwice` through a `LineSink*` obtained from it.
4. Repeat with `class Helper` and with `static class Helper`.

Expected:
The program compiles, and calling `writeTwice(10)` through the interface runs the static method. With a `write` that returns `value + 1`, the result is 22.

Actual:
With a struct or a class, C emission aborts with "NamedExpression 'Helper' has unresolved type '#UNRESOLVED'". With a static class, semantic analysis reports "Interface method vtable initializer must be null, default, a free function, or a static method."

Known Impact:
A defaulted interface slot can only use a free function. Qualifying a static method as the default target fails, in one case without a source location.

