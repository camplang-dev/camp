# Outstanding Bugs

- Generic C emission still emits un-erased generic type identifiers such as `T` in some lowered generic functions. This prevents CCompile coverage for `vtableof(T: Interface)` dispatch until erased generic C output is completed.
- Exported struct interface indirect helper layouts can emit raw generic identifiers such as `U` in C headers. These helpers need materialized or erased C layout support before exported struct-interface C ABI headers are fully compilable.
- Direct class interface vtable entries can assign implementation methods whose receiver type is the implementing class pointer to slots whose current C type expects the interface-instance receiver shape. The interface ABI needs either fixup entries or ABI receiver typing that is C-compatible.
