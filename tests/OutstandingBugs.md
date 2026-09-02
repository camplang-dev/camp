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

Next bug number: BUG-111.

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

## BUG-110: Inactive shared-namespace imported-result indexing still overflows

Date/Time: 2026-09-02 18:41 EDT

Summary:
The shared-namespace form of inactive indexing on an imported method result
still overflows the compiler stack after the BUG-107 correction. The failure
occurs in an ordinary static project-reference build after the referenced
library has been rebuilt with the same compiler.

Steps to Reproduce:

1. Create a static library whose source declares a namespace and exports a
   struct containing an array plus an instance method returning that array.
2. Create a consuming project in the same namespace and reference the library's
   [.campbuild] as a static project reference.
3. Include a test source beginning with [requires(TEST_MODULE);] that indexes
   the imported method result with [value.payload()[index]].
4. Build the referenced library and then build the consumer as a static
   artifact without [TEST_MODULE].

Expected:
The inactive declaration is handled without lowering recursion and the static
consumer builds normally.

Actual:
The compiler repeatedly enters reference-namespace lookup, generic property
lookup, and expanded-component indexing until it terminates with a native stack
overflow.

Known Impact:
Reusable build files containing guarded tests cannot be built normally through
static project references when the modules use the same flat namespace.
