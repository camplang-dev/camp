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

Next bug number: BUG-405.

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

## BUG-405: calling a method through a field of a generic class whose type is a construction of a generic interface emits invalid C

Date/Time: 2026-10-27 16:30 EDT

Summary:
A generic class has a field whose type is a pointer to a construction of a generic interface that mentions the class's type parameter (`Get<T>*`), and a method of the class calls an interface method directly through that field (`this.source.get()`). The generated C takes a member of a pointer without dereferencing it correctly, so the native build fails. The same call works when the field is copied to a local first, when the holder class is not generic (`Get<int>*` field), and when the field is a pointer to a non-generic interface in a generic holder.

Steps to Reproduce:

1. Compile and run this program:

   ```camp
   extern void __intrinsic_log_i32(int value);
   extern escaped void* malloc(nuint size);
   extern void free(void* ptr);
   interface Get<V>
   {
   	V* get();
   }
   class IntGet: Get<int>
   {
   	int* get(): Get<int> { return null; }
   }
   class Holder<T>
   {
   	Get<T>* source;
   	T* run() { return this.source.get(); }
   }
   export int main()
   {
   	IntGet* g = new IntGet();
   	Holder<int>* h = new Holder<int>();
   	h.source = g;
   	__intrinsic_log_i32(h.run() == null ? 1 : 0);
   	return 0;
   }
   ```
2. Change `run` to copy the field first: `T* run() { Get<T>* s = this.source; return s.get(); }`, and compile again.
3. Make the holder non-generic: `class Holder { Get<int>* source; int* run() { return this.source.get(); } }` with `new Holder()` in `main`.

Expected:
All three programs compile and print `1`.

Actual:
The first fails in the native build: `error: member reference type 'Get *' (aka 'struct Get *') is a pointer; did you mean to use '->'?` at `return (void *)((*((Get **)(this->source))).get(this->source));` in the generated `run`. The second and third print `1`. A generic holder whose field is a pointer to a non-generic interface also works.

Known Impact:
A generic class cannot call an interface method directly through a field typed with a construction of a generic interface over its parameter. Workaround: copy the field to a local before the call.
