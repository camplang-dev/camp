# Outstanding Bugs

Next bug number: BUG-033.

- **BUG-029:** Some `foreach` forms still reach C emission instead of lowering
  when combined with generic or generator contexts. A generic function iterating
  `foreach (auto item in this)` over `T[] this` with `sizeof(T)`, and an array
  `foreach` inside a generator body, can leave a residual `ForeachStatement`
  that the C emitter rejects. Work around with explicit index loops until the
  generic-array and generator-body foreach lowering paths share the ordinary
  array lowering machinery.

- **BUG-031:** Derived classes should be forbidden from declaring methods that
  would newly satisfy optional interface methods from an interface implemented
  by a base class. Interface re-implementation is already rejected, but optional
  methods inherited through an interface contract can still be accidentally
  introduced on a derived class. Until this is validated, avoid declaring
  optional-interface-shaped methods on derived classes when the base class
  implements that interface.
