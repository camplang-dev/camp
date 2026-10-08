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

Next bug number: BUG-276.

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

## BUG-275: Private erased-array result leaks into a project API

Date/Time: 2026-10-08 09:00 America/Toronto

Summary:
An exported dynamic array result whose element is `void<PrivateType>` retains
the private annotation in the generated Camp API. A separate source-project
consumer cannot load that API, even when it never calls the declaration.
Opaque erased pointers, array parameters, in/out parameters and optional erased
results project successfully; the array-result path does not.

Steps to Reproduce:

1. Create `producer.camp` containing:
   ```camp
   struct Hidden { int value; }
   export void<Hidden>[] getItems() => default;
   ```
2. Create `producer.campbuild` containing `producer.camp`.
3. Create `consumer.camp` containing `export int main() => 0;` and
   `consumer.campbuild` containing:
   ```text
   --project-reference producer.campbuild:static
   consumer.camp
   ```
4. Run `campc build consumer.campbuild --nostdlib` with compiler `4621d5be`.

Expected:
The public interface projects inaccessible erased annotations to the opaque
boundary shape described by proposal 024 section 2.4, so the private name does
not prevent a separate consumer from loading it.

Actual:
The generated API contains `export extern void<Hidden>[] getItems();`, omits
`Hidden`, and consumer compilation fails with `Unknown type 'Hidden'` at the
generated result annotation.

Known Impact:
Source-project references cannot import a producer exposing this result shape.
An array parameter projects to a pointer and length successfully, so it can
exercise opaque array transport while this result-specific defect is fixed.
