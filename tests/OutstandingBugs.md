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

Next bug number: BUG-265.

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
