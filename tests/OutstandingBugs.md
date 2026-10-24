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

Next bug number: BUG-363.

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

## BUG-362: Lifetime annotations are accepted on loop, out and catch variable declarations

Date/Time: 2026-10-24 14:32 EDT

Summary:
A lifetime annotation (`scoped`, `unscoped(...)` or `escaped`) is rejected on the type of an ordinary local
variable declaration, because the lifetime of a local is inferred from the values stored in it and a
lifetime cast on the value is the explicit proof. The same annotation is accepted without any diagnostic
on the other places that declare a local variable: a `foreach` variable, a variable declared in an `out`
argument at a call, and a variable declared by `catch`, both in the `catch (T name)` statement and in the
`catch T name` argument form at a call. A lifetime annotation is never valid on any local variable
declaration, wherever it is declared. A `for` initializer is already rejected.

Steps to Reproduce:

1. Compile the following program:

   ```camp
   struct Name { const char[] text; }
   struct Fault { int code; }
   void produce(out Name result, thrown Fault fault) { result = default; }
   void test(Name[] items)
   {
   	escaped Name plain = items[0];
   	foreach (escaped Name item in items) { }
   	produce(out escaped Name made, catch escaped Fault fault);
   	try { produce(out Name inner, catch Fault ignored); } catch (escaped Fault caught) { }
   }
   export int main() { return 0; }
   ```

2. Repeat with `scoped` and with `unscoped(items)` in place of `escaped`.

Expected:
Every annotated declaration is rejected with the diagnostic that lifetime annotations are not valid on local
variable types: the plain local, the `foreach` variable, the `out` declaration, the `catch` argument
declaration and the `catch` statement variable.

Actual:
Only the plain local is rejected:

```text
error: Lifetime annotations are not valid on local variable types; use an explicit lifetime cast instead.
```

The `foreach` variable, the `out` declaration and both `catch` declarations compile without a diagnostic.
When the annotated type is `auto`, an unrelated "Unknown type 'auto'." error is reported on these forms
(and an extra conversion error on the `out` form) instead of the lifetime diagnostic.

Known Impact:
The accepted annotations have no effect on the inferred lifetime, so they only mislead readers into thinking
a contract is being checked. Programs that write them today will start to fail when this is fixed.
