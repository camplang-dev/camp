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

Next bug number: BUG-267.

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


## BUG-262: `typenameof` of an erased type fails C emission

Date/Time: 2026-10-07 02:40 EDT

Summary:
`typenameof` applied to a `void<V>` type, or to a type built on one such as
`void<V>*`, is accepted by semantic analysis but native emission rejects the
expression. Accepted proposal 024 defines `typenameof(void<V>)` as the empty
string, so the expression has a value and should compile.

Steps to Reproduce:

1. Compile this source with `campc run repro.camp --nostdlib` and an intrinsic
   logging shim:

   ```camp
   extern void __intrinsic_log_string(string value);
   export int main()
   {
       __intrinsic_log_string(typenameof(void<int>*));
       return 0;
   }
   ```

Expected:
The program builds and logs an empty line.

Actual:
The build fails with `C emission does not yet support expression node
NameOfExpression.`

Known Impact:
The empty erased type name cannot be observed directly. `typenameof` of
ordinary concrete types is unaffected.

## BUG-264: Imported array-returning method drops the last array length after a slice argument

Date/Time: 2026-10-07 03:40 EDT

Summary:
A call to another project's exported method that returns an array and takes two
array or view parameters is emitted without the length of the last array argument
when an earlier array argument is a range slice such as `left[1..]`. Passing the
same arguments without the slice is emitted correctly, as is the sliced call made
inside the declaring project. This is a remaining form of the imported
array-argument component positions fixed under BUG-255.

Steps to Reproduce:

1. Create `lib.campbuild` (`--name lib`, `--artifact static`, source `lib.camp`):

```camp
#within explicit
namespace Lib;

export escaped class Fmt
{
	export Fmt() { }
	export escaped char[] join(const char[] first, const char[] second, within allocator)
	{
		return new char[first.length + second.length];
	}
}
```

2. Create `app.campbuild` (`--name app`, `--artifact static`,
   `--project-reference lib.campbuild:static`, source `app.camp`):

```camp
#within explicit
namespace App;

int run(Lib::Fmt* f, const char[] left, const char[] right, within Allocator* allocator)
{
	char[] joined = f.join(left[1..], right) finally delete;
	return (int)joined.length;
}
export int entry(Lib::Fmt* f) { return within (default) run(f, "ab", "c"); }
```

3. Run `campc build app.campbuild`.

Expected:
The call passes the slice's elements and length, then `right` and `right_length`,
then the allocator and result length, and the project builds.

Actual:
The call is emitted as `LibFmt_join(f, <slice>, <slice length>, right, allocator,
(StdAllocator ***)&joined_length)`, omitting `right_length`, and the C compiler fails
with "too few arguments to function call, expected 7, have 6".

Known Impact:
Consumers of a generated API cannot pass a range slice before another array argument
to an imported array-returning method. Binding the slice to a local first avoids the
failure.

## BUG-265: Function-pointer type with an optional of an expandable payload drops a component

Date/Time: 2026-10-07 08:30 EDT

Summary:
A function-pointer or callable type whose parameter is an optional of an expandable
payload, such as `fn int(int??)` or `fn int(int[]?)`, is emitted with only two C
parameters for that optional: the payload and one specified flag. A function of the
same Camp signature takes every component (`int`, `bool`, `bool` for `int??`, and
elements, length and specified flag for `int[]?`), so assigning the function to the
callable value and calling through it do not compile. The nested type is also spelled
with an undeclared C name.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
extern void __intrinsic_log_i32(int value);
int describe(int?? value) { return value.specified ? 1 : 0; }
export int main()
{
	fn int(int??) measure = describe;
	int?? both = 5;
	__intrinsic_log_i32(measure(both));
	return 0;
}
```

2. Run `campc run repro.camp --nostdlib` with a shim providing `__intrinsic_log_i32`.

Expected:
The callable type carries every component of the optional, the program builds and it
prints `1`. The same holds for `fn int(int[]?)` assigned a function taking `int[]?`.

Actual:
The C compiler reports "unknown type name 'intOptional'", "incompatible function pointer
types initializing 'int (*)(int, bool)' with an expression of type 'int (int, bool, bool)'"
and "too many arguments to function call, expected 2, have 3". With `fn int(int[]?)` the
pointer is declared `int (*)(int *, bool)` for a function of type
`int (int *, uintptr_t, bool)`.

Known Impact:
Callable values cannot carry a nested optional or an optional array, delegate or other
expandable payload. Calling the function directly works.

## BUG-266: Erased `in` array argument is misplaced when the call also passes an `out`

Date/Time: 2026-10-07 09:05 EDT

Summary:
A call to an erased generic that passes an ordinary array to an `in T` parameter and
also passes an `out` argument packs the array argument incorrectly. The temporary meant
to hold the array's elements and length is initialized with the `sizeof(T)` value, and
the array's elements pointer is passed in the size capability's place, so the generated
C does not compile. The same call shape with an optional argument, or without the `out`
argument, works.

Steps to Reproduce:

1. Create `repro.camp`:

```camp
void store<T: copyable>(out T target, in T value, sizeof(T)) { target = value; }
export int main()
{
	int[] source = [1, 2, 3];
	int[] destination = [9];
	store<int[]>(out destination, source);
	return (int)destination.length;
}
```

2. Run `campc run repro.camp --nostdlib`.

Expected:
The program builds and exits with 3. Moving the `in T` parameter before the `out T`
parameter gives the same result.

Actual:
The C compiler reports "incompatible integer to pointer conversion initializing 'int *'
with an expression of type 'unsigned long'" for the packed argument and "incompatible
pointer to integer conversion passing 'int *' to parameter of type 'uintptr_t'" for the
size argument, in either parameter order.

Known Impact:
An array cannot be passed by `in` to an erased generic that also writes an `out`
argument.
