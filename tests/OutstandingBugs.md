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

Next bug number: BUG-310.

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

## BUG-309: A field is replaced by property accessors that share its name

Date/Time: 2026-10-18 19:26 EDT

Summary:
When a type declares a field and also a getter or setter whose property name is the field's name, property syntax is rewritten to the accessors instead of binding to the field. The language semantics say property rewriting is a fallback: if a visible field or member of that name exists, the access binds to it and the accessors are not consulted.

Steps to Reproduce:

1. Declare `struct Holder { int Value; int getValue() { return 7; } }`.
2. In a function, declare `Holder holder = default;`, write `holder.Value = 5;` and `int read = holder.Value;`.
3. Compile. Then replace the getter with `void setValue(int next) { }`, drop the read, write `holder.Value = 5;` and print `holder.Value`.

Expected:
Both programs compile, and every `holder.Value` is the field: the first stores 5 and reads 5, the second prints 5 and never calls `setValue`.

Actual:
With the getter, the assignment reports `Property 'Value' is not writable on type 'Holder'` and the read reports `Multiple member candidates found for 'Value'`. With the setter, the program compiles but `holder.Value = 5;` calls `setValue` and leaves the field at zero, so the program prints 0.

Known Impact:
A type cannot expose a public field and keep a same-named accessor for another purpose, and a setter silently takes over writes that should reach the field. Renaming either member works around it.
