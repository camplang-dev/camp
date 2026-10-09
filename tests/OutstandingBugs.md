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

Next bug number: BUG-280.

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

## BUG-279: Nested distributive pointer callable types emit inconsistent C signatures

Date/Time: 2026-10-08 20:47 EDT

Summary:
A callable type containing a distributive pointer to a nested optional does not
emit the same flattened C signature as its matching named function. The source
passes semantic checking, but native compilation fails because the function
pointer parameter retains an undeclared optional aggregate name and omits
component parameters. The indirect call supplies those components nevertheless.

Steps to Reproduce:

1. Save the following as `repro.camp`:

   ```camp
   int??* identity(int??* value) => value;
   export int main()
   {
       int?? value = default;
       fn int??*(int??*) callback = identity;
       int??* pointer = callback(&value);
       return (*pointer).specified ? 1 : 0;
   }
   ```

2. Run `campc run repro.camp` with compiler commit
   `36207137e4b5f58b7097bbba69aab678905e02ed` on macOS.

Expected:
The program compiles and exits zero. Per semantics 02, pointers distribute
recursively over expanded components and an indirect expanded-return call must
initialize every component from one invocation. Here the input has three
pointer components; the result has one return carrier and two result addresses.
The matching declaration, callable type and invocation must use that same shape.

Actual:
Clang rejects an unknown `intOptionalOptional` parameter type, incompatible
function pointer initialization and a five-argument call through a three-argument
function pointer. The declaration has the expected five physical arguments, but
the callable type omits the two input flag-pointer components.

Known Impact:
Indirect calls transporting nested distributive pointers cannot be compiled.
A direct call to the named function compiles, but does not preserve the callable
value/invocation contract and is not an equivalent workaround for that surface.
No bootstrap source or test files were changed while confirming this defect.
