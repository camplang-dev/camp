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

Next bug number: BUG-279.

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

## BUG-275: Private erased-array result leaks into a project API

Date/Time: 2026-10-08 09:00 America/Toronto

Summary:
An exported dynamic array result whose element is `void<PrivateType>` retains
the private annotation in the generated Camp API. A separate source-project
consumer cannot load that API, even when it never calls the declaration.
Opaque erased pointers, array parameters, in/out parameters and optional erased
results project successfully; the array-result path does not.

Steps to Reproduce:

1. Create `producer.camp` containing:
   ```camp
   struct Hidden { int value; }
   export void<Hidden>[] getItems() => default;
   ```
2. Create `producer.campbuild` containing `producer.camp`.
3. Create `consumer.camp` containing `export int main() => 0;` and
   `consumer.campbuild` containing:
   ```text
   --project-reference producer.campbuild:static
   consumer.camp
   ```
4. Run `campc build consumer.campbuild --nostdlib` with compiler `4621d5be`.

Expected:
The public interface projects inaccessible erased annotations to the opaque
boundary shape described by proposal 024 section 2.4, so the private name does
not prevent a separate consumer from loading it.

Actual:
The generated API contains `export extern void<Hidden>[] getItems();`, omits
`Hidden`, and consumer compilation fails with `Unknown type 'Hidden'` at the
generated result annotation.

Known Impact:
Source-project references cannot import a producer exposing this result shape.
An array parameter projects to a pointer and length successfully, so it can
exercise opaque array transport while this result-specific defect is fixed.

## BUG-276: A within expression drops an ordinary generic array-result call

Date/Time: 2026-10-08 09:32 America/Toronto

Summary:
A generic call returning an ordinary dynamic array `T[]` is omitted when used
as a local initializer inside a `within` expression. The omitted call can have
observable side effects. This differs from an erased generic `T` result whose
argument happens to be an array; that result-storage path works.

Steps to Reproduce:

1. Compile and run the following with `campc run repro.camp --nostdlib
   --show-errorlevel`, using compiler `c6825ae6`:
   ```camp
   interface Allocator {
       escaped void* alloc(nuint size);
       void* realloc(void* memory, nuint size);
       void free(void* memory);
   }
   int calls = 0;
   T[] echo<T: copyable>(T[] values, within allocator, sizeof(T)) {
       calls++;
       return values;
   }
   export int main() {
       Allocator* allocator = null;
       int[] values = [9];
       int[] returned = within (allocator) echo<int>(values);
       return calls == 1 ? 0 : 1;
   }
   ```
2. Inspect the emitted C initializer and the exit code.

Expected:
The initializer invokes `echo`, passes the array components and supplied
capabilities, and assigns both result components. `within` supplies an allocator
without suppressing evaluation, so the program exits with zero.

Actual:
The C declares `returned` and `returned_length` without any initializer or call.
The program exits with one because `calls` is still zero. This repro never reads
the uninitialized result.

Known Impact:
Ordinary generic array-result calls inside `within` cannot safely prove execution
or result transport. Tests of erased generic result transport can instead use a
`T` result with an array argument; that preserves the erased result-storage
contract but does not prove this ordinary array-result path.

## BUG-277: Relaxed callable-newtype initialization emits an incompatible C pointer

Date/Time: 2026-10-08 09:32 America/Toronto

Summary:
A named function ascribed to a callable newtype may use the non-expandable
`in P` versus `in void<P>` compatibility rule. Initializing that newtype from the
ascribed name succeeds in semantic analysis but emits a C assignment between
incompatible pointer signatures.

Steps to Reproduce:

1. Compile the following with `campc run repro.camp --nostdlib
   --show-errorlevel`, using compiler `c6825ae6`:
   ```camp
   newtype fn int Reader(in void<int> value);
   int read(in int value): Reader => value;
   export int main() {
       Reader callback = read;
       return callback(0);
   }
   ```
2. Compare an explicitly cast initializer: `Reader callback = (Reader)read;`.

Expected:
Proposal 024 section 5 applies this relaxation to callable-newtype ascription.
The compatible named function initializes the nominal callable carrier and the
program returns zero.

Actual:
Clang rejects assigning `int (*)(int*)` to the `Reader` carrier
`int (*)(const void*)`. Semantic analysis accepts the ascription; the failure
occurs during native compilation.

Known Impact:
Implicit initialization from a compatible ascribed function fails when erased
and concrete parameter spellings differ. An explicit cast to the already
ascribed callable newtype compiles and preserves the intended ascription check;
it is a safe workaround for testing this compatibility rule.

## BUG-278: An optional cast argument loses its specified component at an erased reference

Date/Time: 2026-10-08 09:43 America/Toronto

Summary:
A cast that creates an optional value is accepted as an `in void<P>?` argument,
but the generated call omits the optional's specified component. Other optional
rvalues, including a function result, are expanded correctly at this boundary.

Steps to Reproduce:

1. Run `campc run repro.camp --nostdlib --show-errorlevel` with compiler
   `c6825ae6` on the following source:
   ```camp
   int read(in void<int>? value) => value.specified ? value.value : -1;
   export int main() => read((int?)7) == 7 ? 0 : 1;
   ```
2. Inspect the generated C call and compare passing a function result
   from `int? make() => 7;` instead of the cast expression.

Expected:
The optional cast creates a specified value containing seven. The caller supplies
addresses for both its payload and specified components, and the program exits
with zero. Proposal 024 sections 3 and 7 preserve those optional components.

Actual:
C declares `read(const void *value, bool *value_specified)` but calls
`read(&(int){(int)(7)})`. Clang rejects the call for having too few arguments.

Known Impact:
Optional cast rvalues cannot be passed to these erased reference parameters.
A function returning the same optional value passes both components correctly
and safely preserves an rvalue-reference test's purpose.
