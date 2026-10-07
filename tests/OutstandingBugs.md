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

Next bug number: BUG-261.

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

## BUG-259: Array-return initializer loses the enclosing within statement's allocator context

Date/Time: 2026-10-07 01:43 EDT

Summary:
Under explicit allocation policy, initializing an array local from an allocating
array-return call inside a `within (default)` statement incorrectly reports that
the call lacks an allocator context. The enclosing statement supplies that
context, but expansion of the array declaration does not preserve it.

Steps to Reproduce:

1. Compile this source with `campc build repro.camp --artifact none`:

```camp
#within explicit
escaped char[] join(const char[] first, const char[] second, within allocator)
{
	return new char[first.length + second.length];
}
export int main()
{
	within (default)
	{
		char[] joined = join("a", "bb") finally delete;
		return (int)joined.length - 3;
	}
}
```

Expected:
The source compiles. The call uses the explicitly selected default allocator
from its enclosing `within` statement.

Actual:
The initializer reports "Call requires a within context for parameter
'allocator'; prefix the call with a `within (allocator)` expression or place it
inside a `within (allocator)` statement."

Known Impact:
Array-return initializers can be rejected despite an enclosing explicit
allocator context. Repeating `within (default)` directly on the initializer
call compiles and avoids the missing-context diagnostic.
