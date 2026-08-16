# Outstanding Bugs

Next bug number: BUG-082.

## BUG-081: Generic `in T` parameter address can be retained without lifetime diagnostic

Status: open

Observed while smoke-testing interpreter-like generic value slots. A generic
function can take the address of an `in T` parameter and store it in retained
generic pointer storage without a lifetime diagnostic. The equivalent
non-generic program is correctly rejected with a scoped pointer-bearing storage
diagnostic.

General repro:

1. Define `struct Holder<T: copyable> { T* value; }`.
2. Define `void retain<T: copyable>(Holder<T>* holder, in T value, sizeof(T))`
   that assigns `holder.value = &value;`.
3. Instantiate `Holder<int>` in a caller and call `retain<int>(&holder, 42)`.
4. Build the program.

Expected behavior: the assignment should be rejected. Per the generic and
lifetime semantics, `in T` is observation transport only and does not grant
permission to copy, return, or retain the value. The address of the parameter is
scoped to the current function and cannot be stored into longer-lived object
storage.

Current behavior: the program is accepted when no later operation forces a
native C error.

Current workaround: do not retain addresses of `in T` parameters. Require an
explicit pointer-bearing parameter whose lifetime is valid for the target
storage.
