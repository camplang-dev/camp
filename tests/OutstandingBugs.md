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

Next bug number: BUG-103.

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

## BUG-102: Generated Camp APIs do not preserve expanded public struct fields

Date/Time: 2026-09-02 16:04 EDT

Summary:
The Camp API generated for a static project flattens an expanded public struct
field, such as an array, into its ABI component fields. A Camp project that
references the static project consequently sees the pointer and length storage
instead of the public Camp field declared by the library.

Steps to Reproduce:

1. Create a library project that exports a public struct containing a public or
   module-consumable [byte[] bytes] field.
2. Build the library as a static artifact with generated Camp API metadata.
3. Reference the library [.campbuild] from a second Camp project.
4. In the consumer, initialize the exported struct, read [value.bytes] as a
   [byte[]], or use an array of the exported struct as a function argument.
5. Build or test the consuming project.

Expected:
The generated Camp API preserves the source-level expanded field shape for Camp
consumers while independently representing its pointer and length components at
the C ABI boundary. Source-level reads, initializers, and function arguments use
the original exported Camp type.

Actual:
The generated Camp API declares separate [byte* bytes] and
[nuint bytes_length] fields. Reading [value.bytes] therefore has type [byte*],
and uses of arrays containing the imported struct can expose projected ABI type
names rather than the source-level struct type. Valid consumer code fails with
conversion diagnostics such as [byte*] to [byte[]] or an array of the projected
struct type to an array of the source-level type.

Known Impact:
Static Camp project references cannot preserve natural public APIs for structs
with expanded fields. This blocks module isolation for libraries that return
owned buffers or expose array views through result structs. Manually rebuilding
arrays from ABI component fields in consumers would leak ABI lowering into Camp
source and is not an acceptable workaround.
