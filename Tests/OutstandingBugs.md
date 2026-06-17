# Outstanding Bugs

Next bug number: BUG-018.

- **BUG-004:** Calls with an explicit `catch` argument do not currently receive an
  implicit `within` argument from the active `within (...)` context. Calls such
  as `reader.readLine(catch error)` inside a `within (allocator)` block should
  be able to insert the allocator automatically, but today they must spell
  `within allocator, catch error` explicitly.

- **BUG-017:** Iterator protocol adapters and iterator `foreach` lowering do not
  fully support expanded yielded slot types such as `iter byte[]`. A generator
  can lower a `next(byte** current, nuint* current_length)` shape, but the
  generated `op_iter` adapter and `foreach` current-slot materialization still
  need first-class expanded-slot forwarding.
