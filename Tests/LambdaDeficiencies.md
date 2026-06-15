# Lambda Deficiencies

Stage A scoped lambdas are complete only when every item below has a committed
test and the item has been removed from this list.

- **LAMBDA-A-004:** Lambdas inside `try`/`catch`/`finally` control-flow contexts
  need coverage.
- **LAMBDA-A-005:** Lambdas that capture catch variables need coverage.
- **LAMBDA-A-006:** Lambdas inside iterator/generator bodies need coverage.
- **LAMBDA-A-007:** Lambdas used from `foreach` bodies need coverage.
- **LAMBDA-A-008:** Lambda parameters with `out`, `thrown`, and `within`
  modifiers need coverage.
- **LAMBDA-A-009:** Lambda return and call behavior involving expanded
  array/optional return values needs coverage.
- **LAMBDA-A-010:** Scoped lambda escape rejection beyond `fn` and escaped
  delegate targets needs diagnostics coverage.
- **LAMBDA-A-011:** Overload ambiguity diagnostics with lambda arguments need
  coverage.
- **LAMBDA-A-012:** Capturing params-expanded locals and components directly
  needs coverage.
