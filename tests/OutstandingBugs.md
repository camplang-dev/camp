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

Next bug number: BUG-372.

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

## BUG-370: Duplicate base interfaces and redeclared inherited interface members are not diagnosed

Date/Time: 2026-10-25 13:30 PDT

Summary:
The semantics require name and signature conflicts in interface inheritance to be diagnosed. Two cases are accepted at the interface declaration. An interface that lists the same base interface twice passes semantic analysis and then fails the native build with a duplicate member in the generated private header. An interface that declares a method with the same name as a method it inherits, whether the signature is identical or different, also passes. The declared interface can then never be implemented: every implementing type is rejected with the marker "is ambiguous" and "does not implement" errors that point at the implementer rather than at the interface declaration.

Steps to Reproduce:

1. Compile a program containing `interface Reader { int get(); }` and `interface Twice: Reader, Reader { void e(); }`.
2. Compile a program containing `interface Reader { int get(); }` and `interface Narrow: Reader { long get(); }`.
3. Repeat step 2 with `interface Same: Reader { int get(); }`, then add `struct Impl: Same { int get(): Same { return 2; } }`.

Expected:
Each interface declaration is rejected with a diagnostic at the declaration: a repeated base interface in step 1, and a member that redeclares an inherited member name in steps 2 and 3. Deriving from two unrelated interfaces that each declare a method of the same name remains valid, and the ambiguity is diagnosed where the member is used.

Actual:
Steps 1 to 3 report no error at the interface declaration. Step 1 fails in the C compiler with "duplicate member" in the generated private header. Step 3 reports "Interface method marker 'Same.get' is ambiguous", then "Type 'Impl' does not implement interface member 'Same.get()'" and "Type 'Impl' does not implement interface member 'Reader.get()'".

Known Impact:
Interface authors get a native compiler failure or confusing errors at every implementer instead of one error at the declaration. A derived interface cannot be implemented until the redeclaration is removed.

## BUG-371: A call through an interface with two base interfaces dispatches the second base's slots to the first base

Date/Time: 2026-10-25 14:10 PDT

Summary:
An interface that inherits from two interfaces gets a slot table in which the members of the second base are bound to the members of the first. Calling a second-base member through a pointer to the derived interface runs the first base's implementation. Converting that pointer to the second base and calling through the result is wrong in the same way. Converting the implementing value directly to the second base gives the correct result. The behavior is the same for a struct and for a class, and when the two bases share a common base of their own.

Steps to Reproduce:

1. Declare `interface Left { int left(); }`, `interface Right { int right(); }` and `interface Both: Left, Right { int both(); }`.
2. Declare a struct implementing `Both` whose `left()` returns 10, `right()` returns 20 and `both()` returns 30, each marked with its declaring interface.
3. Convert a local of that struct to `Both*` and log the results of `left()`, `right()` and `both()` through that pointer.
4. Repeat with a sealed class in place of the struct.

Expected:
The calls return 10, 20 and 30.

Actual:
The calls return 10, 10 and 30. `right()` runs the implementation of `left()`. A `Right*` obtained from the `Both*` returns 10 from `right()`, while a `Right*` converted directly from the struct returns 20.

Known Impact:
Any interface with more than one base interface silently runs the wrong method for the second and later bases, with no diagnostic.
