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

Next bug number: BUG-115.

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

## BUG-114: Prelude ordering changes inactive imported-result lowering

Date/Time: 2026-09-02 19:58 EDT

Summary:
An inactive test source overflows lowering when its file-level
[requires(TEST_MODULE);] directive precedes the namespace declaration. The
semantically equivalent case with the namespace declaration before [requires]
succeeds. Both declarations are valid source-prelude constructs, and Camp does
not require them to appear in either order.

Steps to Reproduce:

1. Create adjacent static-library modules that use the same flat namespace and
   include [src/*.camp] and [tests/*.camp] from reusable build files.
2. In the consumer test file, put [requires(TEST_MODULE);] on the first line,
   followed by the namespace declaration.
3. In that test file, index an array returned by an imported instance method.
4. Build the consumer as a static artifact without [TEST_MODULE].
5. Compare with a fixture that places the namespace declaration before the
   [requires] directive.

Expected:
Both source-prelude orderings make the test source inactive and produce the
same successful static build without lowering its body.

Actual:
The leading-requirement form recursively enters imported property/index
lowering until the compiler terminates with a native stack overflow. The
namespace-first fixture succeeds.

Known Impact:
The guarded-test layout required by beta repository policy cannot be included
safely in reusable module build files when a test contains this imported-array
pattern. Reordering the prelude happens to avoid the failure, but should not
change compiler behavior and violates that repository convention.
