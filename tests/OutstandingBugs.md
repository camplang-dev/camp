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

Next bug number: BUG-297.

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

## BUG-296: String variable passed to a `const char[]` parameter that has a trailing default misaligns the call

Date/Time: 2026-10-15 22:05 EDT

Summary:
A `string` variable converts to a `const char[]` parameter by supplying the
pointer and the string length as two components. When the callee also declares a
trailing parameter with a default value, the call lowers with the length
component missing: the next explicit argument is placed in the length slot and
the default is appended instead. The call compiles without a diagnostic and
silently computes with the wrong length and the wrong later argument. A string
literal, a `const char[]` local, and a callee without a default are unaffected.

Steps to Reproduce:

1. Compile and run:
   ```camp
   extern void __intrinsic_log_i32(int value);
   int measure(const char[] text, int bonus = 1) => (int)text.length + bonus;
   export int main()
   {
       string label = "abc";
       __intrinsic_log_i32(measure(label, 5));
       return 0;
   }
   ```
2. Inspect the generated C call, or call a method whose later parameter is an
   `enum` with a default; the native build can instead fail with "too many
   arguments to function call".

Expected:
The program prints 8: the length of `label` (3) plus the explicit `bonus` (5).

Actual:
It prints 6. The generated call is `measure(label, 5, 1)`, so `text.length` is 5
and `bonus` takes its default. With a method taking `(int owner, const char[] name, Kind kind, Prim rep = Prim.N)` the call is emitted with one argument too many and the C
compiler rejects it.

Known Impact:
Any call that passes a `string` value to a `const char[]` parameter followed by
an explicitly supplied argument and a defaulted parameter. Workaround: remove the
default, pass an explicit `const char[]` view (for example a local declared as
`const char[]`), or supply every argument after the string by name.
