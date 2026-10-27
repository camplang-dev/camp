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

## BUG-404: generic derived class of a base whose constructor takes a size capability emits invalid C when the base argument is a pointer to its parameter

Date/Time: 2026-10-27 13:20 EDT

Summary:
A generic class derives from a generic base constructed with a pointer to the derived class's own type parameter (`Box<T*>`), and the base constructor takes a `sizeof` capability. The derived constructor runs the base constructor with the size of the pointer, but the generated C names a placeholder type that is never declared, so the native build fails. The same program works when the base is constructed with the parameter itself (`Box<T>`), and when the base is constructed with a concrete pointer type (`Box<long*>`). It fails whether the derived constructor declares its own `sizeof(T)` capability or not.

Steps to Reproduce:

1. Compile and run this program:

   ```camp
   extern void __intrinsic_log_i32(int value);
   extern escaped void* malloc(nuint size);
   extern void free(void* ptr);
   virtual class Box<T>
   {
   	nuint stride;
   	Box(sizeof(T)) { this.stride = sizeof(T); }
   	virtual ~Box() { }
   }
   sealed class Pointer<T>: Box<T*>
   {
   	Pointer() { }
   }
   export int main()
   {
   	Pointer<long>* p = new Pointer<long>();
   	__intrinsic_log_i32((int)p.stride);
   	delete p;
   	return 0;
   }
   ```
2. Change the derived constructor to `Pointer(sizeof(T)) { }` and compile again.
3. Change the derived class to the non-generic `sealed class Pointer: Box<long*>` with `Pointer() { }`, and `new Pointer()` in `main`.

Expected:
The programs compile. The first two print `8`, the size of a pointer, like the third.

Actual:
The first two fail in the native build: `error: use of undeclared identifier 'voidPtr'` at `Box_op_initnew((Box *)(this), sizeof(voidPtr *));` in the derived constructor. The third prints `8`.

Known Impact:
A generic class cannot derive from a base constructed with a pointer to its own parameter when that base's constructor takes a capability. Workaround: none found other than not deriving through a pointer-wrapped parameter.
