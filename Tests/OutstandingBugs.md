# Outstanding Bugs

- **BUG-003:** Direct class interface vtable entries can assign implementation methods whose receiver type is the implementing class pointer to slots whose current C type expects the interface-instance receiver shape. The interface ABI needs either fixup entries or ABI receiver typing that is C-compatible.
- **BUG-004:** Calls with an explicit `catch` argument do not currently receive an
  implicit `within` argument from the active `within (...)` context. Calls such
  as `reader.readLine(catch error)` inside a `within (allocator)` block should
  be able to insert the allocator automatically, but today they must spell
  `within allocator, catch error` explicitly.
- **BUG-009:** Direct function-to-delegate argument expansion can miss trailing
  delegate parameters. A call such as `list.sort(compare)` may fail to insert the
  delegate context when the delegate parameter is the final logical parameter.
  Assigning the function to a delegate local first works around the compile
  error, but still needs a real generated thunk before runtime calls are ABI-safe
  because delegate calls pass a context argument and plain functions do not.
