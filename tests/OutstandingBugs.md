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

Next bug number: BUG-252.

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

## BUG-251: Iterator locals reject scoped pointer-bearing initializers

Date/Time: 2026-10-06 23:33 EDT

Summary:
Inside a `struct iter` generator, declaring a local of a pointer-bearing type
(a view, array or pointer) and initializing it from a scoped value reports that
the assignment cannot store a scoped value in storage tied to `this`. The scoped
value is the iterator's own parameter, or a value derived from it, so the local
has the same lifetime as the parameter that the generated iterator state already
retains. Earlier compilers accepted these declarations, and nothing in the
lifetime semantics forbids them. The rejection began with the change that
introduced named stack-storage handling for generated iterator and async state.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp --nostdlib --artifact none`:

```camp
struct View { const char[] text; }
const char[] span(View this) { return this.text; }

struct iter int a(View this) { const char[] value = this.span(); yield (int)value.length; }
struct iter int b(const char[] text) { const char[] value = text; yield (int)value.length; }
struct iter int c(const int* p) { const int* q = p; yield *q; }

export int main() { return 0; }
```

Expected:
The source compiles. A local copied from a parameter, or from a value derived
from a parameter, is no shorter-lived than the iterator state that holds both.

Actual:
Each local declaration is rejected with `error: Assignment cannot store a scoped
pointer-bearing value in storage tied to 'this'.` Compilers before the stack-storage
suspension change accept the same source.

Known Impact:
Any generator that keeps a view, array or pointer derived from a parameter in a
local no longer compiles. The only workaround is to avoid such locals and use the
parameter expression at every use.
