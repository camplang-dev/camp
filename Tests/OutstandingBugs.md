# Outstanding Bugs

Next bug number: BUG-038.

## BUG-037: Primitive `new` declarations can survive lowering and fail during C emission

`auto value = new int();` currently reaches C emission as a `ConstructionExpression`
instead of lowering to an allocation expression or a clear diagnostic. This is
not part of the explicit escaped delegate lambda proposal; the regression tests
for allocator capture use class allocation instead.
