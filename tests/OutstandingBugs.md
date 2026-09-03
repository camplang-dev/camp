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

Next bug number: BUG-116.

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

## BUG-115: Inactive interface implementation body is still lowered

Date/Time: 2026-09-02 20:17 EDT

Summary:
A file-wide false [requires] condition does not prevent lowering of a helper
class that implements an interface declared by an active source file in the
same module. An array-index expression in the inactive implementation body is
repeatedly lowered until the compiler exhausts its native stack.

Steps to Reproduce:

1. Create a static-library project containing active production sources and a
   test source included by the same build file.
2. Declare an interface in an active source with a method that accepts a byte
   slice.
3. Begin the test source with [requires(TEST_MODULE);], then declare a class
   implementing that interface.
4. In the implementation method, copy bytes in a loop using an indexed field
   slice as the source expression.
5. Build the project as a static artifact without defining [TEST_MODULE].

Expected:
The file-wide requirement makes every declaration and body in the test source
inactive. The static library builds without lowering the implementation body.

Actual:
Lowering enters the inactive implementation body and repeatedly lowers the
indexed expression until the compiler terminates with a native stack overflow.
Building the same production sources without the guarded test source succeeds.

Known Impact:
Reusable module build files cannot include guarded tests containing this
interface-helper pattern, so downstream static project references cannot build
the affected module. Moving the namespace statement before [requires] happens
to avoid the failure but should be semantically irrelevant and violates the
beta repository convention for guarded test files.
