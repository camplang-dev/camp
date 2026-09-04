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

Next bug number: BUG-126.

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

## BUG-116: Conditional expression can leave a slice result uninitialized

Date/Time: 2026-09-03 America/Toronto

Summary:
A conditional expression whose branches produce borrowed array slices can lower
to generated C that declares the result slice without assigning either branch.
The resulting program may read an invalid pointer and crash despite compiling
without a diagnostic.

Steps to Reproduce:

1. Declare a local borrowed array-slice variable initialized by a conditional
   expression whose two branches are distinct slices of a source array.
2. Pass that local to another method.
3. Compile and run the program.

Expected:
The generated program assigns the selected branch slice, preserving its pointer
and length, and the called method receives that selected slice.

Actual:
The generated C declares the slice local but leaves its pointer and length
uninitialized before passing it to the called method. The executable can crash.

Known Impact:
Code using a conditional expression to select a slice is unsafe until fixed.
An equivalent branch that calls the consumer separately for each selected slice
avoids the faulty lowering.

## BUG-121: Re-exported dependency types are misnamed in module source

Date/Time: 2026-09-03 America/Toronto

Summary:
Using a dependency type through its local name after an `export Type as Alias`
declaration can emit a doubly prefixed C type name. The source declaration is
accepted, but the generated C cannot compile because the emitted type name was
never declared.

Steps to Reproduce:

1. In a namespace, write `export Std::Allocator as LocalAllocator;`.
2. Declare a local `LocalAllocator*` variable or parameter.
3. Build the module with the native C emitter.

Expected:
The generated C uses the declared ABI spelling for the re-exported allocator
type and compiles successfully.

Actual:
The generated C uses an undeclared double-prefixed type such as
`NamespaceLocalAllocator` when the available declaration is the standard
allocator ABI type.

Known Impact:
Implementations cannot use a re-export alias as a source type until this is
fixed. Use the original dependency type name internally and reserve the
re-export for an exported signature that requires the dependency ABI type.

## BUG-122: Slice-returning accessor can omit the generated result length

Date/Time: 2026-09-04 00:50 America/Toronto

Summary:
A method that returns a borrowed array slice from an owned null-terminated
string can lower to C without assigning the generated result-length out
parameter. The call is accepted, but a consumer that iterates the slice can
receive an arbitrary length and access invalid memory.

Steps to Reproduce:

1. Store an owned `string` in an escaped class.
2. Declare an accessor returning `const char[]` whose body returns that string.
3. Pass the accessor result to a method that iterates the returned slice, then
   compile and run the program.

Expected:
The accessor returns both the string pointer and the correct slice length, so
the consumer iterates only the characters in the owned string.

Actual:
The generated C returns the pointer but leaves the result-length out parameter
unassigned. The consumer can receive a large arbitrary length and crash while
reading past the string.

Known Impact:
Borrowed slice accessors over owned strings are unsafe until fixed. Return
`string` from the accessor when a null-terminated path or similar value is
appropriate; callers can then pass it directly to slice-taking operations.

## BUG-123: Guarded expression can evaluate an underflowing array index early

Date/Time: 2026-09-04 01:15 America/Toronto

Summary:
A boolean expression that first guards an unsigned index and then reads a
preceding array element can lower to C that evaluates the indexed element into
a temporary before evaluating the guard. A zero index can therefore underflow
and crash even though the source condition would be false.

Steps to Reproduce:

1. Declare an unsigned loop index and an array.
2. Test `index != 0 && array[index - 1] == array[index]` in one condition.
3. Execute the condition with `index` equal to zero.

Expected:
The left operand prevents evaluation of the indexed right operand when the
index is zero, so the condition evaluates false without accessing the array.

Actual:
The generated C creates temporaries for both array expressions before the
short-circuit condition. It reads `array[index - 1]` with an underflowed index
and can fault.

Known Impact:
Guarded index expressions are unsafe when the guarded expression can underflow
or otherwise be invalid. Place the guard in a separate branch before evaluating
the indexed expression.

## BUG-124: Local namespace type can be shadowed by an imported same-named type

Date/Time: 2026-09-04 02:52 America/Toronto

Summary:
A type declared in the current namespace can be resolved as an imported type
with the same simple name. This occurs even when the local declaration is
explicitly qualified with its namespace, so member accesses bind against the
wrong type or fail.

Steps to Reproduce:

1. Create a static project dependency that exports a type named `Catalog` in
   one namespace.
2. In a consuming project, declare a distinct `Catalog` type in a different
   namespace with a field or method not present on the dependency type.
3. Access that local member from the locally declared type, including through
   an explicit namespace qualification.

Expected:
The current namespace declaration has its own identity and wins ordinary
lookup; explicit qualification resolves that declaration directly.

Actual:
The compiler resolves the use as the imported same-named type and reports that
the local field or method does not exist.

Known Impact:
Projects cannot expose a type with a simple name already exported by a static
dependency. Until fixed, use a distinct public simple name at the product
boundary and document the name as a bootstrap-compiler workaround.

## BUG-125: Static dependency API omits the allocator type for escaped owners

Date/Time: 2026-09-04 03:19 America/Toronto

Summary:
An exported escaped class in a static project dependency can emit `within
Allocator` in its generated API header without making that allocator type
available to the consuming module. A consumer cannot compile even though both
projects build independently.

Steps to Reproduce:

1. Create a static project dependency that exports an escaped class.
2. Build that dependency so its generated API header contains the class
   constructor and ownership methods.
3. Add a second project that references the dependency statically and build it.

Expected:
The generated API includes or imports the allocator type required by the
escaped owner ABI, and the consuming project compiles.

Actual:
The consuming build reports that the exported escaped declaration exposes a
non-exported type named `Allocator`.

Known Impact:
Public escaped owners cannot safely cross a static project boundary. This
blocks ordinary modular API design; replacing ownership with opaque numeric
handles is only a temporary workaround and should not dictate product APIs.
