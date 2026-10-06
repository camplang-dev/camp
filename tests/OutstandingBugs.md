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

Next bug number: BUG-250.

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

## BUG-249: Nested callable return signatures are parsed as outer parameters

Date/Time: 2026-10-05 22:24 EDT

Summary:
Resolved signatures for callable values returning another callable are split at
the first opening parenthesis. That parenthesis belongs to the returned
callable, so binding and C emission misinterpret the outer result and parameter
types. Returning a delegate through another delegate can produce invalid C.

Steps to Reproduce:

1. Compile this source with `campc run repro.camp --nostdlib`:

   ```camp
   newtype delegate int Handler(int value);
   int add(int value) { return value + 5; }
   Handler make() { return add; }
   int use(delegate Handler() choose) {
       Handler handler = choose();
       return handler(3);
   }
   export int main() { return use(make) == 8 ? 0 : 1; }
   ```

2. Inspect the native compiler diagnostics.

Expected:
The outer callable's context and expanded result remain separate from the
returned callable's signature. The program builds and exits successfully.

Actual:
C emission produces malformed types such as `fn_int` and `int__void`, and
native compilation fails before execution.

Known Impact:
Nested callable return types can corrupt ABI declarations and conversions.
Passing a function pointer returning a named callable avoids the affected
delegate carrier in this example.
