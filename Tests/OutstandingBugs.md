# Outstanding Bugs

- **BUG-004:** Calls with an explicit `catch` argument do not currently receive an
  implicit `within` argument from the active `within (...)` context. Calls such
  as `reader.readLine(catch error)` inside a `within (allocator)` block should
  be able to insert the allocator automatically, but today they must spell
  `within allocator, catch error` explicitly.
- **BUG-005:** Bound delegate values for expanded-form receivers such as
  `const char[] this` do not currently preserve all receiver components. The
  delegate context can hold the receiver pointer, but not the companion length
  value, so calls like `span.format` cannot be represented correctly without a
  generated context object or adapter thunk.
- **BUG-008:** Default argument insertion can append a default for a later
  parameter even when the caller supplied that argument explicitly in some
  generic/delegate call shapes. `List<T>.binarySearch` avoids optional
  `startAt/count` parameters until this is fixed.
- **BUG-009:** Taking the address of an erased generic array element can emit
  C like `&items[index]` against a `void*` backing pointer instead of byte
  offsetting by `index * sizeof(T)`. `List<T>.getAddressOf` currently performs
  the byte arithmetic explicitly.
- **BUG-010:** Iterator state fields that store expanded params values are not
  fully materialized in emitted C. A generator local such as
  `delegate int(int) map = ...` can emit a raw initializer for a delegate field,
  and an `int[]` generator parameter stored on the iterator state can later emit
  `this->values.length` instead of using the lifted companion length field.
- **BUG-011:** Callable typedefs are emitted before enum typedefs, so a function
  pointer type with an enum slot such as `delegate void(thrown MyError)` can
  reference `MyError` before the enum typedef exists. The C emitter needs either
  enum typedefs before callable typedefs or enum-tag spelling in callable slots.
- **BUG-012:** A call that passes an expanded delegate value followed by an
  explicit `within` argument can retain an extra generated hidden argument.
  For example, forwarding `delegate int(within Allocator*)` through a wrapper
  can emit `wrapper(call, context, allocator, null)` even though the wrapper
  expects only `call, context, allocator`.
- **BUG-013:** Callable typedef names and types can lose return-value constness
  for pointer returns. For example, a `newtype delegate const char* Getter()`
  can emit a `char* (*)(void*)` storage type and reject a lambda returning
  `const char*`.
- **BUG-014:** Lambda return lowering can reuse an outer function's cleanup
  label and generated return storage when the enclosing scope has an active
  `finally` cleanup. A lambda inside a function with `finally delete` can emit
  `goto __cleanupN` to a label that only exists in the outer function.
