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

Next bug number: BUG-303.

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

## BUG-300: Explicit parameter names in callable types are ignored by named-argument binding

Date/Time: 2026-10-18 08:50 EDT

Summary:
The language guide says an anonymous callable type keeps explicit parameter names and names only the unnamed ones with `argX`, so `fn bool(int, string text)` has the formal names `arg0` and `text`. A call through a `fn` or `delegate` value rejects the explicit name, and the diagnostic prints the callable type without any names, so the explicit names are dropped when the type is formed.

Steps to Reproduce:

1. Declare `int weigh(int value, int weight) => value * weight;`.
2. In a function, declare `fn int(int, int scale) cb = weigh;`.
3. Call `cb(arg0: 7, scale: 3);` and compile.

Expected:
The call compiles. `scale` names the second parameter and `arg0` names the first, as in the callable surface the guide describes.

Actual:
`error: No parameter named 'scale' exists for this call.` followed by `error: There is no argument given that corresponds to the required parameter 'arg1' of 'fn int(int, int)()'.` The same happens for a `delegate` type and when the callable is a parameter of another function. Only the generated `argX` names bind.

Known Impact:
Named arguments through callable values can use only `argX` names. Any explicit name written in the callable type is accepted by the declaration but has no effect at calls. Calling with the `argX` form works.

## BUG-301: caller(qualifiedname) omits the namespace for a free function in a namespace block

Date/Time: 2026-10-18 09:06 EDT

Summary:
The language semantics say `caller(qualifiedname)` becomes `[Namespace::][Type.]functionName`. A method of a type inside a namespace includes the namespace, but a free function declared in a `namespace` block reports only its own name.

Steps to Reproduce:

1. Declare `string who(string name = caller(qualifiedname)) => name;` at file scope.
2. Declare `namespace Outer { void inFree() { log(who()); } }` where `log` writes a string.
3. Call `Outer::inFree();` from `main` and run.

Expected:
The logged value is `Outer::inFree`, matching `Outer::Inner.method` for a method of a struct declared in the same namespace.

Actual:
The logged value is `inFree`.

Known Impact:
Library code that logs or asserts with the qualified caller name cannot tell free functions of different namespaces apart. Methods are unaffected.

## BUG-302: Malformed source-capture defaults are reported without a source position

Date/Time: 2026-10-18 09:06 EDT

Summary:
A parameter default that misuses `sourceof(...)` or `caller(...)` is rejected, but several of the diagnostics carry no line and column. The compiler prints them at `(no line,column)`, so tools cannot place them and the error cannot be compared by location.

Steps to Reproduce:

1. Declare `string bad(string text, string again = sourceof(text, text)) => text;`.
2. Compile the file. Repeat with `sourceof(text + text)` and with `uint bad(uint line = caller(sourceline, 1)) => line;`.

Expected:
Each error points at the offending default, as `sourceof(missing)` and an unknown `caller(...)` selector already do.

Actual:
`(1,1): (no line,column) error: sourceof(...) requires exactly one positional argument.` `... requires a single unqualified parameter name.` and `caller(...) requires exactly one positional argument.` are reported without a position. Separately, a self anchor `string bad(string text = sourceof(text))` is accepted without an error.

Known Impact:
Editors and tests cannot locate these errors. Wrong arity, a non-name argument and a missing anchor on the first parameter all behave this way.
