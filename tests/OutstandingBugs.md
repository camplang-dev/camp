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

Next bug number: BUG-222.

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

## BUG-221: Inline constant macros corrupt shadowing parameter declarations

Date/Time: 2026-10-04 00:30 EDT

Summary:
A top-level inline constant is emitted as a C macro using its source name. A
function parameter with the same source name is then rewritten by the C
preprocessor, producing invalid C even though the parameter must take precedence
over the outer declaration under ordinary lexical lookup.

Steps to Reproduce:

1. With compiler revision `ccb56c3cb2f151f2b051b69f897e301887839b4b`, save this
   source as `inline_shadow.camp`:

   ```camp
   inline int value = 11;

   int identity(int value)
   {
       return value;
   }

   export int main()
   {
       return identity(17) - 17;
   }
   ```

2. Run `campc run inline_shadow.camp --nostdlib --show-errorlevel --out-dir out`.
3. Inspect the generated C if the native build fails.

Expected:
Compilation succeeds and the program reports `ERRORLEVEL 0`. The parameter
`value` shadows the outer constant. The Analysis Scopes rules in
`docs/semantics/01-binding-analysis-and-lowering-pipeline.md` require lookup to
respect more local declarations. The Inline Constants rules in
`docs/semantics/11-metadata-api-surface-and-symbols.md` describe macro-style C
emission as an ABI artifact, not a change to source lookup.

Actual:
Native compilation fails with `expected ')'` and conflicting function-type
errors. Generated C places `#define value ((int)11)` before
`static int identity(int value)`, so macro expansion corrupts the parameter
declaration. No executable runs.

Known Impact:
Valid source that shadows a top-level inline constant with a parameter cannot
compile through the native C backend. The same macro also corrupts same-named
local declarations in a larger reproduced program. Avoiding shadowed constant
names avoids this collision, but no workaround preserving that lexical lookup
case has been verified.
