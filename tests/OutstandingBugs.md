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

Next bug number: BUG-324.

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

## BUG-320: A `while` condition that propagates a thrown error clears the error

Date/Time: 2026-10-20 01:45 EDT

Summary:
A call in a `while` condition whose `thrown` slot is forwarded to the caller's own `thrown` slot leaves the loop and returns, but the generated exit writes the default error first. The caller sees success even though the condition's call failed. The same call in an `if` condition, a declaration, or a `return` expression propagates the error unchanged.

Steps to Reproduce:

1. Declare `enum Fault { OK = 0, BAD }`, `int fail(thrown Fault error) { throw Fault.BAD; }`, and `int loop(thrown Fault error) { int count = 0; while (fail() > 0) count += 1; return count; }`.
2. Call `loop(catch fault)` from `main` with `Fault fault = default;` and log `(int)fault`.

Expected:
`1`: `fail()` threw `Fault.BAD`, and `loop` passes that error through its own `thrown` slot untouched.

Actual:
`0`. The generated loop body ends its error check with `(*error) = 0; return 0;`, which erases the error that `fail` stored.

Known Impact:
Errors raised while evaluating a loop condition are silently dropped. Workaround: call the throwing function before the loop or inside its body and test the result.

## BUG-321: A catch variable used after its handler is not diagnosed

Date/Time: 2026-10-20 14:10 EDT

Summary:
The variable of a `catch (T name)` clause is scoped to the catch body, but a use of that name after the handler passes semantic analysis. The failure only appears when the generated C is compiled, as an undeclared identifier, so the user sees a native build error instead of a Camp diagnostic.

Steps to Reproduce:

1. Declare `enum Fault { OK = 0, EMPTY }`, `int checked(int value, thrown Fault error) { if (value == 0) throw Fault.EMPTY; return value; }`, and a function whose body is `try { checked(1); } catch (Fault fault) { } log((int)fault);`.
2. Build an executable that calls it.

Expected:
A Camp diagnostic that `fault` is not declared at the use after the handler, as for any other local used outside its scope.

Actual:
`Native build command failed ... error: use of undeclared identifier 'fault'`. No Camp diagnostic is reported.

Known Impact:
Misuse of a catch variable is reported by the C compiler rather than the Camp compiler. No workaround is needed beyond not using the variable outside its handler.

## BUG-322: Calls of different thrown types in one expression share the first call's error destination

Date/Time: 2026-10-20 14:25 EDT

Summary:
When one expression contains several calls that can throw and their `thrown` types differ, lowering routes every call's error through the destination chosen for the first call. Inside a `try` whose only handler accepts the first call's type, the second call's error is stored through a pointer of the wrong type and is caught by that handler, although the handler does not accept it.

Steps to Reproduce:

1. Declare `enum Low { OK = 0, EMPTY }`, `enum High { OK = 0, BAD }`, `int low(int v, thrown Low e)`, `int high(int v, thrown High e)` where `high` throws `High.BAD`, and `int f(thrown High outward) { try { return low(3) + high(8); } catch (Low inner) { return -5; } }`.
2. Call `f(catch fault)` with `High fault = default;` and log the result and `(int)fault`.

Expected:
`high` throws `High.BAD`, which the `Low` handler does not accept, so `f` leaves through its own `thrown` slot: `fault` is `High.BAD` and the result is the default `0`. Writing the two calls as separate statements gives that result.

Actual:
`-5` with `fault` unchanged. The generated C passes `&inner` (a `Low`) as the `thrown High *` argument of `high` and branches to the `Low` handler.

Known Impact:
An error of an unrelated type is caught by the wrong handler, or lost, and the C compiler is handed a pointer of the wrong type. Workaround: call each throwing function in its own statement.

## BUG-323: An error transferring to an outer catch skips the finally of a try nested inside the protected body

Date/Time: 2026-10-20 15:05 EDT

Summary:
When a `throw`, or a call that fails, inside a `try`/`finally` that is itself nested in the body of an outer `try`/`catch` transfers control to the outer handler, the inner `finally` body does not run. The inner `try` is exited by the transfer, and the language reference says a `finally` block runs when control leaves its `try` by a caught error. The same `finally` runs on normal completion, `return`, `break`, `continue` and a propagated error.

Steps to Reproduce:

1. Declare `enum Fault { OK = 0, RANGE }`.
2. Write `void f() { try { try { throw Fault.RANGE; } finally { log(51); } } catch (Fault fault) { log(62); } }` and call it.

Expected:
`51` then `62`: the inner `finally` runs as the throw leaves its `try`, then the handler runs.

Actual:
Only `62`. The generated transfer to the handler runs no cleanup for the inner `try`.

Known Impact:
Cleanup registered by a nested `try`/`finally` is lost when an error is caught by an enclosing handler, so resources it would release stay held. Workaround: put the `catch` on the same `try` as the `finally`, or catch the error inside the inner `try`.
