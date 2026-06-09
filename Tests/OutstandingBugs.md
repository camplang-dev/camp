# Outstanding Bugs

- **BUG-003:** Direct class interface vtable entries can assign implementation methods whose receiver type is the implementing class pointer to slots whose current C type expects the interface-instance receiver shape. The interface ABI needs either fixup entries or ABI receiver typing that is C-compatible.
- **BUG-005:** A throwing call inside a function that already has a lowered `thrown` parameter can emit `&error` instead of forwarding `error`, corrupting C calls unless the source uses an explicit `catch error`.
