# Lifetime Analysis And Flow Facts

## Bound Lifetime Model

Lifetime annotations bind to a normalized fact: kind, anchors, and source. The
compiler tracks facts for slots and values separately so pointer-bearing
storage and computed values can be validated precisely.

## Lifetime Anchors

Anchors are visible names used by `scoped(...)`, `unscoped(...)`, and related
lifetime forms. The binder must reject missing anchors and anchors not valid in
the current signature or body context.

## Slot Facts And Value Facts

Slot facts describe storage lifetime. Value facts describe the value read from a
slot or produced by an expression. Assignments, member access, casts,
initializer lists, calls, and returns may transform or check these facts.

## Defaults

Parameters and receivers receive declaration-site defaults when no explicit
annotation is present. Defaults differ for receivers, ordinary parameters,
escaped fields, and allocation contexts.

## Assignment And Storage

Assignment checks that a value's lifetime can be stored in the target slot. A
scoped value cannot be stored in escaped storage unless a valid cast or proof
changes the fact.

## Return, Yield, And Delete

Return and yield expressions are checked against the function or iterator
result lifetime. `delete` checks the value against allocation and free
requirements.

## Call-Site Relation Solving

Calls solve relationships among arguments, anchors, receiver, return, `out`
values, and thrown slots. `constof` and lifetime facts interact through the same
signature surface but enforce different contracts.

## Constructors And Retained Values

Constructors that retain pointer-bearing values must prove the retained values
outlive the constructed object or are explicitly escaped.

## Delegates And Captures

Escaped delegates and once callables require captured values to satisfy the
context lifetime. Scoped delegate lambdas can capture scoped values only when
the generated context does not escape.

## Iterators And Generated Contexts

Iterator and generator state can retain values across yields. Analysis must
validate captured values and yielded references against generated state
lifetime.

## Generics

Generic code may require explicit capabilities to copy, store, construct, or
destroy values. Lifetime checks must operate on the instantiated or erased shape
visible to the generic body.

## Diagnostics

Diagnostics should state which value cannot be stored, returned, yielded,
deleted, or captured, and identify the lifetime fact that causes the conflict.
