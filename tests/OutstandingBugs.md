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

Next bug number: BUG-365.

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
