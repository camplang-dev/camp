# `constof` And Signature Compatibility

## Anchor Binding

`constof(anchor)` resolves `anchor` in the current signature scope. Valid
anchors include parameters and receivers where the source grammar permits them.
Unresolved anchors are diagnostics.

## Caller-Visible Substitution

At a call site, `constof(anchor)` is substituted from the argument or receiver
constness visible to the caller. Return and output positions preserve caller
constness through that substitution.

## Callee Implementation View

Inside the callee, `constof` is treated as a bound relationship rather than a
new independent qualifier. The implementation may use the value according to
the substituted contract.

## Storage Conversions

Assignment and storage conversions involving `constof` must not remove
constness. The compiler distinguishes ordinary storage conversion from
signature-level `constof` parameter passing.

## Parameter Passing

`constof` parameter passing uses call-site substitution and callable-shape
compatibility. It is not simply a cast from one stored type to another.

## Return And `out` Positions

Return positions are covariant for compatible constness. Input positions are
contravariant where callable signature compatibility permits it. `out` payloads
must preserve the caller-visible contract.

## Callable Variance

Anonymous callables and callable newtypes may be compatible across `constof`
variance when the source and target signatures satisfy the variance rules.

## Override Exactness

Virtual overrides remain exact. Do not apply ordinary callable variance to
override matching because vtable slots require stable ABI shape.

## Lambda Target Typing

Target-typed lambdas inherit `constof` parameter and return expectations from
their target callable shape. Auto-inferred lambdas must infer a signature that
can satisfy the target or report a diagnostic.

## `constof(this)`

`constof(this)` binds to the receiver in context-carrying callable shapes.
Lowering must preserve the relationship through delegate/context expansion.

## Diagnostics

Diagnostics should identify the anchor, the position where constness is lost or
over-constrained, and whether the failure is assignment, call matching,
override matching, lambda target typing, or explicit cast validation.
