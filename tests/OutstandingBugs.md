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

Next bug number: BUG-102.

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

## BUG-101: Indexing an imported struct method array result overflows the compiler stack

Date/Time: 2026-09-02 16:01 EDT

Summary:
When a referenced static Camp project exports a struct containing an expanded
array field and an instance method returns an array derived from that field,
directly indexing the imported method result causes the compiler to recurse
until its native stack overflows. An imported method that returns a standalone
default array does not reproduce the broader failure.

Steps to Reproduce:

1. Create a library project that publicly exports a struct with a [const byte[]]
   field and a public instance method returning that field, or a slice derived
   from that field.
2. Build the library as a static artifact.
3. Reference its [.campbuild] from a second project using
   [--project-reference path/to/library.campbuild:static].
4. In the consuming project, obtain an instance of the exported struct and
   compile an expression such as [value.payload()[0]].

Expected:
The expression compiles and indexes the array returned by the imported instance
method.

Actual:
The compiler enters unbounded recursion between
[TryCreateIndexedParamsComponentExpressions],
[TryCreateParamsComponentExpressions], and [LowerExpression], then terminates
with a native stack overflow.

Known Impact:
Consumers cannot directly index array views returned by imported struct methods
when those views are derived from expanded array fields. Assigning the returned
array to a local before indexing may avoid this compiler path, but changing valid
consumer code would conceal a compiler crash and is not an acceptable library
workaround.
