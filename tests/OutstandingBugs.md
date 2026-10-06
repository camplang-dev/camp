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

Next bug number: BUG-249.

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

## BUG-248: Iterator receiver context hides following array arguments

Date/Time: 2026-10-05 22:16 EDT

Summary:
A method call on an iterator-shaped newtype can miscount its receiver context
as a source argument. A subsequent primitive string passed to an array-view
parameter then loses its hidden length component, producing an invalid native
call.

Steps to Reproduce:

1. Compile this source with `campc run repro.camp --nostdlib`:

   ```camp
   newtype iter nuint Sink(char[] buffer);
   nuint size(Sink this, const char[] value) { return value.length; }
   export int main() {
       Sink sink = default;
       return sink.size("abc") == 3 ? 0 : 1;
   }
   ```

2. Inspect the lowering or native compiler diagnostic.

Expected:
The call passes the receiver carrier, its bound context, the string carrier,
and the string's length.

Actual:
The string length is omitted. Native compilation reports too few arguments.

Known Impact:
Iterator-shaped writer methods accepting character-array views cannot compile
with primitive string arguments. Ordinary arrays can avoid the string expansion
path in affected calls.
