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

Next bug number: BUG-299.

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

## BUG-298: A call through a callable value whose parameter is a pointer-payload `nint` carrier fails in the C compiler

Date/Time: 2026-10-16 21:10 EDT

Summary:
A generic function with an `nint` representation parameter can be called directly with a data pointer: the argument and result convert between the pointer and the `nint<pointer>` carrier correctly. When the same function is ascribed to a callable newtype constructed over the pointer type and the call goes through the callable value, the generated C passes the `int*` argument where the `intptr_t` carrier is expected and assigns the carrier result to an `int*`. Semantic analysis accepts the program and the native compile then stops with incompatible pointer/integer conversion errors.

Steps to Reproduce:

1. Compile and run this program:
   ```camp
   extern void __intrinsic_log_i32(int value);
   newtype fn U WideId<U: nint>(U value);
   U pass<U: nint>(U value): WideId<U> => value;
   export int main()
   {
       int value = 77;
       int* pointer = &value;
       WideId<int*> f = pass<int*>;
       int* result = f(pointer);
       __intrinsic_log_i32(*result);
       return 0;
   }
   ```
2. Replace the callable-value call with the direct call `int* result = pass<int*>(pointer);` and compile again.

Expected:
Both forms compile and print 77.

Actual:
The callable-value form is rejected by the C compiler: `incompatible pointer to integer conversion passing 'int *' to parameter of type 'intptr_t'` and `incompatible integer to pointer conversion initializing 'int *' with an expression of type 'intptr_t'`. The direct call prints 77.

Known Impact:
Callable values whose parameters or results are pointer-payload `nint` carriers. Workaround: call the generic function directly, or carry an integer payload instead of a pointer.

## BUG-299: A consumer that infers a reference to an exported function ascribed to a producer-private callable newtype fails in the C compiler

Date/Time: 2026-10-17 09:40 EDT

Summary:
A producer exports a function whose ascription names a callable newtype the producer does not export. A separate consumer can call the function directly. When the consumer binds a reference to it with `auto` and calls through the reference, semantic analysis accepts the program and the generated C declares the local with the producer's private newtype name, which the consumer's translation unit never declares. The same program with the newtype exported compiles and runs.

Steps to Reproduce:

1. Producer project, one source file:
   ```camp
   newtype fn bool HiddenCheck<U: int>(U value);
   export bool zeroOf<V: int>(V value): HiddenCheck<V> => value == default;
   ```
2. Consumer, referencing the producer as a static project reference:
   ```camp
   extern void __intrinsic_log_i32(int value);
   export int main()
   {
       auto zero = zeroOf<byte>;
       byte none = 0;
       __intrinsic_log_i32(zero(none) ? 1 : 0);
       return 0;
   }
   ```
3. Repeat with `export newtype fn bool OpenCheck<U: int>(U value);` in the producer.

Expected:
Both builds print 1, with the private newtype invisible to the consumer.

Actual:
The private form stops in the C compiler with `use of undeclared identifier 'HiddenCheck'` at `HiddenCheck zero = (HiddenCheck)zeroOf;` and `call to undeclared function 'zero'`. The exported form prints 1.

Known Impact:
A consumer inferring a reference to an exported function whose ascribed callable newtype is private to the producer. Workaround: export the newtype, or call the function directly instead of binding a reference.
