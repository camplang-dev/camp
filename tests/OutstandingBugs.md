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

Next bug number: BUG-295.

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

## BUG-293: A pointer to an integer carrier does not convert to a pointer to a const carrier

Date/Time: 2026-10-12 19:03 EDT

Summary:
Adding `const` to the pointee is an implicit conversion for every pointer type (`int*` to `const int*`). For a pointer to an integer carrier (`long<Color>*`) the same conversion is rejected in every context, so a carrier cannot be passed, returned or stored through a read-only pointer.

Steps to Reproduce:

1. Declare `enum Color: int { RED, GREEN }`.
2. Declare `const long<Color>* viaReturn(long<Color>* p) => p;`.
3. Declare `void viaLocal(long<Color>* p) { const long<Color>* q = p; }`.
4. Declare `void viaArgument(const long<Color>* p) {}` and `void caller(long<Color>* p) { viaArgument(p); }`.
5. For comparison, `const int* plain(int* p) => p;` compiles.

Expected:
All three forms are accepted, as for `int*` to `const int*`.

Actual:
Each reports that it cannot convert `long<Color>*` to `const long<Color>*` (return expression, declaration initializer, argument).

Known Impact:
A carrier-typed value cannot be exposed read-only by pointer. Passing the mutable pointer avoids the problem.

## BUG-294: A generated project API omits the interface list of an exported struct

Date/Time: 2026-10-12 20:11 EDT

Summary:
A project that exports an interface and a struct implementing it produces an API declaration in which the struct no longer names the interface. The struct's method keeps its `: Reader` implementation marker, so the API contradicts itself, and a consuming project can neither convert the struct to the interface nor build against the API at all when the method is exported.

Steps to Reproduce:

1. In a producer project, declare `export interface Reader { int read(); }` and `export struct Cell: Reader { int value; int read(): Reader { return this.value; } }`.
2. In a second project that references the producer as a static project reference, write `Cell cell = { 5 }; Reader* r = &cell;`.
3. Build the second project.

Expected:
The generated API declares `export struct Cell : Reader`, so the conversion compiles and `r.read()` dispatches to the struct's method.

Actual:
The generated API declares `export struct Cell` with no interface list and `export extern int read() : Reader;` inside it. The consumer reports `Declaration initializer cannot convert 'Cell*' to 'Reader*'.` If the method is declared `export int read(): Reader`, the generated API itself fails to build with `Method 'read' cannot implement 'Reader' because type 'Cell' does not implement that interface.`

Known Impact:
An exported type cannot be used as an interface, directly or through a generic `implements` constraint, from another project. Keeping the implementing type and its interface in the same project avoids the problem.
