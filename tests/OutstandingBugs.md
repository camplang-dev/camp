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

Next bug number: BUG-110.

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

## BUG-107: Inactive imported indexing overflows when modules share a namespace

Date/Time: 2026-09-02 17:46 EDT

Summary:
The inactive imported-array indexing failure addressed by BUG-103 still
overflows the compiler stack when the referenced library and consuming module
declare the same namespace. The repaired case succeeds when the consumer uses a
different namespace and imports the library namespace with [using].

Steps to Reproduce:

1. Create a static library project whose source declares a namespace and exports
   a struct containing an array plus an instance method returning that array.
2. Create a consuming project whose source declares the same namespace as the
   library and references the library [.campbuild].
3. Include a source file beginning with [requires(TEST_MODULE);] and directly
   index the imported method result with [value.payload()[index]].
4. Build the consuming project as a static artifact with [TEST_MODULE]
   inactive.
5. Compare with an otherwise equivalent consumer in a different namespace that
   uses the library namespace explicitly.

Expected:
Both namespace arrangements compile successfully. Sharing a namespace across
separate modules does not merge their module visibility or change expression
lowering.

Actual:
The different-namespace case succeeds. The shared-namespace case enters
unbounded recursion through reference-namespace lookup, generic property lookup,
and expanded-component indexing during lowering, then terminates with a native
stack overflow.

Known Impact:
Project families that intentionally use one flat namespace across multiple Camp
modules cannot include guarded tests exercising imported array-view APIs in
their reusable build files. Static downstream project references consequently
cannot build those modules normally.

## BUG-108: Re-exported imported struct arrays retain the unprojected element type

Date/Time: 2026-09-02 18:05 EDT

Summary:
A public function in one static library cannot expose an array parameter whose
element struct is imported from another static library. The generated API for
the downstream library retains the source-level element identity while the
consumer's array literal uses the imported ABI projection, and the two types do
not convert.

Steps to Reproduce:

1. Create a static library that exports a struct.
2. Create a second static library that references the first and exports a
   function accepting [const ExportedStruct[]].
3. Create a consumer that references both libraries, constructs an
   [ExportedStruct[]], and passes it to the second library's function.
4. Build the consumer.

Expected:
The second library's public API preserves the imported struct's projected
identity, and the consumer can pass an array of that struct.

Actual:
The consumer reports that its ABI-projected struct array cannot convert to an
array of the source-level struct type. Rebuilding both static libraries with the
current compiler does not change the result.

Known Impact:
Static libraries cannot reliably re-export APIs containing arrays of structs
owned by their dependencies. This prevents ordinary layered module APIs from
representing collections of shared record types.

## BUG-109: Public member may expose an internal receiver type

Date/Time: 2026-09-02 18:19 EDT

Summary:
The compiler accepts a public instance method declared on an internal struct.
This violates public-signature visibility because the method's implicit [this]
parameter has the less-visible struct type. Analysis should reject this in the
same way as any other public parameter whose type is less than public.

Steps to Reproduce:

1. Declare an internal struct in a static library.
2. Give the struct a public instance method.
3. Build the library.

Expected:
Analysis reports that the public method exposes a type with less than public
visibility through its implicit [this] parameter. API generation is not
attempted.

Actual:
Analysis accepts the declaration. Native API generation then emits the public
method prototype while omitting the internal receiver struct declaration, and
the native compiler eventually reports an unknown type name.

Known Impact:
An invalid visibility combination is not diagnosed at its declaration and can
silently produce a broken static-library API header, preventing downstream
modules from compiling.
