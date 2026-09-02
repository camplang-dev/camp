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

Next bug number: BUG-107.

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

## BUG-103: Inactive imported array indexing overflows the compiler stack

Date/Time: 2026-09-02 16:24 EDT

Summary:
Directly indexing an array returned by an imported struct instance method can
overflow the compiler stack when the containing source file is disabled by a
file-level requirement. The same expression compiles and runs when that
requirement is enabled.

Steps to Reproduce:

1. Create a static library project that exports a struct containing an array
   field and an instance method returning an array derived from that field.
2. Create a consuming project with a source file beginning with
   [requires(TEST_MODULE);].
3. In that file, index the imported method result with an expression such as
   [value.payload()[index]].
4. Confirm that testing the consuming project with [TEST_MODULE] active passes.
5. Build the consuming project as a static artifact with [TEST_MODULE]
   inactive while leaving the guarded test source in its [.campbuild].

Expected:
Both configurations compile successfully. The inactive file contributes no
executable test declarations to the static artifact, and lowering any retained
conditional representation terminates normally.

Actual:
The active test configuration succeeds. The inactive static build enters
unbounded recursion through expanded-component indexing and generic property
lookup during expression lowering, then terminates with a native stack overflow.

Known Impact:
A reusable [.campbuild] cannot safely include guarded tests that exercise this
valid imported API pattern. Consequently, downstream static project references
cannot build the dependency even though its own test configuration passes.

## BUG-104: Imported struct array literals use the projected ABI type

Date/Time: 2026-09-02 16:25 EDT

Summary:
An explicitly typed array literal whose element is a public struct imported
from a static Camp project is lowered as an array of the struct's generated C
ABI projection. Passing that array to an imported function expecting the
natural Camp struct array is then rejected as a conversion between two
different element types.

Steps to Reproduce:

1. Create a library project that exports a public struct and a public function
   accepting [const Item[]].
2. Build the library as a static artifact and reference its [.campbuild] from a
   consuming project.
3. In the consumer, initialize [Item[] items = [ { ... } ];].
4. Pass [items] to the imported function.

Expected:
The literal has type [Item[]], and the argument converts to [const Item[]] using
the natural imported Camp type. ABI projection is confined to generated native
boundary code.

Actual:
The literal is materialized as an array whose element type is the generated
namespace-prefixed ABI struct. The call is rejected with a diagnostic equivalent
to [Argument cannot convert 'NamespaceItem[]' to 'const Item[]']. The generated
Camp API itself declares the natural [Item] type correctly.

Known Impact:
Consumers of static Camp project references cannot pass locally constructed
arrays of imported structs through otherwise natural Camp APIs. Reconstructing
or naming the generated ABI projection in Camp source would expose an
implementation detail and is not an acceptable library workaround.

## BUG-105: Imported interface parameters omit class-to-interface conversion

Date/Time: 2026-09-02 16:30 EDT

Summary:
When a function imported from a static Camp project accepts an interface-instance
pointer, passing a pointer to a local class that implements the interface does
not emit the required class-to-interface conversion. Generated C passes the
class pointer directly to the imported interface ABI parameter, causing invalid
dispatch at runtime.

Steps to Reproduce:

1. Create a library project exporting an interface and a public function that
   accepts a pointer to that interface.
2. Build the library as a static artifact and reference its [.campbuild] from a
   consuming project.
3. In the consumer, declare a class that implements the imported interface and
   provides all required implementation methods.
4. Allocate an instance of the class and pass its pointer directly to the
   imported function.
5. Invoke an interface method through the received parameter.

Expected:
The class pointer is converted to the class's interface-instance pointer before
the imported call, matching the behavior of calls to functions compiled in the
same module. Interface dispatch reaches the implementation method.

Actual:
Generated C passes the class pointer directly where the imported function
expects the interface-instance pointer representation. The build can complete
without a diagnostic, but the first interface dispatch dereferences invalid or
null vtable state and terminates with an access violation.

Known Impact:
Camp modules cannot safely pass local interface implementations to APIs imported
through static project references. This is a silent ABI mismatch and blocks
interface-based extension points across module boundaries.

## BUG-106: C API omits opaque declarations for private pointer field types

Date/Time: 2026-09-02 16:36 EDT

Summary:
When a public struct contains a pointer field whose pointee is a non-public
class, the generated C API header uses the lowered pointee type without emitting
an opaque forward declaration for it. A downstream generated C translation unit
that includes the API header consequently fails to compile with an unknown type.

Steps to Reproduce:

1. Create a library project with a non-public class named [Storage].
2. Export a public struct containing a [Storage* storage] field while keeping the
   field and storage implementation non-public at the Camp source level.
3. Build the library as a static artifact with generated Camp and C API metadata.
4. Reference the library [.campbuild] from another Camp project and compile that
   project's generated C.

Expected:
The public struct layout retains the pointer field required by the ABI, and the
generated C API header emits an opaque declaration such as a typedef for
[Storage] without exposing its fields. The generated Camp API may likewise keep
the pointee opaque and non-public.

Actual:
The C API header emits the public struct field using the lowered [Storage*] type
but emits no declaration for [Storage]. Native compilation fails with an
[unknown type name] diagnostic in the generated API header.

Known Impact:
Static Camp project consumers cannot use public value types that retain typed
pointers to private implementation storage. Exposing the storage type publicly
or erasing it to [void*] would unnecessarily weaken source-level encapsulation
and type safety.
