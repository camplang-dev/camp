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

## BUG-296: String variable passed to a `const char[]` parameter that has a trailing default misaligns the call

Date/Time: 2026-10-15 22:05 EDT

Summary:
A `string` variable converts to a `const char[]` parameter by supplying the
pointer and the string length as two components. When the callee also declares a
trailing parameter with a default value, the call lowers with the length
component missing: the next explicit argument is placed in the length slot and
the default is appended instead. The call compiles without a diagnostic and
silently computes with the wrong length and the wrong later argument. A string
literal, a `const char[]` local, and a callee without a default are unaffected.

Steps to Reproduce:

1. Compile and run:
   ```camp
   extern void __intrinsic_log_i32(int value);
   int measure(const char[] text, int bonus = 1) => (int)text.length + bonus;
   export int main()
   {
       string label = "abc";
       __intrinsic_log_i32(measure(label, 5));
       return 0;
   }
   ```
2. Inspect the generated C call, or call a method whose later parameter is an
   `enum` with a default; the native build can instead fail with "too many
   arguments to function call".

Expected:
The program prints 8: the length of `label` (3) plus the explicit `bonus` (5).

Actual:
It prints 6. The generated call is `measure(label, 5, 1)`, so `text.length` is 5
and `bonus` takes its default. With a method taking `(int owner, const char[] name, Kind kind, Prim rep = Prim.N)` the call is emitted with one argument too many and the C
compiler rejects it.

Known Impact:
Any call that passes a `string` value to a `const char[]` parameter followed by
an explicitly supplied argument and a defaulted parameter. Workaround: remove the
default, pass an explicit `const char[]` view (for example a local declared as
`const char[]`), or supply every argument after the string by name.

## BUG-297: Written capability evidence names are matched to the callee's slot names, not to their operands

Date/Time: 2026-10-16 04:50 EDT

Summary:
A call may supply a capability slot with a written evidence name such as
`vtableof_T_I`, which the language defines as resolving to the caller's own
parameter before it is compared with the slot's substituted operand. When a
generic caller forwards its evidence to a generic callee with the type
arguments swapped, a name that happens to equal one of the callee's own slot
names is compared against that callee slot instead of the caller's evidence, so
the call is rejected with an operand mismatch. The same call written with
`vtableof(U: I)` expressions, or with caller parameters named differently from
the callee's, is accepted and returns the correct result.

Steps to Reproduce:

1. Compile and run:
   ```camp
   extern void __intrinsic_log_i32(int value);
   extern escaped void* malloc(nuint size);
   extern void free(void* ptr);
   interface Counter { int read(); }
   struct Cell: Counter
   {
       int value;
       int read(): Counter { return this.value; }
   }
   int pair<T, U>(T* first, U* second, vtableof(T: Counter), vtableof(U: Counter))
   {
       return first.read() * 100 + second.read();
   }
   int swapped<T, U>(T* a, U* b, vtableof(T: Counter), vtableof(U: Counter))
   {
       return pair<U, T>(b, a, vtableof_U_Counter, vtableof_T_Counter);
   }
   export int main()
   {
       Cell x = default;
       x.value = 3;
       Cell y = default;
       y.value = 5;
       __intrinsic_log_i32(swapped<Cell, Cell>(&x, &y));
       return 0;
   }
   ```
2. Replace the two evidence names with `vtableof(U: Counter), vtableof(T: Counter)`,
   or rename the caller's parameters to `A` and `B`, and compile again.

Expected:
Both forms compile and print 503: the caller's `U` evidence fills the callee's
first slot and its `T` evidence fills the second, whatever the parameters are
named.

Actual:
The named form is rejected: `Argument cannot convert 'vtableof(U: Counter)' to
'vtableof(T: Counter)'` and `Capability slot 'vtableof_U_Counter' requires
evidence for exact operand 'T'`. The expression form and the renamed form print
503.

Known Impact:
Forwarding evidence by its generated name through a call whose type arguments
are swapped, when the caller and callee name their parameters alike. Workaround:
write the evidence as `sizeof(U)`, `typenameof(U)` or `vtableof(U: I)`
expressions, omit the evidence so it is supplied from the caller, or use
different parameter names.

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
