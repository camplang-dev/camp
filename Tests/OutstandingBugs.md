# Outstanding Bugs

Next bug number: BUG-025.

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
