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

Next bug number: BUG-141.

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

## BUG-131: Value-returning method with `finally` can omit its C result local

Date/Time: 2026-09-04 America/Toronto

Summary:
A method that returns a value through multiple error paths while using a
`finally` cleanup statement can emit C references to the generated return local
without declaring that local. The Camp source is accepted, but native C
compilation fails.

Steps to Reproduce:

1. Declare a value-returning method that opens a resource, registers a `finally`
   cleanup, and has early error returns plus a successful value return.
2. Build the enclosing project with the native C emitter.

Expected:
The generated C declares and assigns the method's return local on every path,
then performs cleanup before returning it.

Actual:
The generated C references an undeclared local such as `_return30` and fails to
compile.

Known Impact:
Native code using this cleanup-and-return shape cannot compile. Close the
resource explicitly on each return path until the lowering is fixed.

## BUG-133: String-field array expansion adds an extra optional argument

Date/Time: 2026-09-05 America/Toronto

Summary:
A same-class call that passes a `string` field to a `const char[]` parameter
and explicitly supplies the remaining optional arguments can emit one extra
default argument in C. The Camp source is accepted, but native C compilation
fails because the generated call has too many arguments.

Steps to Reproduce:

1. Declare a struct containing a `string` field.
2. Declare a class method whose first parameter is `const char[]` and whose
   later parameters have defaults.
3. From another method in the same class, call the first method with the
   struct's string field and explicitly pass every later argument.
4. Build the project with the native C emitter.

Expected:
The string field expands to the pointer and length components required by the
array parameter, and the generated call contains exactly the explicitly
supplied later arguments.

Actual:
The generated C appends another default argument after the explicitly supplied
arguments. Clang reports that the call has one more argument than the generated
function accepts.

Additional exact repro (2026-09-06):

```camp
uint make(char[] output, const byte[] hash,
    const char[] identity = "textlib",
    const char[] version = "1.2.0") => 0;

void reproduce()
{
    fixed char[160] output = default;
    fixed byte[32] hash = default;
    make(output, hash, "other", "1.2.0");
}
```

The generated C call ends with the explicit `"1.2.0", 5` pair followed by an
extra `"1.2.0"` default pointer. Thus the failure is not limited to a `string`
field receiver; fixed-array expansion followed by explicitly supplied defaulted
slice parameters also reproduces it.

Known Impact:
Native builds cannot use this call shape. Route both public entry points through
a non-defaulted helper, or otherwise avoid combining the string-field expansion
with optional parameters at the affected call site.

## BUG-135: Array-return local can collide with generated result-length parameter

Date/Time: 2026-09-06 America/Toronto

Summary:
A function returning an array can emit an invalid C redeclaration when a local
array is named `result`. The generated array-return ABI already uses
`result_length` as an output parameter, while lowering the local also creates a
local named `result_length`.

Steps to Reproduce:

1. Compile this Camp function through the native C backend:

   ```camp
   byte[] copyBytes(const byte[] source, within allocator)
   {
       byte[] result = new byte[source.length];
       return result;
   }
   ```

2. Compile the generated C.

Expected:
The local array length and the hidden array-return length output use distinct C
identifiers, and the generated C compiles.

Actual:
The generated C function receives `uintptr_t *result_length`, then redeclares
`uintptr_t result_length = source_length`. Clang reports a redefinition with a
different type and an invalid pointer-to-integer assignment.

Known Impact:
Native compilation fails for this otherwise valid source shape. Rename the
local array to something other than `result` until generated identifier
collision handling is fixed.

## BUG-139: Early cleanup can access a later uninitialized `finally` local

Date/Time: 2026-09-06 America/Toronto

Summary:
An early cleanup transfer caused by a thrown failure can execute cleanup for a
local declared later in the function. The generated C does not give that later
local a cleanup-active guard or initialize its storage before the earlier
failure point, so cleanup reads an indeterminate pointer and may call a
destructor through it.

Steps to Reproduce:

1. Compile and run a test with a failing operation before a later owned local:

   ```camp
   class Resource { }

   @test
   void reproduce(within Allocator* allocator, thrown Assertion*)
   {
       assert(false);
       Resource* resource = new Resource() finally delete;
   }
   ```

2. Inspect the generated C cleanup path or run it with an uninitialized stack
   slot that contains a non-null value.

Expected:
Cleanup runs only for declarations whose initialization completed. A transfer
before `resource` is declared must not inspect or destroy `resource`.

Actual:
The assertion failure jumps to a shared cleanup label that tests and deletes
the uninitialized generated `resource` local. In the observed native test this
masked the original assertion as exit code 139, with a destructor receiving
the indeterminate pointer `0x2d`.

Known Impact:
Ordinary thrown failures can become undefined behavior or native crashes when
later declarations have `finally` cleanup. This can hide the original
diagnostic and makes failure paths dependent on incidental stack contents.
Declaring the owned local as `default` before the possible failure and assigning
it afterward avoids the uninitialized cleanup until lowering is corrected.
