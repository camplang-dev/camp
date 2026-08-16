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

## BUG-080: Iterator lowering collides locals with the same source name in disjoint block scopes

Status: open

Observed while smoke-testing bytecode decoder iterators. Same-named local
variables declared in separate `if`/`else` block scopes are valid in ordinary
functions, but the same pattern inside a `struct iter` can fail during iterator
lowering because both locals are lifted to an iterator state field with the same
name.

General repro:

1. Define a `struct iter int values(bool choose)`.
2. In the `if` branch, declare `int value = 1; yield value;`.
3. In the `else` branch, declare `int value = 2; yield value;`.
4. Iterate over `values(false)` from `main`.

Expected behavior: the iterator should compile and preserve the source lexical
block scopes. Lowering may lift locals into state, but generated storage names
must remain unique when source locals belong to distinct scopes.

Current behavior: analysis/lowering reports `Iterator state field 'value' is
already declared.`

Current workaround: give iterator locals unique source names across the whole
iterator body, even when they are in disjoint block scopes.
