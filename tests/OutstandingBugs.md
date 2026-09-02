# Outstanding Bugs

Before adding a bug, verify the behavior against the language semantics and
confirm that it is in fact a bug. The explanation should include repro
instructions and should be written in general terms, without reference to a
particular file, package, or product.

Bugs should be added to this file one at a time. After each bug is added, commit
`OutstandingBugs.md` to the repo and include the new bug number in the commit
message.

When committing a change that fixes a bug, or is related to a bug, reference the
bug number in the commit message. The final commit that fixes a bug, or the only
commit if there is just one, should delete the bug from this file and include
that `OutstandingBugs.md` change in the same commit.

Next bug number: BUG-097.

## BUG-096: Test allocator rejects a live allocation after its address is reused

Status: Open

The test runner records every allocation in a newest-first linked list and
looks up allocations by returning the first record whose pointer value matches.
When a live allocation is moved by `realloc` to an address previously used by a
newer, freed allocation, both records have the same pointer value. A later
`realloc` or `free` finds the newer freed record instead of the older live
record and incorrectly reports that the live pointer was already freed.

Generalized repro:

1. Run a tracked Camp test that keeps a growable allocation alive while making
   and releasing temporary allocations through the same test allocator.
2. Grow the live allocation repeatedly so the host allocator moves it into an
   address formerly occupied by one of the newer temporary allocations.
3. Grow or release that live allocation again.

Expected: allocation lookup selects the live record for the reused address, so
the valid `realloc` or `free` succeeds.

Actual: allocation lookup selects the newer freed record and reports
`memory-invalid-realloc` or `memory-invalid-free`. A rejected `realloc` returns
null to the program under test, so the program can observe truncated or missing
data before the test runner reports the allocator error.

The behavior was verified under the debugger at the rejected `realloc`: the
allocation history contained two records for the requested pointer, with the
first record marked freed and a later record of the same size marked live.

Known impact: allocator-intensive tests can fail or execute incorrectly after
ordinary host-allocator address reuse, even when the code under test has valid
allocation ownership.

## ~~BUG-088: API emission suppresses valid source-authored `destroy` methods~~

Status: Fixed

The API header emitter suppresses methods named `destroy` as generated lifecycle
implementation details even when the method was explicitly declared by source
code.

The compiler already rejects declaring a `destroy()` method on a type with an
explicit destructor. The remaining bug is API emission only: if a source-authored
`destroy()` method is valid in the declaring module, it should also be emitted
when it is part of the public API surface.

Generalized repro:

```camp
// library.camp
namespace Repro;

public escaped class Resource
{
	public void destroy()
	{
	}
}
```

Build the source as an API/static/shared package and inspect the generated API
header.

Expected: the API header contains `public extern void destroy();` because the
method is source-authored and was accepted by declaration analysis.

Actual: the generated API header omits the method, so consuming modules cannot
call it.

Known impact: packages cannot expose a valid public method named `destroy`
because API emission currently treats the name itself as sufficient evidence
that the method is a generated lifecycle helper.

## ~~BUG-087: Override signature lookup does not recognize same-namespace types from another source file~~

Status: Fixed

An override method declared in one source file can fail to resolve an unqualified
type declared in another source file, even when both files are in the same
namespace. The diagnostic incorrectly says the type is declared in that namespace
but is not imported by the file.

This appears to be specific to override signature analysis. A top-level function
or ordinary member method in the same namespace can resolve the same type
correctly.

Generalized repro:

```camp
// a.camp
namespace A::B;

export virtual class Base
{
	export virtual void f(Thing* value)
	{
	}
}

export struct Thing
{
}
```

```camp
// b.camp
namespace A::B;

export sealed class Derived: Base
{
	export override void f(Thing* value)
	{
	}
}
```

Expected: `Thing` is visible because `b.camp` is in namespace `A::B`, the same
namespace where `Thing` is declared.

Actual: the compiler reports that `Thing` is declared in namespace `A::B` but is
not imported by the file.

Known impact: projects that split a virtual base type and derived overrides
across source files can require unnecessary self-imports or fail with misleading
namespace diagnostics.
