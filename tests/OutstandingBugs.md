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

Next bug number: BUG-122.

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
