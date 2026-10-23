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

Next bug number: BUG-358.

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

## BUG-357: A field of an out struct parameter cannot be assigned a value the receiver retains

Date/Time: 2026-10-23 15:11 EDT

Summary:
Assigning a value that a method's receiver retains into a field of an `out`
struct parameter is rejected, while assigning a struct containing the same value
to the whole `out` parameter is accepted. The two forms store the same value into
the same slot, so they should be checked the same way. A lifetime cast to
`unscoped(this)` on the stored value does not help.

Steps to Reproduce:

1. Declare `struct Dep { const byte[] identity; int order; }`.
2. Declare `struct Reader { const byte[] bytes; }` with the methods below.
3. `bool whole(out Dep dependency) { dependency = { this.bytes, 0 }; return true; }`
4. `bool fromField(out Dep dependency) { dependency = default; dependency.identity = this.bytes; return true; }`
5. `bool viaOut(out Dep dependency) { dependency = default; const byte[] identity; if (!this.read(0, out identity)) return false; dependency.identity = identity; return true; }`,
   where `bool read(uint id, out const byte[] result) { result = this.bytes; return true; }`.
6. `bool viaCast(out Dep dependency)`: the same as step 5 with
   `dependency.identity = (unscoped(this))identity;`.
7. Compile.

Expected:
All four methods compile, and a caller whose `Reader` was built from a
caller-provided value can return the filled `Dep` (see the related bug on `out`
arguments of methods on local value receivers). The field assignment is checked
against the same relation as the whole-struct assignment: the value is retained
by the receiver, and the `out` slot receives the receiver-retained relation.

If the relation cannot be established, the diagnostic names the field and the
parameter and suggests an annotation. For example: "Cannot store a value retained
by 'this' in 'dependency.identity': the `out` parameter 'dependency' does not
declare that it holds receiver data. Declare it `out unscoped(this) Dep
dependency`."

Actual:
Step 3 compiles. Steps 4, 5 and 6 are rejected with "Assignment cannot store a
scoped pointer-bearing value in storage tied to 'dependency'". The message gives
no cause and no suggestion.

Known Impact:
Reader types that fill an `out` struct field by field cannot hand receiver-held
views to the caller. The workaround is to build a local struct and assign it as a
whole, which hides the field-level check rather than satisfying it.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md`, Assignment And Storage:
state that a field of an `out` parameter is checked like the parameter as a whole.
