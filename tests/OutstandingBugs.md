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

Next bug number: BUG-401.

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

## BUG-399: A derived class of a retained-allocator root cannot declare its destructor

Date/Time: 2026-10-27 07:57 EDT

Summary:
A lifecycle root with a retained allocator (`Retained(within this.allocator)`) has a parameterless destructor, and its derived classes accept and forward a `within` context in their constructors. A derived class's destructor overrides the root's parameterless destructor slot, so it is parameterless too: the object is freed through the retained allocator, not through a context supplied to the destructor. The compiler rejects the parameterless override, because the derived constructor declares `within`. The `within` form it asks for instead does not match the slot and emits C that does not compile.

Steps to Reproduce:

1. Declare an `Allocator` interface with `alloc`, `realloc` and `free`, and a class implementing it (with the usual `malloc` and `free` externs).
2. Declare `virtual class Retained { int r; Retained(within this.allocator) { this.r = 2; } virtual ~Retained() { } }`.
3. Declare `sealed class Sub: Retained { Sub(within allocator) { } override ~Sub() { } }`.
4. In `main`, construct `Sub` with `within (allocator) new Sub()` and delete it.
5. Build with `campc run`. Repeat with `override ~Sub(within allocator) { }` in place of `override ~Sub() { }`.

Expected:
The first program builds and runs `~Sub` and then `~Retained`. The second is rejected at the destructor, because a destructor with a `within` parameter does not match the root's parameterless destructor slot.

Actual:
The first is rejected with "Destructor must declare a within allocator because an explicit constructor declares within." The second passes analysis and the native build fails with "incompatible function pointer types initializing 'void (*)(Retained *)' ... with an expression of type 'void (Retained *, Allocator **)'" for the vtable's `op_delete` entry.

Known Impact:
Derived classes of a retained-allocator root cannot have destructors. Workaround: omit the derived destructor and do the cleanup in the root.
