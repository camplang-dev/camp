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

Next bug number: BUG-361.

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

## BUG-360: Implementations of members that require an escaped receiver are checked inconsistently

Date/Time: 2026-10-23 22:01 EDT

Summary:
Implementations of an interface member or callable newtype that requires an escaped
receiver are not checked against that requirement consistently. The members of an
`escaped interface`, a single interface member declared `escaped this`, and a
callable newtype with an `escaped this` receiver should all place the same
requirement on their implementations, but the bootstrap accepts some invalid
implementations and rejects some valid ones. Related: BUG-346, BUG-347, BUG-348
(escaped types) and BUG-351 (explicit receiver lifetimes).

The rule: a member that requires an escaped receiver is implemented by a method
whose receiver is escaped. In an escaped class the receiver is escaped by default,
so `run()` and `run(escaped this)` are both valid. In a class that is not escaped
the method must write `escaped this` explicitly; a default receiver is rejected,
because the generated `getInterface()` conversion has an `escaped this` receiver and
only an escaped class instance can satisfy it (the conversion site rejects any
other source). Every member of an `escaped interface` carries this requirement
without writing it. An implementation that demands `escaped this` where the member
provides an ordinary receiver is rejected, because the caller may pass a scoped
receiver. A struct implementing an escaped interface keeps its default receiver
(see the `StackTask` example in `docs/language/12-interfaces-and-dynamic-dispatch.md`);
the scoped adapter is rejected at the conversion, not at the declaration.

Steps to Reproduce:

1. Declare these members and callable newtypes:
   - `escaped interface Task { void run(); }`
   - `interface Mixed { void keep(escaped this); void plain(); }`
   - `newtype delegate void EscapedDelegate(escaped this);`
   - `newtype delegate void NormalDelegate();`
2. Declare the implementations below and compile.

Escaped interface member (`Task.run()`):
```camp
class A: Task { void run(): Task { } }                      // not escaped, default receiver
class B: Task { void run(escaped this): Task { } }          // not escaped, explicit
escaped class C: Task { void run(): Task { } }              // escaped, default
escaped class D: Task { void run(escaped this): Task { } }  // escaped, explicit
struct E: Task { void run(): Task { } }                     // struct, default
```

Interface member declared `escaped this` (`Mixed.keep`) and an ordinary member (`Mixed.plain`):
```camp
class F: Mixed { void keep(): Mixed { } void plain(): Mixed { } }                       // keep: default
class G: Mixed { void keep(escaped this): Mixed { } void plain(): Mixed { } }
escaped class H: Mixed { void keep(): Mixed { } void plain(): Mixed { } }
escaped class I: Mixed { void keep(escaped this): Mixed { } void plain(): Mixed { } }
class J: Mixed { void keep(escaped this): Mixed { } void plain(escaped this): Mixed { } }  // plain: escaped this
```

Callable newtype ascription:
```camp
class K { void m() : EscapedDelegate { } }
class L { void m(escaped this) : EscapedDelegate { } }
escaped class M { void m() : EscapedDelegate { } }
escaped class N { void m(escaped this) : EscapedDelegate { } }
class O { void m(escaped this) : NormalDelegate { } }
escaped class P { void m() : NormalDelegate { } }
```

Expected:
| Case | Expected | Bootstrap today |
|---|---|---|
| A | rejected, with an explanation and the suggestion to write `escaped this` or make the class `escaped` | accepted |
| B | accepted | rejected: "not compatible with interface member" |
| C | accepted | accepted |
| D | accepted | rejected: "not compatible with interface member" |
| E | accepted | accepted |
| F | rejected | rejected |
| G | accepted | accepted |
| H | accepted | rejected: "not compatible with interface member" |
| I | accepted | accepted |
| J (`plain` with `escaped this`) | rejected | rejected |
| K | rejected | accepted |
| L | accepted | accepted |
| M | accepted | accepted |
| N | accepted | accepted |
| O (`escaped this` against an ordinary receiver) | rejected | accepted |
| P | accepted | accepted |

Acceptance criteria for the fix: every row above matches the Expected column, and
each rejection has a diagnostic that names the member, says what the member
requires and why the implementation falls short, and suggests the change. For A and
K: "Method 'run' implements 'Task.run', which requires an escaped receiver, but class
'A' is not escaped and the method does not declare one. Declare it
`run(escaped this)` or make the class `escaped`." For J and O: "Method 'plain'
requires an escaped receiver, but 'Mixed.plain' provides an ordinary one, so a
caller may pass a scoped receiver. Remove `escaped this` from the method, or declare
the member `escaped this`." The existing message ("Method 'run()' is not compatible
with interface member 'Task.run()'") names no cause and no remedy. The conversion
behavior that already works must be kept: a class pointer from `new`, or a pointer
to an escaped class, converts to an escaped interface pointer; a non-escaped class
pointer, `stackalloc` instance or local struct is rejected at the conversion; a bound
method reference to a callable newtype with `escaped this` needs an escaped
receiver.

Actual:
Rows B, D and H are rejected although they are valid, rows A, K and O are accepted
although they are invalid, and the rejection message gives no cause and no suggestion.
The bootstrap appears to require the written receiver of the implementation to
equal the written receiver of the member, instead of checking that the implementation
can satisfy the member's receiver lifetime. In particular `escaped interface`
members are treated as having an ordinary receiver.

Known Impact:
A class that is not escaped cannot implement an escaped interface in the valid form
(row B). Beta's own plain classes that implement escaped interfaces (for example the
dependency trace sink, and the allocators in the beta goldens) compile only because
row A is accepted, and will need explicit `escaped this` once the bug is fixed. Beta
cannot port the rule faithfully while the bootstrap accepts and rejects the wrong
forms.

Documentation:
`docs/language/12-interfaces-and-dynamic-dispatch.md` (escaped interfaces) and
`docs/semantics/09-interface-vtables-and-dynamic-dispatch.md`: state that every
member of an escaped interface requires an escaped receiver, that implementations in
a class that is not escaped write `escaped this`, and that escaped classes default to
it. `docs/language/06-functions-methods-and-callables.md` (callable newtype
ascription): state the same receiver requirement for a newtype with `escaped this`.
