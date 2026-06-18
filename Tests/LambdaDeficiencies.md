# Lambda Deficiencies

Stage A scoped lambdas appear complete based on the committed Stage A and
hardening tests. The remaining deficiencies below are for the revised Stage B
and Stage C work around escaped delegates and final cross-feature hardening.

Next deficiency number: DEF-008.

- **DEF-001:** Implement escaped delegate lambda lowering.
  Stage: B. Priority: high. Complexity: high. The analyzer and lowering still
  report `Escaped delegate lambdas are not implemented yet.` for escaped
  delegate targets. Add lowering for escaped delegate lambdas that allocates a
  context with the current allocator, copies permitted captures into that
  context, returns the ordinary delegate components, and emits compilable C for
  both direct escaped delegate variables and calls that receive escaped delegate
  parameters.

- **DEF-002:** Enforce escaped capture legality and read-only captured values.
  Stage: B. Priority: high. Complexity: medium-high. Escaped delegates should
  be allowed to capture only values that are valid to escape. Local values copied
  into the escaped context become read-only from inside the lambda. Add
  diagnostics/tests for capturing non-escaped pointers/references into escaped
  contexts, mutating copied escaped captures, capturing `this` with insufficient
  lifetime, and valid capture of escaped/copyable values.

- **DEF-003:** Define escaped delegate context ownership and cleanup behavior.
  Stage: B. Priority: high. Complexity: medium-high. Escaped lambda contexts
  need a clear runtime ownership model: allocate with the same allocator
  semantics as `new`, expose enough context shape for callers to eventually
  `delete del.context`, and ensure generated context cleanup is valid for
  fields that need cleanup. Add tests for explicit deletion of escaped delegate
  contexts, creation inside `within (...)`, null allocation behavior, and
  cleanup through `finally delete` once the delegate context is manually owned.

- **DEF-004:** Harden escaped captures of expanded/materialized values.
  Stage: B. Priority: medium-high. Complexity: high. Scoped lambdas have tests
  for expanded captures and expanded returns, but escaped delegates need their
  own rules. Add support/tests for capturing and returning expanded values such
  as `T[]`, delegates, optionals, strings converted to spans, and callable
  newtypes. The escaped context must store a coherent materialized value or
  reject the capture with a clear diagnostic when a referenced view would not
  remain valid.

- **DEF-005:** Harden escaped lambda target typing with callable newtypes,
  method references, and overloads. Stage: B/C. Priority: medium. Complexity:
  medium. Existing scoped tests cover callable newtypes and overload-selection
  restrictions. Escaped delegate targets need matching coverage: escaped
  delegate newtypes, explicit target-typed calls, `auto` behavior when an
  escaped target is or is not present, overload diagnostics for lambda
  arguments, and bound method references used where an escaped delegate is
  required.

- **DEF-006:** Repeat the Stage A hardening matrix for escaped delegates and
  remove any Stage C gaps. Stage: C. Priority: medium-high. Complexity: high.
  Stage A has committed scoped coverage for block bodies, generic extension
  methods, iterators, `foreach`, expanded parameters/returns, parameter
  modifiers, catch variables, `try`/`finally`, and `this` capture. Once Stage B
  exists, add escaped-delegate equivalents or explicit diagnostics for each
  area, then run the full golden suite and remove this umbrella item only after
  every case has a committed test.

- **DEF-007:** Harden lambda diagnostics and generated C/API hygiene.
  Stage: C. Priority: medium-high. Complexity: medium. Add focused tests and
  fixes for missing target types, overload ambiguity, invalid capture, and
  unsupported expression-position diagnostics. Verify no residual
  `LambdaExpression` reaches C emission, generated helper/context names are
  collision-safe, and generated lambda helper functions/context structs stay out
  of public API headers. Include nested lambdas mixing scoped and escaped
  targets once escaped lowering exists.
