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

Next bug number: BUG-319.

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

## BUG-317: Interpolated string size accumulation wraps instead of failing before allocation

Date/Time: 2026-10-20 00:45 EDT

Summary:
The runtime size of an interpolated string is the sum of its literal segments and the lengths its formatters report. When that sum exceeds the `nuint` range the specification requires the interpolation to fail before allocation or writing, through the target's ordinary bounds-failure mechanism. The compiler adds the lengths without an overflow check, so the total wraps and the program continues with a small allocation.

Steps to Reproduce:

1. Declare a `prep char[]` `toString` that returns `(nuint)0 - 1` without writing.
2. Write `string t = $"ab{value}cd";` with a value of that type.
3. Compile with `--nostdlib` and run the program.

Expected:
The interpolation fails before it allocates or writes anything (semantics/14, "Runtime Evaluation And Lowering": "Runtime overflow must fail before allocation or buffer writing").

Actual:
The program runs to the end and both log calls around the interpolation print.

Known Impact:
A formatter that reports a huge length makes the interpolation allocate a wrapped size, then write formatter output past it. No workaround except keeping formatter lengths small.

## BUG-318: Returning an implicit scoped interpolated string is not diagnosed

Date/Time: 2026-10-20 00:45 EDT

Summary:
A runtime interpolation without `new` or `stackalloc` uses compiler-selected scoped storage, and the specification says the result cannot be returned or otherwise escape unless equivalent stack-backed storage could. The compiler rejects returning an explicit `stackalloc $"..."` local but accepts returning a local initialized from an implicit scoped interpolation.

Steps to Reproduce:

1. Declare `string escape(Count count) { string local = $"a{count}"; return local; }` with a `Count` that has a prepared `toString`.
2. Compile the file.

Expected:
A diagnostic that the scoped storage cannot be returned, as for `char[] local = stackalloc $"a{count}"; return local;`, which reports "Return expression cannot return stackalloc-backed storage."

Actual:
No diagnostic. The returned string points into the dead frame.

Known Impact:
Dangling string results from functions that interpolate. Workaround: use `within (allocator) new $"..."` for results that outlive the function.
