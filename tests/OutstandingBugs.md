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

Next bug number: BUG-121.

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

## BUG-116: Conditional expression can leave a slice result uninitialized

Date/Time: 2026-09-03 America/Toronto

Summary:
A conditional expression whose branches produce borrowed array slices can lower
to generated C that declares the result slice without assigning either branch.
The resulting program may read an invalid pointer and crash despite compiling
without a diagnostic.

Steps to Reproduce:

1. Declare a local borrowed array-slice variable initialized by a conditional
   expression whose two branches are distinct slices of a source array.
2. Pass that local to another method.
3. Compile and run the program.

Expected:
The generated program assigns the selected branch slice, preserving its pointer
and length, and the called method receives that selected slice.

Actual:
The generated C declares the slice local but leaves its pointer and length
uninitialized before passing it to the called method. The executable can crash.

Known Impact:
Code using a conditional expression to select a slice is unsafe until fixed.
An equivalent branch that calls the consumer separately for each selected slice
avoids the faulty lowering.

## BUG-120: Static project archives embed stale transitive objects

Date/Time: 2026-09-03 20:10 America/Toronto

Summary:
A static project-reference archive can contain object files copied recursively
from its referenced projects, sometimes more than once. When an upstream
project implementation changes without changing its public API, the downstream
archive can remain current and retain the old upstream object. A final
executable that references both projects can then link the stale transitive
object instead of the newly rebuilt upstream object.

Steps to Reproduce:

1. Create static project A, static project B referencing A, and executable C
   referencing both A and B.
2. Build C and inspect B's archive membership; observe that it contains A's
   object files, potentially multiple times.
3. Change an implementation in A without changing A's public API and rebuild C.
4. Observe that A rebuilds while B is considered current.
5. Run C and observe the old A behavior selected from B's archive.

Expected:
B's static archive contains only B's own objects, while its project-reference
metadata causes the final link to include A's current archive exactly once. An
upstream implementation change therefore cannot leave stale upstream objects
inside a downstream archive.

Actual:
B's archive recursively contains A's objects. B is not rebuilt after A's
implementation changes, and the final executable can select B's stale embedded
copy even though A's current archive and the executable itself were rebuilt.

Known Impact:
Multi-module programs can silently run obsolete code after a successful normal
build. Removing and rebuilding every downstream archive that embeds the changed
project is a temporary workaround, but it is not reliable for incremental
compiler development.
