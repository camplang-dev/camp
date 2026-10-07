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

Next bug number: BUG-259.

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

## BUG-254: Type name shadowed by a same-named property in a static method

Date/Time: 2026-10-06 23:45 EDT

Summary:
Inside a static method of a class, a qualified call on a type whose name matches a
property of that class (an accessor `getCatalog` declares the property `Catalog`) is
rejected with a message saying the property requires explicit `this.` qualification.
A static method has no `this`, and the name is used as the qualifier of a static call,
so it must resolve to the type. Earlier compilers resolve it to the type.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp --artifact none`:

```camp
public class Catalog { public static int begin() { return 1; } }
public class Holder
{
	Catalog* catalog;
	public Catalog* getCatalog() => this.catalog;
	public static int make() { return Catalog.begin(); }
}
export int main() { return Holder.make() - 1; }
```

Expected:
The source compiles. `Catalog.begin()` in the static method calls the static method of
the type `Catalog`.

Actual:
`error: Member property 'Catalog' requires explicit 'this.' qualification.` at the use
of `Catalog` in the static method.

Known Impact:
Any static method that calls a static member through a type name equal to a property
name of its own class fails to compile. Qualify the type with its namespace as a
workaround.

## BUG-255: Imported method returning an array drops the last array parameter's length

Date/Time: 2026-10-06 23:51 EDT

Summary:
When a project consumes another project's generated API, a call to an exported method
that returns an array and takes two or more array or view parameters is emitted without
the length argument of the last array parameter, and the following arguments shift into
the wrong slots. The same method called from within the library that declares it is
emitted correctly. A method with one array parameter, or one that returns a scalar, is
unaffected.

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
	char[] joined = f.join(left, right) finally delete;
	return (int)joined.length;
}
export int entry(Lib::Fmt* f) { return within (default) run(f, "a", "b"); }
```

3. Run `campc build app.campbuild`.

Expected:
The call is emitted as `LibFmt_join(f, left, left_length, right, right_length,
allocator, &joined_length)` and the project builds.

Actual:
The call is emitted as `LibFmt_join(f, left, left_length, right, allocator,
(StdAllocator ***)&joined_length)`, omitting `right_length`, and the C compiler
fails with "too few arguments to function call, expected 8, have 7".

Known Impact:
Consumers of exported methods or functions that return an array and take two or more
array parameters cannot build. A workaround is to pass one array parameter and the
other as a pointer and length, or to return the result through an `out` parameter.

## BUG-256: Indexing a dereferenced pointer to an array emits C with the wrong precedence

Date/Time: 2026-10-06 23:54 EDT

Summary:
Indexing the dereference of a pointer to an array, `(*pointer)[i]`, is emitted as
`*pointer[i]`, which the C compiler reads as `*(pointer[i])`. The index is applied to
the pointer to the elements component instead of to the elements, so the program reads
the wrong memory or crashes. Dereferencing into a local first and indexing the local is
emitted correctly, and `(*pointer).length` is correct.

Steps to Reproduce:

1. Compile and run this source with `campc run repro.camp`:

```camp
extern void __intrinsic_log_i32(int value);
export int main()
{
	int[] values = [10, 20, 30];
	int[]* pointer = &values;
	__intrinsic_log_i32((*pointer)[1]);
	return 0;
}
```

Expected:
The program logs 20.

Actual:
The generated C is `__intrinsic_log_i32(*pointer[1]);`. The program logs an
unrelated value or crashes. The forms `(*pointer).elements[2]` and
`pointer.elements[2]` are emitted the same way.

Known Impact:
Any index or slice of a dereferenced array pointer reads the wrong element. Copy the
dereferenced array to a local (`int[] view = *pointer;`) and index the local.

## BUG-257: Passing the address of an array to a pointer-to-const-array parameter emits a double address

Date/Time: 2026-10-06 23:54 EDT

Summary:
Passing `&array` directly as an argument for a `const T[]*` parameter is emitted as
`&&array`, which is not valid C. Storing the address in a `const T[]*` local first and
passing the local is emitted correctly, apart from a C warning that the initialization
discards a qualifier in a nested pointer type.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp`:

```camp
extern void __intrinsic_log_i32(int value);
int lengthThrough(const int[]* source) { return (int)(*source).length; }
export int main()
{
	int[] numbers = [10, 20, 30];
	__intrinsic_log_i32(lengthThrough(&numbers));
	return 0;
}
```

Expected:
The program builds and logs 3; `&numbers` converts implicitly to `const int[]*`.

Actual:
The C compiler fails with "use of undeclared label 'numbers'" for
`lengthThrough(&&numbers, &numbers_length)`.

Known Impact:
Calls that pass an array address to a pointer-to-const-array parameter do not build.
Bind the address to a `const T[]*` local and pass the local.

## BUG-258: Returning an array member through a struct value receiver emits pointer member access

Date/Time: 2026-10-07 00:46 EDT

Summary:
An extension function with a struct value receiver can bind and return its
array member, but native emission accesses the receiver as a pointer. The C
function receives the struct by value, so those member accesses are invalid.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp --nostdlib`:

   ```camp
   struct View { const char[] text; }
   const char[] span(View this) { return this.text; }
   export int main()
   {
       View view = { "abc" };
       return view.span().length == 3 ? 0 : 1;
   }
   ```

Expected:
The source builds and exits successfully. The value receiver's members use
value member access in C.

Actual:
The generated function receives `View this` but uses `this->text_length` and
`this->text`. Native compilation fails because `View` is not a pointer.

Known Impact:
Returning an array field from a struct value receiver can prevent native
compilation. Passing the struct as an ordinary named parameter avoids the
receiver-specific emission path.
