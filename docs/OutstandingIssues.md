# Outstanding Issues

This file records documentation uncertainty: places where the intended Camp
semantics cannot be settled from the docs, the source, and smoke tests. A
confirmed compiler defect belongs in `tests/OutstandingBugs.md` instead.

Number issues in order and keep the numbers stable. When an issue is resolved,
update the affected docs and remove its entry in the same change.

## ISSUE-1: Generic inference when an `out` argument shares a parameter with an `in` argument

Date: 2026-10-30

Question:
When one type parameter appears in both an `in T` slot and an `out T` slot, which
type does inference choose, and does the `out` argument take part in the choice?

```camp
void exchange<T>(in T source, out T destination, sizeof(T), typenameof(T))
{
	destination = default;
}

int small = 1;
long wide = 2;
exchange(small, out wide);
```

Observed:
The compiler fixes `T` from the `in` argument alone, so the call above infers
`T` as `int` and accepts it, converting the `int` into `wide`. An `out` argument
whose type differs from the other arguments is therefore not part of the common
type, and it is checked in the opposite direction: the value the callee stores
converts to the argument (`exchange(wide, out small)` is an error at `small`).

The accepted type-inference design describes an `out T` slot as an exact bound
that the `in T` arguments must also convert to, which would infer `long` here.

Where the docs stand:
Semantics 06 (Type Argument Inference) states the rules that both designs agree
on: top-level `in` and by-value arguments take their best common type, and every
other position, an `out` slot included, is matched against its argument's type.
It does not state the mixed case. Decide which behavior is intended, then state
it in semantics 06 and add a test; if the design is the intended one, log the
compiler difference as a bug.
