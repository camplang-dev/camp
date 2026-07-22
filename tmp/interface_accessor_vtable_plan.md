# Four-Stage Plan: Interface Accessors, `vtableof`, And Extern Class Interfaces

## Summary

Change class-to-interface conversion so it uses a generated callable accessor method, while preserving exported direct vtable symbols for `vtableof(Type: Interface)`. Classes get generated `getInterfaceName()` accessors; structs remain internal-only for exported interface implementation. Extern classes may declare implemented interfaces, meaning they provide/import the accessor and direct vtable symbol contract without Camp generating fields or thunks.

## Stage 1: Generate Class Interface Accessors

- For each class interface implementation, generate a real method named `get<InterfaceName>()` returning `InterfaceName*`.
- The method visibility follows the implemented interface visibility, but it is only emitted in public Camp API headers when both the class and interface are exported.
- For ordinary non-extern classes, the method body returns the existing object interface pointer, equivalent to `&this._vt_InterfaceName`.
- Treat the method as compiler-generated but real:
  - participates in duplicate symbol checks;
  - supports direct call `obj.getIFoo()`;
  - supports property syntax `obj.IFoo`;
  - appears in metadata/API surfaces like other generated exported methods where appropriate.
- Preserve existing direct interface vtable generation for `vtableof`; do not merge the accessor vtable with the direct vtable.

Completion criteria:
- `obj.getIFoo()` and `obj.IFoo` compile for ordinary classes.
- Generated accessor collides cleanly with a user-authored `getIFoo`.
- Camp API header for exported class + exported interface includes `export extern IFoo* getIFoo();`.

## Stage 2: Lower Class-To-Interface Conversion Through Accessors

- Replace class-to-interface conversion lowering from direct field address access:
  - old: `(IFoo**)&instance->_vt_IFoo`
  - new: `Type_getIFoo(instance)`
- Apply this to:
  - implicit assignment/argument/return conversions;
  - explicit casts `(IFoo*)instance`;
  - property access `instance.IFoo`;
  - direct accessor calls;
  - derived class conversions through the base implementation.
- Keep `vtableof(Type: IFoo)` lowering to the direct exported vtable symbol `Type_IFoo`.
- For derived classes:
  - forbid interface re-implementation if a base already implements the interface, if not already enforced;
  - convert derived instances through the inherited base accessor;
  - do not generate a duplicate accessor on the derived class for inherited implementations.

Completion criteria:
- `IFoo* i = obj;`, `(IFoo*)obj`, `obj.IFoo`, and `obj.getIFoo()` produce equivalent behavior.
- `vtableof(Widget: IFoo)` still lowers to the direct vtable symbol, not the accessor.
- Derived class instances convert using the base implementation.
- No generated C reaches into `_vt_IFoo` from conversion code except inside the accessor body.

## Stage 3: Extern Classes And API Header Rules

- Remove the hard ban on extern classes declaring implemented interfaces.
- For `extern class NativeWidget : IFoo`:
  - do not generate vtable fields;
  - do not generate implementation thunks;
  - do not require interface methods to be declared in the extern class body;
  - generate/import the `getIFoo()` method surface;
  - reject an explicit user declaration of `getIFoo()` when `IFoo` is listed in the extern class header.
- Allow `vtableof(NativeWidget: IFoo)` only when the extern class declares `: IFoo`; lower it to the assumed exported direct vtable symbol.
- Public C API headers:
  - continue exposing direct class/interface vtable symbols used by `vtableof`;
  - do not expose object-layout private vtable fields.
- Struct interface implementations:
  - keep private vtable generation;
  - do not export `getInterfaceName()`;
  - do not list implemented interfaces in public Camp API headers;
  - do not expose struct interface vtable symbols publicly.

Completion criteria:
- Extern class interface implementation compiles and imports through a library API header.
- Explicit accessor declaration on an extern implementing class is diagnosed.
- Public C API contains direct `Type_IFoo` vtable symbols for exported class/interface pairs.
- Struct implementations remain absent from public Camp API headers and public C vtable exports.

## Stage 4: Integration Tests, Regression Coverage, And Deferred Bug Note

- Add a command-line/library integration test with two modules:
  - library exports interfaces, ordinary classes, extern classes, structs, base/derived classes, and interface implementations;
  - consumer imports the generated Camp API and uses implicit conversion, explicit casts, property syntax, direct accessor calls, and `vtableof`;
  - verify native compile/link/run, not just header text.
- Add focused golden tests for:
  - generated accessor in Camp API;
  - direct vtable symbol in C API;
  - private-header-only object vtable details;
  - struct interface implementation omitted from public API;
  - derived conversion via base implementation.
- Add diagnostics tests for:
  - user-declared duplicate accessor;
  - extern class explicit accessor conflict;
  - extern `vtableof` without declared interface implementation;
  - derived class interface re-implementation, if not already covered.
- Add `BUG-031` to `tests/OutstandingBugs.md` and advance next bug number to `BUG-032`:
  - derived classes should be forbidden from declaring methods that would newly satisfy optional interface methods from an interface implemented by a base class.
  - This is deferred intentionally.
- Run:
  - targeted API/CEmit/CCompile/CommandLine tests during implementation;
  - full non-skipped suite before each commit.

## Assumptions

- Existing compiler generation already maintains separate direct vtable and fixup/object vtable concepts; implementation should reuse those instead of inventing a new vtable model.
- Interface accessor naming is exactly `get<InterfaceName>`, and property syntax is therefore `<InterfaceName>`.
- Accessor visibility follows interface visibility, with public API emission only when the containing class is exported.
- Struct exported interface implementation remains intentionally unsupported for now.
- Optional-method-in-derived-class validation is deferred as `BUG-031`.
