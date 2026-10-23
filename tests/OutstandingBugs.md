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

Next bug number: BUG-352.

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

## BUG-350: A default method result on a local value receiver is tied to the receiver's address instead of what it holds

Date/Time: 2026-10-23 12:59 EDT

Summary:
A local struct has two lifetimes: its own memory, which for a local ends with
the function, and the lifetime of the values it holds, which is fixed by what it
was initialized from. A method result with the default `unscoped(this)` means
"something the receiver retains", so its lifetime should be the receiver's
content, not its address. The compiler substitutes the receiver's own storage
for the default result, so a result that is only a view of data the struct
points at is reported as tied to the local and cannot be returned, even though
reading the same field directly can. This depends on BUG-349: until a method
that returns an interior pointer must be declared `scoped(this)`, taking the
content fact for the default result would accept dangling results.

Steps to Reproduce:

1. Declare `struct SectionView { const byte[] image; nuint offset; nuint size;
   const byte[] payload() => this.image[this.offset..this.offset + this.size]; }`.
2. Declare `const byte[] fromField(const byte[] image)` that writes
   `SectionView section = { image, 4, 8 }; return section.image;`.
3. Declare `const byte[] fromMethod(const byte[] image)` that writes the same
   initializer and `return section.payload();`.
4. Declare `struct Result { const byte[] data; int code; }` and a function that
   writes the same initializer and `return { section.payload(), 0 };`.
5. Declare a function that fills a `fixed byte[16]` local, builds
   `SectionView section = { buffer[..], 4, 8 };` and returns `section.payload()`.
6. Compile.

Expected:
Steps 2, 3 and 4 compile: the struct was initialized from a caller-provided
value, so its content, and any result retained from it, has that lifetime. Step
5 is rejected, because its content is a local buffer. The same holds when the
struct is built by a constructor call or returned from another function.

For a result declared `scoped(this)` the call keeps using the receiver's
address, so a local receiver still cannot have it returned.

Actual:
Step 2 compiles. Steps 3 and 4 are rejected with "Return expression cannot
return a pointer-bearing value tied to local storage 'section'." Step 5 is
rejected correctly. The message does not say that the result comes from a method
or why the local is involved. It should name the call, give the result's declared
lifetime, and suggest a remedy: "Cannot return the result of 'section.payload()'.
Its declared lifetime is `scoped(this)`: it points into 'section', a local that
ends when this function returns. Return a copy, or take the value from storage
that outlives the function."

Known Impact:
View-like structs that hold a slice or pointer to caller data and expose it
through accessor methods cannot return those views from a function that builds
the struct locally. The only workarounds are lifetime casts that claim more than
is true, or turning the accessor into a free function.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md`: the sentence "A value
receiver borrows its storage for the call; its current value fact does not
replace that storage lifetime" applies to input retention checks; say so, and
state that a default result takes the receiver's content fact while a
`scoped(this)` result takes its address. Define `unscoped(anchor)` consistently
with `docs/language/07`.

## BUG-351: An explicit lifetime on the receiver is accepted but not enforced

Date/Time: 2026-10-23 12:59 EDT

Summary:
A method can state that its receiver is retained by one of its parameters, with
an explicit receiver lifetime such as `unscoped(destination) this`. The syntax is
accepted, but it has no effect. Inside the method, values derived from the
receiver are still rejected when stored into the parameter named as the anchor,
and at call sites the receiver is not checked against that parameter, so a
receiver that dies before the destination is accepted.

Steps to Reproduce:

1. Declare `struct NameView { const char[] name; bool enabled; }`.
2. Declare a struct `NameEntry` with `fixed char[16] characters;`, `uint length;`,
   `bool enabled;` and `const char[] name() => this.characters[..this.length];`
   declared `scoped(this)`.
3. Declare a struct `Settings` with `fixed NameEntry[8] entries;` and `uint count;`
   and the method `uint copyViews(unscoped(destination) this, NameView[] destination)`
   whose body stores `{ this.entries[i].name(), this.entries[i].enabled }` into
   `destination[i]`.
4. Declare a global `Settings` value and a function that fills a local
   `fixed NameView[8]` from a pointer to it with `settings.copyViews(views[..])`.
5. Declare a function taking a parameter `NameView[] longLived`, creating a local
   `Settings local = default;` and calling `local.copyViews(longLived)`.
6. Compile.

Expected:
The method body in step 3 compiles: the receiver is declared retained by
`destination`, so views derived from it may be stored there. The call in step 4
compiles, because the receiver outlives the local destination. The call in step
5 is rejected, because the receiver is a local that ends before the destination
that retains views of it.

The diagnostic for step 5 names the call, the receiver and the destination, and
says what to do. For example: "Cannot call 'copyViews': it requires the receiver
to outlive 'destination' (`unscoped(destination) this`), but 'local' is a local
that ends before the value passed for 'destination'. Declare 'local' where it
outlives the destination, or pass a destination that does not outlive it."

Actual:
Step 3 is rejected with "Assignment cannot store a scoped pointer-bearing value
in storage tied to 'destination'." Steps 4 and 5 compile without diagnostics, so
the annotation neither permits the store nor constrains the callers.

Known Impact:
There is no way to write a method that copies views of its own inline storage
into a caller-provided array. The remaining options are to copy the underlying
data instead of storing views, or to annotate the destination parameter
`unscoped`, which moves the error to every caller that passes local storage.

Documentation:
`docs/semantics/05-lifetime-analysis-and-flow-facts.md` already says an explicit
receiver lifetime overrides the default receiver fact. Add the meaning of
`unscoped(anchor) this` (the receiver is retained by the anchor, so it must
outlive it) and an example, and describe the body and call-site checks.
