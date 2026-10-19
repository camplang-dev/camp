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

Next bug number: BUG-311.

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

## BUG-310: `classtype` is rejected in result positions of virtual, abstract and override methods

Date/Time: 2026-10-19 09:48 EDT

Summary:
The language semantics allow `classtype` in a virtual, abstract, override or
sealed method only in result positions: the return type and a direct `out`
parameter type. Input parameters and callable types nested in the signature are
the rejected positions. The compiler rejects every use of `classtype` in a
virtual or abstract method, including the permitted result positions, with a
diagnostic that claims `classtype` is outside a class declaration. A second
diagnostic with no source location ("`classtype` may not be used in fields or
static fields") is also produced.

Steps to Reproduce:

1. Compile a program containing an abstract class that declares
   `abstract classtype* clone();` and
   `abstract void getPeer(out classtype* peer);`.
2. Compile a program containing a virtual class that declares
   `virtual classtype* clone() { return this; }`.
3. Declare a class derived from either one with
   `override classtype* clone() { return this; }`.

Expected:
Steps 1 and 2 compile. The override in step 3 is accepted and keeps `classtype`
in its result. A call through a derived static receiver type rebinds the result
to that type, and a call through the base static type yields the base type.
Only `classtype` in an input parameter, or inside a nested callable, iterator or
async type of a virtual, abstract, override or sealed signature, is diagnosed
(for example `abstract void compareTo(classtype* other);`).

Actual:
Each declaration in steps 1 and 2 fails with "'classtype' is valid only inside
class declarations", even though it is inside a class declaration. The
location-less "'classtype' may not be used in fields or static fields" error
accompanies it, and follow-on errors report that the override does not match an
inherited virtual or abstract method. The permitted result positions therefore
cannot be used, and the rejection of input and nested-callable positions cannot
be observed separately from the rejection of valid ones.

Known Impact:
Virtual and abstract class-relative result contracts, such as a polymorphic
`clone`, cannot be declared. The workaround is to return the declaring class
type and cast at each call site.
