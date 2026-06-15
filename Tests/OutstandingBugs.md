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
- **BUG-008:** Default argument insertion can append a default for a later
  parameter even when the caller supplied that argument explicitly in some
  generic/delegate call shapes. `List<T>.binarySearch` avoids optional
  `startAt/count` parameters until this is fixed.
- **BUG-009:** Taking the address of an erased generic array element can emit
  C like `&items[index]` against a `void*` backing pointer instead of byte
  offsetting by `index * sizeof(T)`. `List<T>.getAddressOf` currently performs
  the byte arithmetic explicitly.
