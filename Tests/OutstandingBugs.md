# Outstanding Bugs

- **BUG-003:** Direct class interface vtable entries can assign implementation methods whose receiver type is the implementing class pointer to slots whose current C type expects the interface-instance receiver shape. The interface ABI needs either fixup entries or ABI receiver typing that is C-compatible.
- **BUG-004:** Calls with an explicit `catch` argument do not currently receive an
  implicit `within` argument from the active `within (...)` context. Calls such
  as `reader.readLine(catch error)` inside a `within (allocator)` block should
  be able to insert the allocator automatically, but today they must spell
  `within allocator, catch error` explicitly.
- **BUG-005:** Generic member iterator generators cannot currently use the
  containing type's generic parameters in the `iter` return type. A method such
  as `struct iter T iterate()` inside `class List<T>` reports `Unknown type 'T'`
  or is treated as an ordinary `iter T` return instead of a generator.
- **BUG-006:** Generic iterator parameter types can leak generic parameter names
  into emitted C typedefs. Generic callable/delegate parameters have been
  narrowed to erase bare generic parameter names for C, but iterator typedef
  surfaces such as `addEach(iter T iterator)` still need focused coverage.
- **BUG-008:** Generic constructor calls can emit unresolved hidden `sizeof(T)`.
  Calling `new SomeGeneric<T>()` from inside a generic instance method may
  insert the constructor's hidden `sizeof(T)` argument as literal `sizeof(T)` in
  C instead of lowering it to the containing instance's stored `_sizeof_T`
  field. `List<T>` works around this in `copyList` by manually allocating and
  initializing the copy.
- **BUG-009:** Direct function-to-delegate argument expansion can miss trailing
  delegate parameters. A call such as `list.sort(compare)` may fail to insert the
  delegate context when the delegate parameter is the final logical parameter.
  Assigning the function to a delegate local first works around the compile
  error, but still needs a real generated thunk before runtime calls are ABI-safe
  because delegate calls pass a context argument and plain functions do not.
