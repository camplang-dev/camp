# Outstanding Bugs

Next bug number: BUG-024.

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
