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

Next bug number: BUG-408.

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

## BUG-406: Missing-within-context call error is suppressed when allocation errors exist in the same program

Date/Time: 2026-10-27 20:47 EDT

Summary:
Under the explicit within policy, three constructs require an explicit within
context: a `new` expression, a pointer-form `delete`, and a call to a function
that has a `within` parameter. Each is an independent error. The compiler
reports the `new` and `delete` errors in one pass and the missing-context call
error in a later pass, and the later pass does not run when the earlier pass
reported errors. A program that contains both kinds of mistake therefore shows
only the `new` and `delete` errors. The call error appears only after those are
fixed and the program is compiled again.

Steps to Reproduce:

1. Compile the following program with the explicit within policy (the
   `#within explicit` prelude directive selects it for the file):

   ```camp
   #within explicit
   extern escaped void* malloc(nuint size);
   extern void free(void* ptr);
   interface Allocator
   {
   	escaped void* alloc(nuint size);
   	void free(void* ptr);
   }
   class C
   {
   	int v;
   	C() { this.v = 1; }
   }
   int work(within allocator)
   {
   	return 5;
   }
   export int main()
   {
   	C* c = new C();
   	delete c;
   	return work();
   }
   ```

2. Remove the `new` and `delete` lines (keeping `return work();`) and compile
   again.

Expected:
All three errors are reported in a single compilation: the `new` at (20,9), the
`delete` at (21,9) and the call at (22,13), each with its own message. This is
what the compiler does for each error when it is the only kind in the program.

Actual:
Step 1 reports only:

```text
(20,9): error: new requires an explicit within context; use within(allocator) or within(default).
(21,9): error: pointer-form delete requires an explicit within context; use within(allocator) delete or within(default) delete.
```

The call error is not reported. Step 2 reports:

```text
(20,13): error: Call requires a within context for parameter 'allocator'; prefix the call with a `within (allocator)` expression or place it inside a `within (allocator)` statement.
```

Known Impact:
A user fixing a program under the explicit policy needs an extra compile cycle
to discover the call errors, and an error-expectation test that mixes both kinds
of mistake cannot assert all of them at once. The workaround is to keep each kind
in a separate program.

## BUG-407: Copyable struct with a direct class or fixed-struct field is accepted

Date/Time: 2026-10-27 22:13 EDT

Summary:
An ordinary copyable struct cannot contain a direct field whose type is a class
or a fixed struct, because copying the struct would copy a value that has no
copy semantics. The compiler accepts such a declaration without a diagnostic.
For a class field the program then fails later, when the generated C is
compiled; for a fixed-struct field the program compiles and runs.

Steps to Reproduce:

1. Compile and run the following program:

   ```camp
   extern void __intrinsic_log_i32(int value);
   extern escaped void* malloc(nuint size);
   extern void free(void* ptr);
   class K
   {
   	int v;
   }
   struct Copyable
   {
   	K k;
   	int x;
   }
   export int main()
   {
   	__intrinsic_log_i32(1);
   	return 0;
   }
   ```

2. Replace the class `K` and the field `K k;` with a `fixed struct F { int a; }`
   and a field `F f;`, and compile and run again.

Expected:
Both programs are rejected with a diagnostic at the offending field of
`Copyable` that says an ordinary struct cannot contain a direct class or fixed
struct field.

Actual:
No diagnostic is reported. Step 1 stops in the native compile with
`field has incomplete type 'K'`. Step 2 compiles and runs.

Known Impact:
The restriction on copyable structs is not enforced, so an invalid declaration
is reported as a native compile failure or not at all. The workaround is to
declare the field as a pointer to the class or fixed struct, or to declare the
containing struct `fixed`.
