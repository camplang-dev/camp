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

Next bug number: BUG-308.

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

## BUG-307: An else after an unbraced if that is itself the body of a loop is reported as detached

Date/Time: 2026-10-18 13:53 EDT

Summary:
When the body of a `while` or `for` is an unbraced `if` and that `if` has an `else`, the `else` is not attached to the `if`. The compiler reports it as an `else` with no `if`.

Steps to Reproduce:

1. Write `while (i < 4) if (i++ == 1) continue; else total += 5;` with the declarations it needs.
2. Compile the file.
3. Repeat with a `for` loop in place of the `while`.

Expected:
The `else` belongs to the nearest `if`, as it does inside a braced body.

Actual:
`(no line,column) error: 'else' must follow an if statement.` is reported with no position, for both loop kinds.

Known Impact:
Dangling-else bodies that C accepts cannot be written without braces, and the diagnostic has no location.
