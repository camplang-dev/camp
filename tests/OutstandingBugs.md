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

Next bug number: BUG-339.

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

## BUG-337: Assignments to static fields skip escaped-storage lifetime checks

Date/Time: 2026-10-22 21:46 EDT

Summary:
Static fields of a class are escaped storage, but storing a scoped pointer-bearing value into one is accepted. The same store into a global variable is rejected.

Steps to Reproduce:

1. Declare a class with a `static` field of a pointer-bearing type (for example `static const char[] lastName;`).
2. In a static method of that class, assign a parameter, or a view of a fixed-size local array, to the field.
3. Compare with assigning the same value to a global variable.

Expected:
Both stores are rejected with the escaped-storage diagnostic, because static fields are escaped storage (`semantics/05-lifetime-analysis-and-flow-facts.md`, Slot Facts And Value Facts and the storage rule list).

Actual:
The global assignment reports "Assignment cannot store a non-escaped pointer-bearing value in escaped storage.". The static field assignments report nothing.

Known Impact:
A dangling pointer to a parameter or local can be left in static storage without a diagnostic.

## BUG-338: `new` of a class does not check pointer-bearing constructor arguments against what the constructor retains

Date/Time: 2026-10-22 21:46 EDT

Summary:
A constructor parameter written `unscoped` may be stored in a field. Value construction retains the argument's lifetime fact, so returning the constructed value is rejected. Heap construction with `new` yields an escaped value but does not check the arguments, so a heap object can hold a pointer to local storage.

Steps to Reproduce:

1. Declare `class Keeper { const char[] text; Keeper(unscoped const char[] input) { this.text = input; } ~Keeper() { } }`.
2. In a function, declare `fixed char[8] buffer = $"abc";` and write `Keeper* made = new Keeper(buffer[..]); return made;`.

Expected:
The construction is rejected, because the escaped object would retain a view of local storage. `semantics/05` (Construction And Retention) says this analysis "prevents code from constructing a heap object that stores a pointer to local stack storage".

Actual:
No diagnostic is reported for the `new` expression or the return.

Known Impact:
Heap objects can retain pointers to local or parameter storage that ends before the object does. The documentation does not currently state the `new` form of the rule; it should be added alongside the fix.
