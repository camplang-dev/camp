# Outstanding Bugs

- **BUG-004:** Calls with an explicit `catch` argument do not currently receive an
  implicit `within` argument from the active `within (...)` context. Calls such
  as `reader.readLine(catch error)` inside a `within (allocator)` block should
  be able to insert the allocator automatically, but today they must spell
  `within allocator, catch error` explicitly.

- **BUG-014:** Lambda return lowering can reuse an outer function's cleanup
  label and generated return storage when the enclosing scope has an active
  `finally` cleanup. A lambda inside a function with `finally delete` can emit
  `goto __cleanupN` to a label that only exists in the outer function.
