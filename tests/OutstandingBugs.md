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

Next bug number: BUG-350.

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

## BUG-349: A method that returns an interior pointer of its receiver is accepted with the default result lifetime

Date/Time: 2026-10-23 12:59 EDT

Summary:
A method result with no lifetime annotation has the default `unscoped(this)`.
That default says the result is something the receiver retains, such as a
pointer or slice stored in one of its fields. A result that points into the
receiver's own memory is a different thing: it is valid only as long as the
receiver is, and a caller must be told so with `scoped(this)`. The compiler does
not check this. A method that returns the address of a field, a slice or element
of an inline fixed-size field, or the receiver's own address is accepted with the
default result, so the signature promises more than the body delivers.

Steps to Reproduce:

1. Declare a struct with an `int value;` field, a `fixed char[8] chars;` field,
   and a `const byte[] outside;` field.
2. Add the methods `int* defaultAddress() => &this.value;` and
   `const char[] defaultChars() => this.chars[..];` with no lifetime annotation.
3. Add `scoped(this) int* scopedAddress() => &this.value;` and
   `scoped(this) const char[] scopedChars() => this.chars[..];`.
4. Add `const byte[] defaultContent() => this.outside;`.
5. Compile.

Expected:
The two methods in step 2 are rejected at the declaration: they return memory
inside the receiver while declaring the default result. The methods in step 3
and the method in step 4 compile; `defaultContent` returns something the
receiver retains, which is what the default promises.

The diagnostic names the method, the returned expression and the broken
promise, and gives the fix. For example: "Method 'defaultChars' returns
`this.chars[..]`, which points into the receiver's own memory, but its result
has the default lifetime `unscoped(this)`. The default means 'something the
receiver retains'. Memory inside the receiver ends when the receiver does.
Declare the result `scoped(this)`: `scoped(this) const char[] defaultChars()`."
The same message applies to the address of a field and to the address of `this`,
with the offending expression substituted.

Actual:
All of the methods compile without diagnostics.

Known Impact:
The default result lifetime cannot be relied on to mean "retained content", so
it cannot be used to let callers return results of methods on local value
receivers (see the related bug on call-site substitution, which depends on this
check). Accessors that return inline storage, such as a name stored in a fixed
character field, silently carry the wrong promise. Fixing this will require those
accessors to be declared `scoped(this)` in existing code.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md` describes the default
result both as `unscoped(this)` and as "tied to `this`, not escaped". The two
need to agree: the default means what the receiver retains, and a result that
points into the receiver's own memory is `scoped(this)`. The definition of
`scoped(anchor)` (the anchor's own memory) and `unscoped(anchor)` (what the
anchor retains) should be stated once and used by `docs/language/07` as well.
