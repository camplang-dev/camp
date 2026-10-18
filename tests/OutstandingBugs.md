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

Next bug number: BUG-307.

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

## BUG-303: Assigning a string field of a method call result to an array view emits a call without its receiver

Date/Time: 2026-10-18 09:19 EDT

Summary:
Assigning the `string` field of a call result to an existing `const char[]` variable inside a loop emits C for the call with its receiver replaced by the address of the argument. The generated C does not compile. Declaring the `const char[]` with the same initializer compiles correctly.

Steps to Reproduce:

1. Declare `struct Shard { uint document; string name; }` and a struct `Index` with a `fixed Shard[4] files;` field, a `uint count;` field and `Shard get(uint i) => i < this.count ? this.files[i] : default;`.
2. In a function taking `Index* index`, declare `const char[] found = default;` and loop `for (uint i = 0; i < index.count; i++)` with `if (index.get(i).document == wanted) found = index.get(i).name;`.
3. Compile and run.

Expected:
The program compiles and `found` holds the matching shard's name.

Actual:
The generated C calls the method as `Index_get(&i).name`: the receiver is dropped and the argument's address is passed, so clang reports `too few arguments to function call, expected 2, have 1`.

Known Impact:
A method result's string field cannot be assigned to an array view in this shape. Storing the call result in a local struct first avoids it.

## BUG-304: A for-loop initializer local is scoped to the enclosing block instead of the loop

Date/Time: 2026-10-18 13:53 EDT

Summary:
A local declared in the initializer of a `for` statement is treated as a local of the enclosing block by name resolution. Two sibling loops that each declare the same counter are rejected, and a use of the counter after the loop is not reported by the front end; the generated C then fails because C scopes the variable to the loop.

Steps to Reproduce:

1. Declare `int total = 0;` and write `for (int i = 0; i < 3; i++) total += i;` followed by a second `for (int i = 10; i < 12; i++) total += i;` in one function.
2. Compile the file.
3. Separately, write `for (int index = 0; index < 3; index++) {}` followed by `return index;`.
4. Compile that file.

Expected:
Each loop's counter is visible only inside that loop, as a `foreach` variable is, so the two sibling loops compile and the use after the loop is an unresolved-name error.

Actual:
The sibling loops fail with `Symbol 'i' is already declared in this scope.` The use after the loop passes semantic analysis and the native build fails with `use of undeclared identifier 'index'`.

Known Impact:
The usual C-style pattern of reusing a counter name across loops in one function does not compile, and a counter used past its loop is reported as a native build error instead of a Camp diagnostic.

## BUG-305: break and continue outside any eligible construct are not diagnosed

Date/Time: 2026-10-18 13:53 EDT

Summary:
A `break` that is not inside a loop or switch, and a `continue` that is not inside a loop, are accepted by the front end. The generated C then fails to compile, so the error appears as a native build failure.

Steps to Reproduce:

1. Write `int f() { break; return 0; }` and `int g() { continue; return 0; }`.
2. Write a `continue` whose only enclosing construct is a `switch`.
3. Compile the file.

Expected:
Each statement is reported as a Camp error at the statement, because `break` leaves the nearest loop or switch and `continue` advances the nearest loop.

Actual:
The native build fails with `'break' statement not in loop or switch statement` and `'continue' statement not in loop statement`.

Known Impact:
A misplaced `break` or `continue` has no source location in a Camp diagnostic, so editors and tests cannot point at it.

## BUG-306: A for statement with an omitted initializer binds its clauses one position early

Date/Time: 2026-10-18 13:53 EDT

Summary:
When the initializer clause of a `for` is empty and the condition and increment are present, the condition is taken as the initializer and the increment as the condition. The error or behavior then follows the wrong clause.

Steps to Reproduce:

1. Write `int i = 0; int total = 0; for (; i < 3; i++) total += 10;`.
2. Compile the file.
3. Repeat with `for (; i < 5;) i += 2;`.

Expected:
The loop runs three passes with the condition `i < 3` and the increment `i++`; the second form is a loop with no increment that ends when `i` reaches 5.

Actual:
The first form is rejected with `For condition must be 'bool', not 'int'.` pointing at `i++`. The second form treats the omitted increment as the condition and has no usable test. Loops that omit all clauses, or only the condition, behave correctly.

Known Impact:
A loop that keeps its counter outside the loop cannot be written with an empty initializer.
