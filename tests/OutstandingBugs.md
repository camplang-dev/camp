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

## BUG-229: Prefix increment and decrement do not check for const targets

Date/Time: 2026-10-05 10:53 America/Toronto

Summary:
`++target` and `--target` are not rejected when the target is read-only, while
the postfix forms `target++` and `target--` are. The generated C then fails to
compile instead of the compiler reporting a diagnostic.

Steps to Reproduce:

1. Compile an exported entry point with `const int c = 1; ++c; --c;`, a
   `const` struct local with `++box.value;`, and `const int* view = &value;
   ++*view; --*view;`.
2. Build it.

Expected:
Each prefix update is rejected as an update of a const target, the way
`c++`, `box.value++` and `(*view)--` are ("Update target is const and cannot be
assigned.").

Actual:
No diagnostic. The native build fails with, for example, "cannot assign to
variable 'c' with const-qualified type 'const int'".

Known Impact:
A user error surfaces as a C compiler failure with no Camp source location.

## BUG-230: Writes through a const array view are not diagnosed

Date/Time: 2026-10-05 10:53 America/Toronto

Summary:
Assigning to, or incrementing, an element of a `const` array view is accepted by
analysis. Writes through a `const` pointer and to a `const` struct are
diagnosed.

Steps to Reproduce:

1. Compile `void touch(const int[] items) { items[0] = 7; items[1]++; ++items[1]; }`
   and call it from an exported entry point.
2. Build it.

Expected:
Each write is rejected because the elements of a `const` view are read-only.

Actual:
No diagnostic. The native build fails with "read-only variable is not
assignable".

Known Impact:
Same as BUG-229: the error is reported by the C compiler without a Camp source
location, and a different backend could accept the write.

## BUG-231: Updating a getter-only property is not diagnosed

Date/Time: 2026-10-05 10:53 America/Toronto

Summary:
`value.Name = next` and `value.Name += next` are rejected when the type has a
`getName()` but no `setName()` ("Property 'Name' is not writable"), but
`value.Name++` is accepted.

Steps to Reproduce:

1. Declare `class Holder { int stored; int getTotal() => this.stored; }` and
   compile an exported entry point containing `Holder* h = &holder; h.Total++;`.
2. Build it.

Expected:
The update is rejected because the property has no setter.

Actual:
No diagnostic. The generated C is `Holder_getTotal(...)++`, which fails with
"expression is not assignable".

Known Impact:
Same as BUG-229.

## BUG-232: Assigning to an inline constant is not diagnosed

Date/Time: 2026-10-05 10:53 America/Toronto

Summary:
An `inline` constant can be the target of an assignment without a diagnostic.

Steps to Reproduce:

1. Compile `inline int LIMIT = 10;` with an exported entry point containing
   `LIMIT = 11;`.
2. Build it.

Expected:
The assignment is rejected because an inline constant is a compile-time value.

Actual:
No diagnostic. The native build fails with "expression is not assignable".

Known Impact:
Same as BUG-229.

## BUG-233: Integer literals outside the range of their target are accepted

Date/Time: 2026-10-05 11:21 America/Toronto

Summary:
An integer literal that does not fit the primitive type it initializes or is
assigned to is accepted and silently truncated by the C compiler. Enum values and
newtype literals are range-checked; ordinary primitive targets are not.

Steps to Reproduce:

1. Compile an exported entry point containing `byte a = 256;`, `short b = 32768;`,
   `int c = 2147483648;`, `uint d = 4294967296;` and `ulong e = 18446744073709551616;`.
2. Build it.

Expected:
Each literal is rejected because its value is outside the target type's range.

Actual:
No diagnostic for the first four; clang warns that the value changes (256 becomes
0). The last fails in the C compile ("integer literal is too large to be
represented in any integer type") because the emitted C literal is invalid.

Known Impact:
Wrong values are stored silently. A literal wider than 64 bits surfaces as a C
compiler error with no Camp source location.

## BUG-234: auto with no inferable type has no Camp diagnostic

Date/Time: 2026-10-05 11:21 America/Toronto

Summary:
`auto` on a local whose initializer gives no type (no initializer, `null`, or
`default`) is not diagnosed by analysis. The error surfaces later in C emission
or in clang. The expected diagnostic is a generic one: the type cannot be
inferred, so a type must be written.

Steps to Reproduce:

1. Compile an exported entry point containing `auto missing;`, or
   `auto nothing = null;`, or `auto fromDefault = default;` (one per build).
2. Build it.

Expected:
A source-located error that the type of the expression cannot be inferred and a
type must be specified.

Actual:
`auto missing;` and `auto fromDefault = default;` stop with "C emission aborted
because DeclarationTarget ... has unresolved type '#ERROR'". `auto nothing = null;`
emits `_NULL nothing = NULL;` and fails in clang with "use of undeclared
identifier '_NULL'". (`auto x = { 1, 2 };` is diagnosed: "Initializer expression
requires a target type.")

Known Impact:
The error has no Camp source location, and different forms fail at different
stages.
