# Outstanding Bugs

Next bug number: BUG-028.

- **BUG-022:** Block doc comments using conventional multi-line `/** ... */`
  formatting need stronger parsing/stripping and focused tests. Priority:
  medium. Complexity: medium. Current doc-comment metadata work focuses on line
  comments and literal-region behavior; a later pass should verify leading `*`
  stripping on every block line, blank doc-comment lines inside blocks, fenced
  code blocks inside block comments, child targets, and source-range diagnostics.

- **BUG-023:** Returning a casted expanded initializer such as
  `return (unscoped(owner) char[]){ ptr, length };` type-checks but emits
  invalid C (`return (char *)({ ptr, length });`). Priority: medium.
  Complexity: medium. The equivalent local materialization works:
  `char[] result = { ptr, length }; return (unscoped(owner))result;`. A later
  emitter/lowering pass should lower expanded initializer return values through
  normal expanded return component assignments before C emission.

- **BUG-024:** Stage 4 lifetime call-site substitution does not yet refine
  return constness where the relationship is directly provable. Priority:
  medium. Complexity: medium-high. Example: if a function returns
  `scoped const char[]` from a single `const char[]` parameter, and the caller
  passes a provably mutable `char[]`, the result should retain the caller-known
  mutability when the relationship proves it is derived from that argument.
  This should be implemented as a type-refinement pass with positive and
  conservative negative tests, not as a lifetime fact shortcut.

- **BUG-025:** Exported and abstract function signatures reject lifetime
  annotations in places where they should be valid signature annotations.
  Priority: medium-high. Complexity: medium. For example,
  `export abstract void free(escaped void* ptr);` currently reports a
  misleading field-type lifetime diagnostic through API/header processing.
  Std allocator declarations are temporarily left unannotated and trusted
  boundaries use explicit lifetime casts instead.

- **BUG-026:** Returning a lifetime-casted expanded value can drop hidden
  return components during C emission. Priority: medium. Complexity: medium.
  For example, returning `(escaped T[])bufferResult` from a function returning
  `T[]` can emit only the pointer result and fail to assign the hidden length
  result parameter. Returning an equivalent slice currently preserves the
  expanded components and is used as a workaround.

- **BUG-027:** `foreach` over a concrete iterator state that yields an expanded
  params value can omit hidden current-slot component arguments. Priority:
  medium. Complexity: medium. For example, directly enumerating a concrete
  `splitFirstIter` that yields `const char[]` may call `next(&current)` instead
  of `next(&current, &current_length)`, and then attempts to read `.length` from
  the pointer variable. Protocol-shaped `iter` foreach already has coverage for
  expanded slots; this bug is specific to the concrete iterator path.
