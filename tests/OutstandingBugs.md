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

Next bug number: BUG-362.

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

## BUG-361: Method of a constructed generic base class is rejected on a derived-class pointer

Date/Time: 2026-10-24 12:20 EDT

Summary:
A class derived from a constructed generic base class (for example `Derived: Base<int>`) cannot call an
instance method declared on that base through a pointer to the derived class. The bind reports that the
method's `this` parameter is not compatible with the receiver. The same call through a pointer to the
base construction is accepted, and a derived class of a non-generic base can call the base's methods
through a derived pointer. Fields of the generic base are reachable through the derived pointer, so only
method receivers are affected. The error also appears for a call written inside the derived class itself
(`this.method()`), and an explicit `this` parameter on the base method does not change it.

Steps to Reproduce:

1. Compile the following program:

   ```camp
   class GBox<T> { int tag; int other() { return 1; } }
   class GD: GBox<int> { int extra; }
   export int main()
   {
   	GD* g = stackalloc GD();
   	GBox<int>* b = g;
   	int viaBase = b.other();
   	int viaDerived = g.other();
   	return 0;
   }
   ```

2. Replace `GBox<T>` and `GBox<int>` with a non-generic base class and compile again.

Expected:
Both calls compile. A derived-class pointer calls the methods inherited from its base class, and the
conversion to the exact base construction already succeeds, so the receiver of the inherited method
should accept the derived pointer. The non-generic variant compiles without error.

Actual:
The call through the derived pointer fails:

```text
error: Member 'other' exists on type 'GD*', but its this parameter is not compatible with that receiver.
```

The call through `GBox<int>*` compiles, as does the non-generic variant.

Known Impact:
Any class derived from a constructed generic base cannot use the base's instance methods directly.
Workaround: convert to the base construction first (`GBox<int>* b = g; b.other();`).
