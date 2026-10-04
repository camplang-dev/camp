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

Next bug number: BUG-225.

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

## BUG-224: Same-named static classes in different namespaces cannot be used

Date/Time: 2026-10-04 12:20 America/Toronto

Summary:
Two static classes that share a simple name but live in different namespaces are
valid distinct declarations, and a namespace-qualified reference should select
one of them. Instead, any member access through either class fails to bind. Same
named struct types in different namespaces already work.

Steps to Reproduce:

1. Save this program as `static_names.camp`:

   ```camp
   namespace Left
   {
   	static class Counter
   	{
   		static int next(int step) { return step + 1; }
   	}
   }

   namespace Right
   {
   	static class Counter
   	{
   		static int next(int step) { return step + 2; }
   	}
   }

   export int main()
   {
   	return Left::Counter.next(1) + Right::Counter.next(1) - 5;
   }
   ```

2. Run `campc run static_names.camp --show-errorlevel`.

Expected:
Compilation succeeds and the program reports `ERRORLEVEL 0`. The Names, Imports,
Visibility And Symbols rules make a namespace qualifier part of a declaration's
identity, so `Left::Counter` and `Right::Counter` are different containers.

Actual:
Compilation fails with `Member 'next' could not be found on type 'Counter'.` at
each qualified call. Using only one of the two classes compiles correctly. Static
class lookup by member owner is keyed by simple name, and a name declared in more
than one namespace is dropped from that table.

Known Impact:
Any program or library that reuses a static class name across namespaces cannot
call or read members through either class. Renaming one class avoids the problem.
