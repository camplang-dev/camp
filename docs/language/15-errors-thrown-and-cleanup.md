# Errors, Thrown, And Cleanup

## `thrown` Parameters

`thrown` parameters model error slots in callable signatures.

```camp
export void readFile(const char[] path, out byte[] data, thrown IoError);
```

A thrown slot is part of the callable shape and participates in forwarding,
callbacks, iterators, and async completion.

## `throw`

`throw` produces an error value for a matching thrown slot.

```camp
throw missingFile;
```

The surrounding function must have a compatible thrown surface.

## `try`, `catch`, And `finally`

`try` protects a block. `catch` handles thrown values. `finally` runs cleanup.

```camp
try
{
	readFile(path, out data);
}
catch (auto error)
{
	report(error);
}
finally
{
	delete data.elements;
}
```

## Catch Arguments

Calls can use `catch` arguments to bind or handle thrown slots at the call site.

```camp
readFile(path, out data, catch error);
```

`catch auto name` asks the compiler to infer the error variable type.

## Cleanup Expressions

Expressions can use cleanup forms such as `finally delete` where the language
permits expression-level cleanup.

```camp
Buffer* buffer = new Buffer(1024) finally delete;
```

## Error Propagation In Calls

Thrown slots can be forwarded through ordinary calls, iterator yields, and async
completion callbacks. The compiler verifies that unhandled thrown values have a
valid destination.
