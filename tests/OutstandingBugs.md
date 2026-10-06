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

Next bug number: BUG-248.

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

## BUG-247: Callable expanded-return initializers are materialized twice

Date/Time: 2026-10-05 22:06 EDT

Summary:
Initializing an expanded local from a call through a callable value can lower
the initializer twice. The shared call node receives duplicate hidden result
arguments, and the first invocation references local components before their
declarations.

Steps to Reproduce:

1. Compile this source using `campc run repro.camp --nostdlib`:

   ```camp
   newtype delegate int Handler(int value);
   Handler make() { return value => value + 1; }
   int use(fn Handler() choose) {
       Handler handler = choose();
       return handler(3);
   }
   export int main() { return use(make) == 4 ? 0 : 1; }
   ```

2. Inspect the lowering or generated native compiler diagnostics.

Expected:
The initializer calls `choose` once, supplies one hidden context result, and
initializes both components of `handler` before invoking it.

Actual:
Lowering emits two calls with two hidden context results. Native compilation
fails with an undeclared `handler_context` and too many call arguments.

Known Impact:
Expanded locals initialized through function pointers or other callable values
may fail to compile. Direct named-function calls avoid this initializer path.
