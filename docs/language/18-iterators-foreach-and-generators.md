# Iterators, `foreach`, And Generators

## Iterator Type Forms

Iterator types use `iter`.

```camp
iter int numbers;
```

Iterator values are expanded forms with protocol methods and state.

## Iterator Protocol

An iterator provides the operations needed to advance, inspect, and clean up an
iteration. Arrays have built-in `foreach` support, and user types can expose
iterator-compatible surfaces.

## Generator Functions

Functions that yield values can produce iterator-compatible results.

```camp
export iter int countUp(int limit)
{
	for (int value = 0; value < limit; value++)
		yield value;
}
```

## `yield`

`yield` produces the next iterator value. The yielded expression must be
compatible with the iterator element type. Thrown values can participate in
iterator signatures.

## `foreach`

`foreach` consumes arrays and iterators.

```camp
foreach (int value in countUp(10))
	sum += value;
```

The loop variable is scoped to the loop body.

## Iterator Cleanup

Iterator cleanup runs according to the iterator protocol and any source cleanup
rules attached to the loop body or generated state.

## Iterator Limitations

Iterator behavior that depends on generated state, captured lifetimes, or
generic erasure is compiler-writer detail. Ordinary code should expose clear
element types and avoid retaining scoped loop values beyond their valid
lifetime.
