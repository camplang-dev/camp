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

Next bug number: BUG-388.

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

## BUG-385: Virtual method dispatch call drops an array parameter's length when the method also returns an array

Date/Time: 2026-10-26 20:15 EDT

Summary:
A virtual method that takes an ordinary array parameter and also returns an ordinary array generates a dispatch body that forwards too few arguments to the implementation. The array parameter's length is omitted from the forwarded call, so the generated C does not compile. The same shapes compile and run correctly when the method has only the array parameter or only the array result.

Steps to Reproduce:

1. Declare a `virtual` class with a virtual method `virtual int[] values(int[] input) { return input; }`.
2. Derive a sealed class from it that does not override the method.
3. Construct the derived class with `stackalloc` through a pointer to the base, call `values` on it, and build the program with `campc build` (or `campc run`).

Expected:
The program builds. The dispatch body for `values` forwards the receiver, the input array pointer and its length, and the result length address to the implementation, as it does for a method with only an array parameter.

Actual:
The native compile of the generated C fails: `error: too few arguments to function call, expected 4, have 3`, at the dispatch body's call through the class vtable (`this->_vt->values(this, input, result_length)`). The input array's length argument is missing.

Known Impact:
Any virtual or abstract method with both an array parameter and an array result cannot be compiled. Workaround: return the array through an `out` parameter or take a fixed-size or pointer parameter instead.

## BUG-386: `base.method()` is rejected when the base method is not virtual

Date/Time: 2026-10-26 22:12 EDT

Summary:
Inside a derived class method, `base.name(...)` can call only a base method that is `virtual`, `abstract` or `override`. When the base method is an ordinary non-virtual method, the call is rejected with "Base implementation 'name' could not be found." plus a cascading "Symbol 'base' could not be found." A non-virtual base method has exactly one implementation, so there is nothing ambiguous about the call, and `base.name(...)` should call it the same way it calls a virtual implementation nonvirtually.

Steps to Reproduce:

1. Declare `virtual class Root { Root() {} int value() { return 3; } }`, where `value` has no `virtual` modifier.
2. Declare `sealed class Leaf: Root { Leaf() {} int twice() { return base.value() * 2; } }`.
3. Construct a `Leaf` with `stackalloc`, call `twice`, and build the program with `campc run`.

Expected:
The program builds and `twice` returns 6. `base.value()` calls the inherited non-virtual method.

Actual:
```
error: Base implementation 'value' could not be found.
error: Symbol 'base' could not be found.
```
The same program builds and runs when `value` is declared `virtual` in `Root`.

Known Impact:
A derived method cannot call a non-virtual base method through `base`. Workaround: call the method unqualified (`value()`) or through `this`, which finds the inherited method.

## BUG-387: A constructor with statements and no `base(...)` call emits invalid C when the base constructor takes arguments

Date/Time: 2026-10-27 09:05 EDT

Summary:
When a base class constructor takes a required argument, a derived constructor that does not start with `base(...)` cannot satisfy it. If the derived constructor body is empty the compiler reports an error, but if the body contains any statement the compiler reports nothing and emits a call to the base constructor with too few arguments. The native compile of the generated C then fails.

Steps to Reproduce:

1. Declare `class Root { Root(int x) { } }`.
2. Declare `sealed class E: Root { E() { __intrinsic_log_i32(1); } }`, with the usual `__intrinsic_log_i32`, `malloc` and `free` externs.
3. Add `export int main() { return 0; }` and build the program with `campc run`.

Expected:
The compiler reports a Camp diagnostic on the derived constructor saying that the base class constructor has arguments, so the derived constructor must start with a `base(...)` call. The empty-body form `E() { }` is already diagnosed.

Actual:
No Camp diagnostic is produced. The native compile fails: `error: too few arguments to function call, expected 2, have 1` at `Root_op_initnew((Root *)(this));` in the generated C.

Known Impact:
Any derived constructor with a body that omits a required `base(...)` call surfaces as a C compiler error. Workaround: start the constructor with `base(...)`.
