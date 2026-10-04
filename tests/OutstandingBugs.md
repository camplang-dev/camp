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

Next bug number: BUG-223.

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

## BUG-221: Missing semantic diagnostic for inline constant name reuse

Date/Time: 2026-10-04 00:30 EDT

Summary:
A parameter or local declaration that reuses a top-level inline constant's name
is invalid and should receive a semantic collision diagnostic before lowering.
Instead, analysis accepts the declaration and C emission produces a macro that
corrupts it. Inline constant names are reserved against reuse; ordinary lexical
shadowing does not make this source valid.

Steps to Reproduce:

1. With compiler revision `ccb56c3cb2f151f2b051b69f897e301887839b4b`, save this
   source as `inline_name_reuse.camp`:

   ```camp
   inline int value = 11;

   int identity(int value)
   {
       return value;
   }

   export int main()
   {
       return identity(17) - 17;
   }
   ```

2. Run `campc run inline_name_reuse.camp --nostdlib --show-errorlevel --out-dir out`.
3. Inspect the generated C if the native build fails.

Expected:
Semantic analysis rejects the parameter name `value` because it reuses the
inline constant's name, identifying the conflicting constant before lowering
or native compilation. No successful executable or runtime output is expected.
The Symbol Collisions rules in
`docs/semantics/11-metadata-api-surface-and-symbols.md` require analyzer collision
checks to include inline constants. Declaration Collection and Declaration
Validation in `docs/semantics/01-binding-analysis-and-lowering-pipeline.md`
require invalid declarations to be diagnosed before lowering. The explicit
inline-name reservation rule was clarified by the language owner.

Actual:
Native compilation fails with `expected ')'` and conflicting function-type
errors. Generated C places `#define value ((int)11)` before
`static int identity(int value)`, so macro expansion corrupts the parameter
declaration. No executable runs.

Known Impact:
Invalid name reuse is reported only by the native C compiler, with confusing
generated-code errors instead of a source-level semantic diagnostic. The same
macro also corrupts same-named local declarations in a larger reproduced
program. Use distinct names for parameters, locals and other declarations;
inline constant names must not be used to test permitted lexical shadowing.

## BUG-222: Typed lifetime fences emit nonportable C struct casts

Date/Time: 2026-10-04 01:37 EDT

Summary:
A lifetime fence that explicitly repeats a struct value's type emits a C cast
from that struct to the same struct. MSVC rejects the generated aggregate cast,
so valid Camp source fails native compilation. The documented lifetime-only
form preserves the lifetime assertion and avoids the emitted C cast.

Steps to Reproduce:

1. On Windows with the MSVC native toolchain available, use compiler revision
   `e2dce5956412d8c76a02a16d17e0988db11fc78d` and save this as `struct_fence.camp`:

   ```camp
   struct Payload { int value; }
   struct Envelope { Payload payload; }

   export int main()
   {
       Payload payload = { 42 };
       fixed Envelope[1] records = default;
       records[0] = { (escaped Payload)payload };
       return records[0].payload.value - 42;
   }
   ```

2. Run `campc run struct_fence.camp --nostdlib --out-dir out`.
3. Replace `(escaped Payload)payload` with `(escaped)payload` and repeat.

Expected:
Both forms compile and run with exit status 0. The Lifetime Cast Syntax rules
in `docs/semantics/03-conversions-raw-carriers-and-fence-casts.md` allow lifetime
assertions to combine with type syntax and distinguish lifetime analysis from
value conversion. Repeating the unchanged struct type requires no native value
conversion; emission must preserve the value without a nonportable C cast.

Actual:
The typed form emits an initializer containing `(Payload)(payload)` and MSVC
reports C2440, "'type cast': cannot convert from 'Payload' to 'Payload'". The
lifetime-only form compiles and runs with exit status 0. The same failure was
observed for a typed lifetime fence around a struct aggregate initializer.

Known Impact:
Typed lifetime fences on struct values prevent native MSVC builds. Use
`(escaped)value` when the type is unchanged. For a typed aggregate initializer,
first initialize a local struct value and then apply the lifetime-only fence
when storing it. Preserve the lifetime assertion and the ownership conditions
that justify it; removing the assertion is not the workaround.
