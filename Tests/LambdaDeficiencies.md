# Lambda Deficiencies

Stage A scoped lambdas are complete only when every item below has a committed
test and the item has been removed from this list.

- **LAMBDA-A-010:** Scoped lambda escape rejection beyond `fn` and escaped
  delegate targets needs diagnostics coverage.
- **LAMBDA-A-011:** Overload ambiguity diagnostics with lambda arguments need
  coverage.
- **LAMBDA-A-012:** Capturing params-expanded locals and components directly
  needs coverage.
