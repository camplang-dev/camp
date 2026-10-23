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

Next bug number: BUG-345.

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
A delegate that outlives its receiver can be created without a diagnostic. 

Documentation:
The semantic documentation does not state the rule for bound references. Extend `semantics/05-lifetime-analysis-and-flow-facts.md` (Delegates And Captures) so that a bound method reference carries the lifetime of its receiver as the hidden context of the callable, and state the receiver lifetime a target delegate type accepts.

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
Signatures with meaningless lifetime relations are accepted, and later call checks may behave unpredictably. The `within` repro uses the typed form because the bare form `within unscoped(owner) allocator` does not parse (see BUG-344).

Documentation:
`semantics/05-lifetime-analysis-and-flow-facts.md` (Invalid anchor uses) says anchors on `within` parameters are rejected "when they are not valid lifetime sources" without saying which are valid. Define it there: list which anchors a `within`, `thrown` and `out` slot may name, and which anchor combinations count as circular.

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

Documentation:
Update `semantics/05-lifetime-analysis-and-flow-facts.md` (Generic Boundaries) to say that members of a generic type are lifetime-checked like members of any other type, and that only values whose type is a type parameter are treated conservatively.

## BUG-342: A retained allocator is not required to outlive the object that keeps it

Date/Time: 2026-10-22 21:46 EDT

Summary:
A class constructor written `within this.allocator` keeps the allocator it is given so the object can free itself. The retained allocator parameter is implicitly `unscoped` relative to the object under construction (`semantics/10-construction-destruction-and-allocation.md`, Within parameter lifetimes), so the allocator passed at a construction must outlive the constructed object. No call site is checked against that rule. In particular `new` yields an escaped object, so the allocator it is constructed with must be escaped.

Steps to Reproduce:

1. Declare an `Allocator` interface and `class Owner { int value; Owner(within this.allocator) { this.value = 1; } ~Owner() { } }`.
2. Declare `Owner* make(Allocator* arena) { return within (arena) new Owner(); }`.
3. Also construct an `Owner` in a local whose lifetime outlasts a scoped allocator.

Expected:
Each construction is rejected when the allocator does not outlive the constructed object. In step 2 `arena` is scoped to the call while the returned object is escaped, so it is rejected. Constructing with an escaped allocator is accepted.

Actual:
No diagnostic is reported for any of them. Only async functions with an unscoped `within` parameter and `new delegate` lambdas that capture a non-escaped allocator are rejected.

Known Impact:
An object can outlive the allocator it needs for cleanup.

Documentation:
`semantics/10-construction-destruction-and-allocation.md` now states the rule (within parameter lifetimes and the implicit lifetime of retained allocators). Also add a sentence to Allocator Lifetime in the same file naming a retained allocator as one of the generated storage uses that must be checked at each construction.

## BUG-343: The unscoped(...) argument check is satisfied when argument and parameter names match

Date/Time: 2026-10-22 21:46 EDT

Summary:
A call checks that an argument for a parameter written `unscoped(owner)` outlives the argument bound to `owner`. The check is skipped when the caller's own variables have the same names as the callee's parameters.

Steps to Reproduce:

1. Declare `void store(const char[] owner, unscoped(owner) const char[] view) {}`.
2. Declare `void sameNames(const char[] owner, const char[] view) { store(owner, view); }`.
3. Declare `void otherNames(const char[] alpha, const char[] beta) { store(alpha, beta); }`.

Expected:
Both calls are rejected: the second argument is a parameter of the caller and is not known to outlive the first.

Actual:
`otherNames` reports "Argument cannot satisfy parameter lifetime 'unscoped(owner)'.". `sameNames` reports nothing.

Known Impact:
Whether a lifetime error is reported depends on the spelling of variable names.

Documentation:
No update needed; the `unscoped(anchor)` argument rule is already defined in `semantics/05-lifetime-analysis-and-flow-facts.md`.

## BUG-344: Bare `within` parameter lifetime forms: `within scoped allocator` is storable and `within unscoped(x) allocator` does not parse

Date/Time: 2026-10-22 22:20 EDT

Summary:
An unannotated `within` parameter is `scoped` like any other parameter, so it cannot be stored in a field tied to the receiver. Two problems affect the bare shorthand forms with a lifetime. `within scoped allocator` only states the default, so it must behave identically, but it is exempted from the receiver-storage check. `within unscoped(x) allocator`, with an anchor list, must be accepted like the anchored forms of other parameters, but the parser consumes only the lifetime keyword and then reads `allocator` as a type name.

Steps to Reproduce:

1. Declare an `Allocator` interface and `class Holder { Allocator* allocator; }` with the methods below.
2. Write `void viaDefault(within allocator) { this.allocator = allocator; }` and `void viaScoped(within scoped allocator) { this.allocator = allocator; }`.
3. Compare with `within unscoped allocator` and `within escaped allocator` bodies that store the same way, and with the typed form `within scoped Allocator* allocator`.
4. Declare `void anchored(const char[] owner, within unscoped(owner) allocator) {}`.

Expected:
`viaDefault` and `viaScoped` are both rejected with "Assignment cannot store a scoped pointer-bearing value in storage tied to 'this'." The unscoped and escaped forms are accepted. The declaration in step 4 is accepted, with the anchor resolved against the parameters of the declaration.

Actual:
`viaDefault` and the typed `within scoped Allocator* allocator` form are rejected. The bare `within scoped allocator` form is accepted. The declaration in step 4 reports "Unknown type 'allocator'.", and the typed form `within unscoped(owner) Allocator* allocator` is the only way to write an anchor.

Known Impact:
A receiver can keep a call-scoped allocator past the call, and an anchored `within` parameter cannot be written in the shorthand. The existing `tests/CCompile/within_allocator_lifetime_parameters.camp` compiles `ScopedHolder` with the store, and must change with the fix. The older unified spec (v28) described the store as intended.

Documentation:
`semantics/10-construction-destruction-and-allocation.md` now states the default and the explicit forms. When the anchored shorthand is implemented, extend that paragraph to say that `within unscoped(x) allocator` and `within scoped(x) allocator` take anchor lists as other parameters do, and which anchors a `within` parameter may name (see BUG-340).
