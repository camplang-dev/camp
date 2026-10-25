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

Next bug number: BUG-368.

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

## BUG-363: A constructor may declare a thrown parameter

Date/Time: 2026-10-24 16:52 EDT

Summary:
A constructor cannot be a throwing callable: construction is not allowed to report failure through a `thrown`
parameter. The compiler accepts a `thrown` parameter on a class constructor, a struct constructor and an
interface constructor without any diagnostic. The restriction was previously only an API convention, kept in
place because the compiler did not reject the form itself.

Steps to Reproduce:

1. Compile the following program:

   ```camp
   class Item { Item(thrown int error) {} }
   struct Pair { int x; Pair(thrown int error) { this.x = 0; } }
   export int main() { return 0; }
   ```

2. Repeat with `export class Item { export Item(thrown int error) {} }`, and with an interface constructor
   such as `IMade(thrown int error, within allocator);`.

Expected:
Every constructor that declares a `thrown` parameter is rejected with a diagnostic stating that a constructor
cannot declare a thrown error parameter.

Actual:
All of the programs compile without a diagnostic, and `new Item(catch int error)` is accepted at a call.

Known Impact:
Code can declare and call constructors that throw, a form the language does not allow. Programs that do so
today will start to fail when this is fixed.

## BUG-364: Conditional expression with an unavailable arm fails in C emit

Date/Time: 2026-10-24 16:52 EDT

Summary:
When a conditional expression is guarded by `configured(FLAG)` and one arm calls a function whose `requires`
condition is not satisfied in the current configuration, semantic analysis accepts the expression, because the
configured condition proves the other arm. The C emitter then still emits the unavailable arm: it folds the
condition into the literal `true` or `false` but keeps both arms, so the generated C calls a function that was
never emitted and the native build fails. The same program written with `if` and `else` compiles.

Steps to Reproduce:

1. Compile the following program with `-d SELECT_FALSE=false`:

   ```camp
   extern void __intrinsic_log_i32(int value);
   requires (SELECT_FALSE) int selectedOnly() { return 11; }
   requires (!SELECT_FALSE) int fallbackOnly() { return 7; }
   export int main()
   {
   	__intrinsic_log_i32(configured(SELECT_FALSE) ? selectedOnly() : fallbackOnly());
   	return 0;
   }
   ```

2. Compile the following program with `-d DECLARED_TRUE=true`, where the unavailable arm is a plain function
   and the selected arm is a generic one:

   ```camp
   extern void __intrinsic_log_i32(int value);
   requires (DECLARED_TRUE)
   {
   	T selected<T: int>(T value) { return value; }
   }
   requires (!DECLARED_TRUE)
   {
   	int fallback() { return 41; }
   }
   export int main()
   {
   	int result = configured(DECLARED_TRUE) ? selected<int>(43) : fallback();
   	__intrinsic_log_i32(result);
   	return 0;
   }
   ```

Expected:
Both programs build and run. The arm that the configured condition rules out is not emitted.

Actual:
Semantic analysis passes and the native build fails:

```text
error: call to undeclared function 'selectedOnly'; ISO C99 and later do not support implicit function declarations
   __intrinsic_log_i32((false ? selectedOnly() : fallbackOnly()));
```

The second program fails the same way on the call to `fallback`.

Known Impact:
A conditional expression cannot select between declarations guarded by opposite requirements, although the
availability rules accept it. Workaround: use an `if` statement with an `else` branch.

## BUG-365: A reserved type name hides the other reserved-name errors in the same file

Date/Time: 2026-10-24 16:52 EDT

Summary:
A reserved word is rejected as a declared name in every position. When one declaration in a file uses a
reserved word as a type name, the compiler reports only that error and omits the reserved-name errors for
the other declarations in the file, which are reported once the type name is fixed. The remaining names are
therefore found one compile at a time.

Steps to Reproduce:

1. Compile the following program:

   ```camp
   class break {}
   int default() { return 0; }
   export int main() { return 0; }
   ```

2. Compile it again with only the function declared, and with a reserved local name inside `main` in place
   of the function (`int continue = 1;`).

Expected:
The program reports an error for each reserved name: the type name `break` and the function name `default`.

Actual:
Only the type name is reported:

```text
error: Type name 'break' is reserved.
```

With only the function (`int default() { return 0; }`) the compiler reports
`Function name 'default' is reserved.`, and with the type name and a local `int continue = 1;` in `main` it
reports only the type name, so the local name error appears only after the type is renamed.

Known Impact:
A file with several reserved names needs one compile per category of name to find them all.

## BUG-366: A failed conversion to an escaped pointer type is reported a second time at its use

Date/Time: 2026-10-24 16:53 EDT

Summary:
Converting a pointer that is not escaped to a pointer to an escaped type (an escaped interface or class) is
rejected because the source must be escaped. When the converted value is then passed as an argument or stored
in escaped storage, the compiler reports a second error on the same range for the same fault, that the
argument cannot satisfy the parameter's escaped lifetime or that the assignment cannot store a non-escaped
value in escaped storage. The conversion error already names the cause, and the converted pointer is escaped
by its type, so the second error repeats it and gives the author nothing more to fix.

Steps to Reproduce:

1. Compile the following program:

   ```camp
   escaped interface Task { void run(); }
   class PlainTask: Task { int n; void run(escaped this): Task { } }
   void schedule(Task* task) { }
   Task* kept;
   void argument(PlainTask* task) { schedule(task); }
   void store(PlainTask* task) { kept = task; }
   export int main() { return 0; }
   ```

Expected:
One error for each of the two uses, the conversion error:

```text
error: Conversion from 'PlainTask*' to 'Task*', a pointer to an escaped type, requires escaped source storage.
```

Actual:
Each use reports the conversion error and a second error on the same range:

```text
(5,43): error: Conversion from 'PlainTask*' to 'Task*', a pointer to an escaped type, requires escaped source storage.
(5,43): error: Argument cannot satisfy parameter lifetime 'escaped'.
(6,38): error: Conversion from 'PlainTask*' to 'Task*', a pointer to an escaped type, requires escaped source storage.
(6,38): error: Assignment cannot store a non-escaped pointer-bearing value in escaped storage.
```

Known Impact:
Two errors are reported for a single mistake, and one of them points the author at the use instead of the
conversion. A direct escaped-lifetime violation that involves no failed conversion, such as passing a
non-escaped pointer for an `escaped T*` parameter, must still report the lifetime error.

## BUG-367: A requires-gated type with an explicit constructor or destructor is rejected

Date/Time: 2026-10-24 20:32 EDT

Summary:
A type declaration may carry a `requires` condition, and the members of a gated type are available exactly
where the type is. When such a class or struct declares an explicit constructor or destructor, the compiler
reports "requires is not valid on constructors or destructors" at the type's `requires` clause. That
diagnostic is meant for a `requires` written on the constructor or destructor itself; here the condition is
written once, on the type, and the constructor and destructor only inherit it. A gated type with no explicit
constructor or destructor compiles.

Steps to Reproduce:

1. Compile the following program with `-d FEATURE=true`:

   ```camp
   requires (FEATURE) class Value
   {
   	int value;
   	Value(int initial) { this.value = initial; }
   }
   export int main() { return 0; }
   ```

2. Repeat with `requires (FEATURE) struct Value { int value; Value(int initial) { this.value = initial; } }`,
   with `~Value() {}` in place of the constructor, and with `-d FEATURE=false`.

Expected:
Every program compiles. The constructor and destructor are available where the type is.

Actual:
Each program fails at the `requires` clause of the type:

```text
error: requires is not valid on constructors or destructors.
```

The same class without the constructor (`requires (FEATURE) class Value { int value; }`) compiles.

Known Impact:
A requirement-gated type cannot declare a constructor or destructor, so gated types cannot be constructed
with arguments or have custom cleanup. Workaround: gate the file with a file-wide `requires (CONDITION);`
or keep the constructor out of the gated type.
