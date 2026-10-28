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

Next bug number: BUG-410.

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

## BUG-409: Fixed struct values can be copied by initialization, assignment and return

Date/Time: 2026-10-27 23:14 EDT

Summary:
A fixed struct has no implicit copy semantics: its storage is inline and visible,
and a value of the type must be constructed, passed and retained by reference.
The compiler rejects a fixed struct passed by value as a parameter ("Fixed
structs and classes must be passed by reference"), but it accepts the other ways
of copying one: initializing a variable from another variable of the type,
assigning one variable to another, and returning a value of the type from a
function. Each of these copies the contents silently.

Steps to Reproduce:

1. Compile and run the following program:

   ```camp
   extern void __intrinsic_log_i32(int value);
   fixed struct Buffer
   {
   	int a;
   }
   export int main()
   {
   	Buffer x = { .a = 5 };
   	Buffer y = x;
   	__intrinsic_log_i32(y.a);
   	return 0;
   }
   ```

2. Replace the body of `main` with the following and compile and run again:

   ```camp
   	Buffer x = { .a = 5 };
   	Buffer y = { .a = 1 };
   	y = x;
   	__intrinsic_log_i32(y.a);
   	return 0;
   ```

3. Compile and run the following program:

   ```camp
   extern void __intrinsic_log_i32(int value);
   fixed struct Buffer
   {
   	int a;
   }
   Buffer make()
   {
   	Buffer b = { .a = 5 };
   	return b;
   }
   export int main()
   {
   	__intrinsic_log_i32(make().a);
   	return 0;
   }
   ```

Expected:
Each program is rejected with a diagnostic at the copy, saying that a fixed
struct cannot be copied and must be referenced through a pointer, in the same
way a by-value parameter is rejected.

Actual:
No diagnostic is reported in any of the three programs. Each compiles and runs
and prints `5`.

Known Impact:
The no-copy rule for fixed structs is only partly enforced, so a program can
duplicate a value that is meant to live in exactly one place. The workaround is
to use a pointer to the fixed struct, or an out parameter, in place of a copy.
