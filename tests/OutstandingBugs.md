# Outstanding Bugs

Next bug number: BUG-063.

## BUG-062: Cross-file `new` of a public derived virtual class emits an undeclared vtable reference

### Summary

When a public derived virtual class is declared in one source file and allocated
with `new` from another source file in the same project, C emission can generate
a reference to the derived class vtable storage without making that vtable
declaration visible to the consuming translation unit.

This is a C-emitter/generated-helper visibility problem. Source visibility
allows the public type to be used across source files in the same artifact, and
construction lowering needs access to the generated virtual class helper state
required to initialize the object.

### General Repro

1. Create a project with at least two source files.
2. In one file, declare a `public virtual class Base`.
3. In the same non-main file, declare a `public virtual class Derived: Base`.
4. In another file, allocate the derived type with `new Derived()`.
5. Build the project to C/native.

Minimal repro, split across files:

```camp
// main.camp
export int main(string[] args)
{
	auto value = new Derived();
	return 0;
}
```

```camp
// helper.camp
public virtual class Base
{
	public virtual ~Base()
	{
	}
}

public virtual class Derived: Base
{
	override ~Derived()
	{
	}
}
```

The current concrete repro is available at:

```text
/Users/andrew/Projects/camplang/playground/tmp/starter
```

Run:

```bash
./bin/campc build /Users/andrew/Projects/camplang/playground/tmp/starter/starter.campbuild
```

### Current Behavior

Reproduced on macOS/clang. Native compilation fails:

```text
error: use of undeclared identifier '_Control__vt'
```

The generated consuming file contains:

```c
Control *ctl = (Control *)(malloc(sizeof(Control)));
if ((ctl != NULL))
{
	*ctl = (Control){0};
	ctl->_vt = &_Control__vt.Component;
	Control_op_initnew(ctl);
}
```

The generated defining file contains file-local vtable storage:

```c
static _Control _Control__vt;
static _Control _Control__vt = { .Component = { .op_delete = Control__op_delete } };
```

The private header shared by the generated files declares the `_Control` layout
type, but it does not declare `_Control__vt`. Because `_Control__vt` is also
emitted as `static` in the defining C file, another generated translation unit
cannot legally reference it.

### Expected Behavior

The compiler should generate C that compiles when a public virtual class is used
from another source file in the same artifact.

One valid lowering strategy is to make the generated virtual class vtable
storage for cross-file-visible virtual classes have matching cross-translation
unit declarations/definitions in the generated private header and source file.
Another valid strategy is to lower cross-file construction through an emitted
create/init helper that performs the vtable assignment inside the defining
translation unit. In either case, the generated C must not reference an
undeclared or file-local vtable object from another translation unit.

Relevant semantics:

- `public` declarations are artifact-internal and usable across statically
  linked source files in the same artifact.
- C emission includes generated helper declarations needed by lowered ABI.
- `new` of a virtual class must assign generated virtual/interface fields before
  invoking the constructor/init-new helper.
