# Outstanding Bugs

Next bug number: BUG-030.

- **BUG-029:** Some `foreach` forms still reach C emission instead of lowering
  when combined with generic or generator contexts. A generic function iterating
  `foreach (auto item in this)` over `T[] this` with `sizeof(T)`, and an array
  `foreach` inside a generator body, can leave a residual `ForeachStatement`
  that the C emitter rejects. Work around with explicit index loops until the
  generic-array and generator-body foreach lowering paths share the ordinary
  array lowering machinery.

- **BUG-027:** `foreach` over a concrete iterator state that yields an expanded
  params value can omit hidden current-slot component arguments. Priority:
  medium. Complexity: medium. For example, directly enumerating a concrete
  `splitFirstIter` that yields `const char[]` may call `next(&current)` instead
  of `next(&current, &current_length)`, and then attempts to read `.length` from
  the pointer variable. Protocol-shaped `iter` foreach already has coverage for
  expanded slots; this bug is specific to the concrete iterator path.
