# Lambda Deficiencies

Stage A scoped lambdas appear complete based on the committed Stage A and
hardening tests. The remaining deficiencies below are for the revised Stage B
and Stage C work around escaped delegates and final cross-feature hardening.

Next deficiency number: DEF-008.

- **DEF-007:** Harden lambda diagnostics and generated C/API hygiene.
  Stage: C. Priority: medium-high. Complexity: medium. Add focused tests and
  fixes for missing target types, overload ambiguity, invalid capture, and
  unsupported expression-position diagnostics. Verify no residual
  `LambdaExpression` reaches C emission, generated helper/context names are
  collision-safe, and generated lambda helper functions/context structs stay out
  of public API headers. Include nested lambdas mixing scoped and escaped
  targets once escaped lowering exists.
