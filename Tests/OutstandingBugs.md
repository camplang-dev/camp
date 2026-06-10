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
- **BUG-006:** Generic callable and iterator parameter types can leak generic
  parameter names into emitted C typedefs. Methods such as
  `addEach(iter T iterator)` or `sort(delegate int(T, T) comparer)` in
  `class List<T>` can emit private-header typedefs containing raw `T` instead
  of erasing the callable slot types for C.
