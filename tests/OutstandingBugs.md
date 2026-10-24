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

Next bug number: BUG-364.

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

## BUG-363: A constructor may declare a thrown parameter

Date/Time: 2026-10-24 16:52 EDT

Summary:
A constructor cannot be a throwing callable: construction is not allowed to report failure through a `thrown`
parameter. The compiler accepts a `thrown` parameter on a class constructor, a struct constructor and an
interface constructor without any diagnostic. The restriction was previously only an API convention, kept in
place because the compiler did not reject the form itself.

Steps to Reproduce:

1. Compile the following program:

   ```camp
   class Item { Item(thrown int error) {} }
   struct Pair { int x; Pair(thrown int error) { this.x = 0; } }
   export int main() { return 0; }
   ```

2. Repeat with `export class Item { export Item(thrown int error) {} }`, and with an interface constructor
   such as `IMade(thrown int error, within allocator);`.

Expected:
Every constructor that declares a `thrown` parameter is rejected with a diagnostic stating that a constructor
cannot declare a thrown error parameter.

Actual:
All of the programs compile without a diagnostic, and `new Item(catch int error)` is accepted at a call.

Known Impact:
Code can declare and call constructors that throw, a form the language does not allow. Programs that do so
today will start to fail when this is fixed.
