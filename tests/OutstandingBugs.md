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

Next bug number: BUG-229.

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

## BUG-227: Specifiers written before a global or field declaration are silently ignored

Date/Time: 2026-10-04 23:55 America/Toronto

Summary:
A call or type specifier written before the type of a global variable or an
aggregate field is accepted and discarded without validation. Specifiers apply
to the carrier they decorate, and call specifiers apply only to concrete
callables, so a call specifier on a non-callable carrier must be rejected and a
type specifier must be validated like any other. Instead the same specifier in
local-declaration position is validated, and the same specifier after the type
is validated, which makes the global and field behavior inconsistent.

Steps to Reproduce:

1. Compile a source file with global declarations such as `_targetcall int
   value;`, `_cdecl int value;` and `_far byte* pointer;`, and a struct with a
   field `_targetcall int field;`, on a target where `_far` is not proven.
2. Build it.

Expected:
Each declaration is diagnosed: a call specifier on `int` is not valid on a
non-callable carrier, and `_far` reports that its requirement is not proven
here, as the equivalent local declaration `_far byte* local = null;` and the
trailing form `byte* _far pointer;` already do.

Actual:
The build succeeds with no diagnostics and the specifier has no effect. In
function bodies the same leading specifier is diagnosed.

Known Impact:
Wrong or misplaced specifiers on globals and fields are not caught, and an
unproven target specifier can be accepted silently. Writing the specifier after
the type validates correctly.

## BUG-228: Declarations may be named after a target specifier

Date/Time: 2026-10-05 00:49 America/Toronto

Summary:
A name that the selected target (or the language) defines as a call specifier or
type specifier is accepted as the name of a declaration. A specifier name is
meaningful in type position, so reusing it as a variable name makes the
declaration ambiguous to read and to parse. A local variable must not be named
after any typespec or callspec, including the compiler-defined `_targetcall` and
`_targettype`.

Steps to Reproduce:

1. Compile an exported entry point whose body declares locals such as `int
   _cdecl = 1;`, `int _near = 2;` and `int _targetcall = 3;`.
2. Build it. The same names are also accepted for parameters, fields, global
   variables and function names.

Expected:
Each declaration is rejected because the name is a specifier.

Actual:
The build succeeds and the variables are usable by that name.

Known Impact:
Specifier names can be shadowed by ordinary declarations. The two compilers must
agree on which names are rejected, so the goldens cannot cover this until the
bootstrap diagnoses it.
