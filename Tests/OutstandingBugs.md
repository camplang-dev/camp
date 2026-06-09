# Outstanding Bugs

- **BUG-003:** Direct class interface vtable entries can assign implementation methods whose receiver type is the implementing class pointer to slots whose current C type expects the interface-instance receiver shape. The interface ABI needs either fixup entries or ABI receiver typing that is C-compatible.
- **BUG-004:** Assigning an expanded-return callable/iterator value directly into an expanded local can emit an extra hidden context out argument, for example `CharWriter writer = Console.getWriter()` lowering to a call with two context outputs.
- **BUG-005:** A throwing call inside a function that already has a lowered `thrown` parameter can emit `&error` instead of forwarding `error`, corrupting C calls unless the source uses an explicit `catch error`.
