# Outstanding Bugs

- **BUG-003:** Direct class interface vtable entries can assign implementation methods whose receiver type is the implementing class pointer to slots whose current C type expects the interface-instance receiver shape. The interface ABI needs either fixup entries or ABI receiver typing that is C-compatible.
- **BUG-004:** Calls with an explicit `catch` argument do not currently receive an
  implicit `within` argument from the active `within (...)` context. Calls such
  as `reader.readLine(catch error)` inside a `within (allocator)` block should
  be able to insert the allocator automatically, but today they must spell
  `within allocator, catch error` explicitly.
- **BUG-005:** Bound delegate values for expanded-form receivers such as
  `const char[] this` do not currently preserve all receiver components. The
  delegate context can hold the receiver pointer, but not the companion length
  value, so calls like `span.format` cannot be represented correctly without a
  generated context object or adapter thunk.
