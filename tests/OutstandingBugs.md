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

Next bug number: BUG-104.

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

## BUG-103: Inactive imported array indexing overflows the compiler stack

Date/Time: 2026-09-02 16:24 EDT

Summary:
Directly indexing an array returned by an imported struct instance method can
overflow the compiler stack when the containing source file is disabled by a
file-level requirement. The same expression compiles and runs when that
requirement is enabled.

Steps to Reproduce:

1. Create a static library project that exports a struct containing an array
   field and an instance method returning an array derived from that field.
2. Create a consuming project with a source file beginning with
   [requires(TEST_MODULE);].
3. In that file, index the imported method result with an expression such as
   [value.payload()[index]].
4. Confirm that testing the consuming project with [TEST_MODULE] active passes.
5. Build the consuming project as a static artifact with [TEST_MODULE]
   inactive while leaving the guarded test source in its [.campbuild].

Expected:
Both configurations compile successfully. The inactive file contributes no
executable test declarations to the static artifact, and lowering any retained
conditional representation terminates normally.

Actual:
The active test configuration succeeds. The inactive static build enters
unbounded recursion through expanded-component indexing and generic property
lookup during expression lowering, then terminates with a native stack overflow.

Known Impact:
A reusable [.campbuild] cannot safely include guarded tests that exercise this
valid imported API pattern. Consequently, downstream static project references
cannot build the dependency even though its own test configuration passes.
