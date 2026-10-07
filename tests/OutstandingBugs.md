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

Next bug number: BUG-263.

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
