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

Next bug number: BUG-385.

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


## BUG-384: The semantic documentation does not define destructor chaining, sealing or the boundary call

Date/Time: 2026-10-26 14:20 EDT

Summary:
The documentation for destructors says only that lowering must preserve base destruction order, and that virtual hierarchies need a root destructor slot. It does not state when a base destructor runs, which classes may declare a destructor, that a destructor can be sealed, or how callers in other modules delete an object. These rules are intended: only the ultimate base class introduces a destructor; if it is not virtual, no derived class may declare one; if it is virtual, derived classes override it and the base destructor is called automatically (non-virtually) after the derived body, also when the body exits early; a destructor may be `sealed`; destructor implementations are not visible across a module or artifact boundary, and a caller outside the module deletes through the generated `destroy` helper, whether it dispatches virtually or not being an implementation detail; in-module `delete` calls the destructor entry point inline and then frees.

Steps to Reproduce:

1. Read the Destructors section of the construction and destruction semantic document, and the Virtual Class Dispatch section of the interface and dispatch semantic document.
2. Look for the chaining order, the declaring-class rules, `sealed` destructors and the cross-module deletion call.

Expected:
The semantic documents describe the rules above, matching the compiler, and the language guide's class lifetime chapter mentions that a base destructor runs after a derived one.

Actual:
The rules are absent. The only related statement is that destructor lowering must preserve base destruction order, which does not say whether base destructors run.

Known Impact:
A reader of the documents cannot tell what a derived destructor does with its base, so independent implementations cannot be checked against the intended behavior.
