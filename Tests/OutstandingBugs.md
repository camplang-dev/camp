# Outstanding Bugs

Next bug number: BUG-023.

- **BUG-021:** Fixed-array aggregate/generic edge coverage is incomplete.
  Priority: medium. Complexity: medium-high. Add tests for the proposal's
  remaining aggregate and generic cases: `Box<T: any>` accepting direct
  non-copyable arguments such as `byte[n]`, fixed structs, and classes when the
  generic stores only `T*`; `ValueBox<T: copyable>` accepting pointer-to-fixed
  arrays but rejecting direct fixed arrays/classes/fixed structs at both type
  construction and call substitution sites; copyable struct assignment copying
  fixed-array fields as part of the containing aggregate while direct
  fixed-field copy remains rejected; copyable structs rejecting fixed-array
  fields whose leaf element is non-copyable; fixed structs/classes accepting
  those fields; and either fully supporting or deliberately rejecting spans whose
  element type is itself a fixed array, such as `byte[8][]`, with tests for the
  chosen behavior.

- **BUG-022:** Block doc comments using conventional multi-line `/** ... */`
  formatting need stronger parsing/stripping and focused tests. Priority:
  medium. Complexity: medium. Current doc-comment metadata work focuses on line
  comments and literal-region behavior; a later pass should verify leading `*`
  stripping on every block line, blank doc-comment lines inside blocks, fenced
  code blocks inside block comments, child targets, and source-range diagnostics.
