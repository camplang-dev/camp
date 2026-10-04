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

Next bug number: BUG-227.

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

## BUG-226: C emission calls an inactive gated declaration in a guarded short-circuit operand

Date/Time: 2026-10-04 19:48 EDT

Summary:
A declaration with a `requires (F)` requirement is not emitted when `F` is false
in the selected configuration. A call to it that is guarded by
`configured(F) && gated()` passes semantic analysis, but the emitted C still
contains the call, with the guard folded to `false`. The C compiler then fails
because the function was never declared or defined. The same guard written as an
`if` statement or a conditional expression works, because the inactive branch is
dropped.

Steps to Reproduce:

1. Build with `-d F=false` and `--nostdlib`.
2. Compile:
   ```camp
   extern void __intrinsic_log_i32(int value);
   requires (F) int onlyF() { return 11; }
   export int main()
   {
   	if (configured(F) && onlyF() == 11) __intrinsic_log_i32(1);
   	__intrinsic_log_i32(2);
   	return 0;
   }
   ```

Expected:
The program builds and prints `2`. The guarded operand of `&&` (and of `||` with a
negated or disjoint guard) is not reachable when the guard is false, so the call
to the inactive declaration must not be emitted.

Actual:
Semantic analysis succeeds, then the native build fails:
`call to undeclared function 'onlyF'` at `if ((false && (onlyF() == 11)))` in the
generated C.

Known Impact:
Any guarded use of a gated declaration inside a `&&` or `||` operand fails to
build when the requirement is false for the selected target. Workaround: guard
with an `if` statement or a conditional expression instead.

