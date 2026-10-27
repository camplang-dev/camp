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

Next bug number: BUG-404.

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

## BUG-403: base call through a generic intermediate class that overrides a parameter-typed method fails type inference

Date/Time: 2026-10-27 12:15 EDT

Summary:
A class calls `base.method(...)` where the base is a generic intermediate class that itself overrides a virtual method whose signature mentions a type parameter. The call is rejected with an error that its type arguments cannot be inferred, although the argument has exactly the type the method takes. The same call works when the base is a non-generic class, and when the method is not overridden by the intermediate class.

Steps to Reproduce:

1. Compile this program:

   ```camp
   extern void __intrinsic_log_i32(int value);
   extern escaped void* malloc(nuint size);
   extern void free(void* ptr);
   virtual class Num<T: int> { virtual T same(T v) { return v; } virtual ~Num() { } }
   virtual class MidN<U: int>: Num<U> { override U same(U v) { return v; } }
   sealed class ByteN: MidN<byte> { override int<byte> same(int<byte> v) { return base.same(v); } override ~ByteN() { } }
   export int main() { ByteN* n = new ByteN(); __intrinsic_log_i32(n.same((int<byte>)201)); delete n; return 0; }
   ```
2. A void-represented variant: `Box<T> { virtual int check(T* p) }`, `Mid<U>: Box<U*> { override int check(U** p) }`, `Leaf<V>: Mid<V> { override int check(V** p) { return base.check(p) + 1; } }`.

Expected:
Both programs compile; the first prints `201`.

Actual:
The first program is rejected with `Argument cannot convert 'byte' to 'U' because type arguments for '_same_generic_abi' cannot be inferred; specify them explicitly.` followed by two conversion errors on the result. The second is rejected with `Argument cannot convert 'V**' to 'void<U*>*' because type arguments for '_check_generic_abi' cannot be inferred`.

Known Impact:
A class cannot chain to the overriding implementation of a parameter-typed virtual method in a generic intermediate class through `base`. Workaround: do not call `base.method(...)` for such methods.
