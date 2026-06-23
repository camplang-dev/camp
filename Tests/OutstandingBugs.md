# Outstanding Bugs

Next bug number: BUG-030.

- **BUG-029:** Some `foreach` forms still reach C emission instead of lowering
  when combined with generic or generator contexts. A generic function iterating
  `foreach (auto item in this)` over `T[] this` with `sizeof(T)`, and an array
  `foreach` inside a generator body, can leave a residual `ForeachStatement`
  that the C emitter rejects. Work around with explicit index loops until the
  generic-array and generator-body foreach lowering paths share the ordinary
  array lowering machinery.

- **BUG-028:** Generic lifetime substitution does not yet diagnose scoped
  delegate context storage through erased `T`. Priority: medium-high.
  Complexity: medium. Example:
  `delegate bool(in int) globalPredicate; void choose<T: copyable>(T value, out T result) { result = value; } void bad() { delegate bool(in int) predicate = value => value > 0; choose<delegate bool(in int)>(predicate, out globalPredicate); }`
  should report that a scoped delegate/context value cannot be stored in escaped
  global storage after substituting `T`, analogous to the current
  `const char[]` out-parameter diagnostic. This likely needs delegate context
  lifetime facts to survive local materialization and generic out propagation.

- **BUG-027:** `foreach` over a concrete iterator state that yields an expanded
  params value can omit hidden current-slot component arguments. Priority:
  medium. Complexity: medium. For example, directly enumerating a concrete
  `splitFirstIter` that yields `const char[]` may call `next(&current)` instead
  of `next(&current, &current_length)`, and then attempts to read `.length` from
  the pointer variable. Protocol-shaped `iter` foreach already has coverage for
  expanded slots; this bug is specific to the concrete iterator path.
