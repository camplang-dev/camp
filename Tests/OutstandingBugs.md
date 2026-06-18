# Outstanding Bugs

Next bug number: BUG-022.

- **BUG-017:** Iterator protocol adapters and iterator `foreach` lowering do not
  fully support expanded yielded slot types such as `iter byte[]`. A generator
  can lower a `next(byte** current, nuint* current_length)` shape, but the
  generated `op_iter` adapter and `foreach` current-slot materialization still
  need first-class expanded-slot forwarding.

- **BUG-018:** Fixed-array pointer receiver/member diagnostics need final
  hardening. Priority: high. Complexity: medium. The proposal explicitly says a
  `T[n]*` value must not silently act like either a `T[n]` value or a `T*`
  element pointer. Add diagnostics and tests for `p.length`, `p.elements`,
  `p[0..2]`, `byte x = p[0]`, hidden `T[] this` receiver selection from
  `T[n]*`, hidden `T* this` receiver selection from `T[n]`, and hidden
  `T[n]* this` receiver selection from `T[n]`. Positive tests should cover the
  explicit forms: `(*p).length`, `(*p).elements`, `(*p)[0]`, `(*p)[0..2]`,
  `&fixedArray`, and explicit `.elements` calls.

- **BUG-019:** Fixed-array span-view lifetime and component synthesis are not
  fully audited. Priority: high. Complexity: medium-high. Add tests for fixed
  arrays converting to `T[]` in all proposal-required component-synthesis
  contexts that are not already covered: named arguments to expanded array
  parameters, `const T[]` span initialization, valid scoped return of a
  fixed-field span, invalid return of a local fixed-array span, valid yield of
  `T[]` or `T[n]*` from iterator-owned fixed storage, and invalid lifetime
  cases. Keep BUG-017 separate for expanded iterator protocol plumbing such as
  `iter byte[]`; this bug is about validating the semantic/lifetime surface and
  ordinary component synthesis around fixed-array views.

- **BUG-020:** Fixed arrays in lambdas and delegate contexts are not covered
  enough. Priority: medium-high. Complexity: high. The proposal requires tests
  for lambda/delegate interaction with fixed storage: accepting
  `delegate void(byte[n]*)`, rejecting `delegate void(byte[n])`, capturing a
  pointer-to-fixed-array, capturing a fixed-array span view under a scoped
  lifetime, rejecting fixed-array capture-by-value, rejecting escaped delegates
  that would retain stack fixed storage, and generated delegate context types
  that contain fixed-array fields initialized from `default` or literals rather
  than copied from an existing fixed array.

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
