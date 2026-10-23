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

Next bug number: BUG-343.

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

## BUG-337: Assignments to static fields skip escaped-storage lifetime checks

Date/Time: 2026-10-22 21:46 EDT

Summary:
Static fields of a class are escaped storage, but storing a scoped pointer-bearing value into one is accepted. The same store into a global variable is rejected.

Steps to Reproduce:

1. Declare a class with a `static` field of a pointer-bearing type (for example `static const char[] lastName;`).
2. In a static method of that class, assign a parameter, or a view of a fixed-size local array, to the field.
3. Compare with assigning the same value to a global variable.

Expected:
Both stores are rejected with the escaped-storage diagnostic, because static fields are escaped storage (`semantics/05-lifetime-analysis-and-flow-facts.md`, Slot Facts And Value Facts and the storage rule list).

Actual:
The global assignment reports "Assignment cannot store a non-escaped pointer-bearing value in escaped storage.". The static field assignments report nothing.

Known Impact:
A dangling pointer to a parameter or local can be left in static storage without a diagnostic.

## BUG-338: `new` of a class does not check pointer-bearing constructor arguments against what the constructor retains

Date/Time: 2026-10-22 21:46 EDT

Summary:
A constructor parameter written `unscoped` may be stored in a field. Value construction retains the argument's lifetime fact, so returning the constructed value is rejected. Heap construction with `new` yields an escaped value but does not check the arguments, so a heap object can hold a pointer to local storage.

Steps to Reproduce:

1. Declare `class Keeper { const char[] text; Keeper(unscoped const char[] input) { this.text = input; } ~Keeper() { } }`.
2. In a function, declare `fixed char[8] buffer = $"abc";` and write `Keeper* made = new Keeper(buffer[..]); return made;`.

Expected:
The construction is rejected, because the escaped object would retain a view of local storage. `semantics/05` (Construction And Retention) says this analysis "prevents code from constructing a heap object that stores a pointer to local stack storage".

Actual:
No diagnostic is reported for the `new` expression or the return.

Known Impact:
Heap objects can retain pointers to local or parameter storage that ends before the object does. The documentation does not currently state the `new` form of the rule; it should be added alongside the fix.

## BUG-339: Bound method references are not checked for the lifetime of their receiver

Date/Time: 2026-10-22 21:46 EDT

Summary:
A bound method reference such as `c.get` carries its receiver. Passing one to an `escaped delegate` parameter, or returning it as an escaped delegate, is accepted even when the receiver is a local value or a parameter. The only enforced case is an expanded receiver bound to an escaped delegate.

Steps to Reproduce:

1. Declare `struct Counter { int n; int get() => this.n; }` and `void takeEscaped(escaped delegate int() callback) {}`.
2. In a function, declare a local `Counter c` and call `takeEscaped(c.get)`.
3. Also call `takeEscaped(counter.get)` where `counter` is a `Counter*` parameter, and `return counter.get;` from a function returning `escaped delegate int()`.

Expected:
A bound method reference cannot retain a receiver beyond the receiver's lifetime, so each of these is rejected.

Actual:
None of them reports a diagnostic.

Known Impact:
A delegate that outlives its receiver can be created without a diagnostic. The semantic documentation does not state the rule for bound references; `semantics/05` (Delegates And Captures) should be extended to cover them as part of the fix.

## BUG-340: Invalid lifetime anchors on `within` and `thrown` parameters and circular anchors are accepted

Date/Time: 2026-10-22 21:46 EDT

Summary:
`semantics/05-lifetime-analysis-and-flow-facts.md` (Invalid anchor uses) says anchors on `within` parameters that are not valid lifetime sources, anchors to thrown slots that cannot observe them, and dependent anchors that would require circular substitution must be diagnosed at the annotation. Missing names are diagnosed, but these three forms are not.

Steps to Reproduce:

1. Declare `void circular(unscoped(second) const char[] first, unscoped(first) const char[] second) {}`.
2. Declare `void withinAnchor(const char[] owner, within unscoped(owner) Allocator* allocator) {}` with an `Allocator` in scope.
3. Declare `void thrownAnchor(const char[] owner, thrown unscoped(owner) int* error) {}`.

Expected:
Each annotation is rejected where it is written.

Actual:
No diagnostic is reported for any of them.

Known Impact:
Signatures with meaningless lifetime relations are accepted, and later call checks may behave unpredictably.

## BUG-341: Lifetime checks are skipped for members of generic classes

Date/Time: 2026-10-22 21:46 EDT

Summary:
A method or constructor of a generic class is not lifetime-checked, so a scoped parameter can be stored in a field tied to the receiver. The identical body in a non-generic class is rejected.

Steps to Reproduce:

1. Declare `interface Source { int read(); }`.
2. Declare `class Generic<T> { Source* source; void set(Source* other) { this.source = other; } }`.
3. Declare the same class without the type parameter.

Expected:
Both stores are rejected: a scoped parameter cannot be stored in a field tied to `this`.

Actual:
The non-generic class reports "Assignment cannot store a scoped pointer-bearing value in storage tied to 'this'.". The generic class reports nothing.

Known Impact:
Generic containers can retain dangling pointers without a diagnostic. Checking should still treat values of the type parameter conservatively as `semantics/05` (Generic Boundaries) describes, but pointer-bearing fields and parameters of known type should be checked.

## BUG-342: A retained allocator is not checked against the lifetime of the object that keeps it

Date/Time: 2026-10-22 21:46 EDT

Summary:
A class constructor written `within this.allocator` keeps the allocator it is given so the object can free itself. When the object is created with `new` inside a scoped allocator context, the object is escaped but the allocator it keeps may end sooner.

Steps to Reproduce:

1. Declare an `Allocator` interface and `class Owner { int value; Owner(within this.allocator) { this.value = 1; } ~Owner() { } }`.
2. Declare `Owner* make(Allocator* arena) { return within (arena) new Owner(); }`.

Expected:
The construction is rejected, because the returned object retains `arena`, which is scoped to the call. `semantics/10-construction-destruction-and-allocation.md` (Allocator Lifetime) says analysis must reject allocator values that cannot outlive the generated storage that needs them.

Actual:
No diagnostic is reported. Only async functions with an unscoped `within` parameter and `new delegate` lambdas that capture a non-escaped allocator are rejected.

Known Impact:
An object can outlive the allocator it needs for cleanup.
