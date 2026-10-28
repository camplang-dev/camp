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

Next bug number: BUG-409.

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

## BUG-407: Copyable struct with a direct class or fixed-struct field, and fixed struct with a direct class field, are accepted

Date/Time: 2026-10-27 22:13 EDT (amended 2026-10-27 22:50 EDT, before any work started)

Summary:
The containment rules for struct fields are:

- a class may contain a copyable struct or a fixed struct;
- a fixed struct may contain a fixed struct, but not a direct class;
- a copyable struct may contain neither a fixed struct nor a direct class.

The compiler does not enforce the last two rules. A copyable struct with a
direct class or fixed-struct field, and a fixed struct with a direct class
field, are accepted without a diagnostic. A class field then fails later, when
the generated C is compiled; a fixed-struct field in a copyable struct compiles
and runs.

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

3. Change the containing struct in the program from step 1 from `struct
   Copyable` to `fixed struct Holder`, keeping the class `K` and the field
   `K k;`, and compile and run again.

Expected:
All three programs are rejected with a diagnostic at the offending field. The
message says an ordinary struct cannot contain a direct class or fixed struct
field (steps 1 and 2) or that a fixed struct cannot contain a direct class field
(step 3).

Actual:
No diagnostic is reported. Steps 1 and 3 stop in the native compile with
`field has incomplete type 'K'`. Step 2 compiles and runs.

Known Impact:
The containment rules are not enforced, so an invalid declaration is reported as
a native compile failure or not at all. The workaround is to declare the field
as a pointer to the class or fixed struct, or, for a copyable struct holding a
fixed struct, to declare the containing struct `fixed`.

## BUG-408: Untyped within parameter with a default value also reports an unknown type

Date/Time: 2026-10-27 22:34 EDT

Summary:
A within parameter cannot have a default value, and the compiler rejects one
with a clear diagnostic. When the within parameter is written in its usual
untyped form, the compiler reports a second, unrelated diagnostic that treats
the parameter name as an unknown type. The same mistake written with an
explicit type reports only the real error, so the extra error comes from how
the untyped form is read once a default follows it.

Steps to Reproduce:

1. Compile the following program:

   ```camp
   extern escaped void* malloc(nuint size);
   extern void free(void* ptr);
   interface Allocator
   {
   	escaped void* alloc(nuint size);
   	void free(void* ptr);
   }
   int work(within allocator = default)
   {
   	return 1;
   }
   export int main()
   {
   	return 0;
   }
   ```

2. Change the parameter to `within Allocator allocator = default` and compile
   again.

Expected:
Both programs report only the within-default diagnostic, at the default value.

Actual:
Step 1 reports two errors:

```text
(8,17): error: Unknown type 'allocator'.
(8,29): error: within parameters cannot have default values; their value is supplied by an explicit within context or explicit within argument.
```

Step 2 reports only the second error, at (8,39). The same extra error appears
for a constructor parameter written the same way.

Known Impact:
The first error is misleading, because the parameter name is not meant to be a
type. The program is rejected either way, so no invalid program is accepted.
Writing the parameter with an explicit type avoids the extra error.
