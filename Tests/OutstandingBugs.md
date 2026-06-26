# Outstanding Bugs

Next bug number: BUG-032.

- **BUG-029:** Some `foreach` forms still reach C emission instead of lowering
  when combined with generic or generator contexts. A generic function iterating
  `foreach (auto item in this)` over `T[] this` with `sizeof(T)`, and an array
  `foreach` inside a generator body, can leave a residual `ForeachStatement`
  that the C emitter rejects. Work around with explicit index loops until the
  generic-array and generator-body foreach lowering paths share the ordinary
  array lowering machinery.

- **BUG-030:** Derived class constructors emit an invalid base-constructor call
  in C. When a class derives from another ordinary class, both explicit and
  synthesized derived constructors can emit `Base_op_initnew()` without passing
  the derived `this` pointer converted to the base type. Clang and MSVC reject
  the generated C with "too few arguments for call." Work around by avoiding
  inheritance in native-built classes until base constructor emission passes the
  receiver.

- **BUG-031:** Derived classes should be forbidden from declaring methods that
  would newly satisfy optional interface methods from an interface implemented
  by a base class. Interface re-implementation is already rejected, but optional
  methods inherited through an interface contract can still be accidentally
  introduced on a derived class. Until this is validated, avoid declaring
  optional-interface-shaped methods on derived classes when the base class
  implements that interface.
