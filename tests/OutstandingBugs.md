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
