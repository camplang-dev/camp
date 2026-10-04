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

Next bug number: BUG-226.

## BUG-225: Export projection names of functions, inline constants and aliases resolve in the producer's own source

Date/Time: 2026-10-04 13:55 EDT

Summary:
An export projection renames a declaration only on the external API surface; inside the artifact, source continues to use the original declaration name (proposal 006, "Export Projections"; language guide 10, "The source declaration keeps its original Camp name inside the artifact"). The compiler enforces this for projected type names and for renamed type members, but not for projected functions, inline constants or aliases: a source reference to the projected name binds and compiles.

Steps to Reproduce:

1. Compile a file containing `public int addOne(int value) { return value + 1; }`, `export addOne as projected_add_one;` and `export int main() { return projected_add_one(1); }`.
2. Repeat with `public inline int LIMIT = 7; export LIMIT as EXPORTED_LIMIT;` and a use of `EXPORTED_LIMIT`, and with `public alias Count = int; export Count as ExportedCount;` and a local of type `ExportedCount`.

Expected:
Each reference to the projected name is rejected the way a projected type name is (for example "Export projection name 'ExportedCount' is only used by the exported API surface; use the source name ..."), because only the original name is in source scope.

Actual:
All three compile without a diagnostic. A projected struct name (`export Pixel as ExportedPixel;`, then `ExportedPixel p = default;`) and a renamed type member are rejected as expected.

Known Impact:
Source can depend on a name that is meant for the external API only, so the external naming leaks into the artifact's own lookup. Workaround: none needed for valid programs; use the original names.

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

