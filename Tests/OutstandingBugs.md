# Outstanding Bugs

- **BUG-003:** Direct class interface vtable entries can assign implementation methods whose receiver type is the implementing class pointer to slots whose current C type expects the interface-instance receiver shape. The interface ABI needs either fixup entries or ABI receiver typing that is C-compatible.
- **BUG-004:** Property getter syntax for functions with expanded return values can lower to the getter function value instead of invoking the getter. For example, `text.Span` currently emits the `String_getSpan` function pointer, while the explicit `text.getSpan()` call works.
- **BUG-005:** Omitted `@range` arguments on a string receiver can produce an empty span for `getSpan()` instead of using the receiver length. The std string runtime test currently uses explicit `getSpan(0, value.Length)` calls to avoid this lowering issue.
- **BUG-006:** Expanded-return calls used as declaration initializers can declare the expanded local components without assigning them. For example, `const char[] span = text.getSpan(0, text.Length);` emits uninitialized `span` and `span_length` locals.
