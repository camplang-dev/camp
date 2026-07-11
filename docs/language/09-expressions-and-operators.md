# Expressions And Operators

## Expression Grammar

Camp expressions include literals, names, member access, calls, indexing,
casts, construction, arrays, initializer lists, lambdas, unary and binary
operators, conditionals, ranges, `await`, `postpone`, `throw`, `within`
expressions, and special expressions such as `sizeof`.

## Operator Precedence

Postfix operations bind tightly, followed by prefix operations, multiplicative
operators, additive operators, shifts, comparisons, equality, bitwise
operators, null-coalescing, logical operators, conditional expressions, and
assignment expressions.

Use parentheses where readability matters.

```camp
int total = (left + right) * scale;
```

## Calls, Indexing, And Member Access

Calls use `target(arguments)`. Indexing uses `target[index]`. Member access uses
`.`.

```camp
nuint count = items.length;
int first = values[0];
writer.writeLine("ready");
```

Property and indexer syntax is source sugar over callable declarations and
member lookup.

## Casts

Type casts use `(T)value`. Unsafe casts use `(unsafe T)value`. Lifetime casts
use forms such as `(scoped)value`, `(escaped)value`, and
`(unscoped(anchor))value`.

```camp
nint index = (nint)position;
escaped byte* retained = (escaped byte*)buffer;
```

## Construction Expressions

`init` constructs a value in declaration-scope storage. `new` allocates and
constructs a value through the current or explicit allocation context.

```camp
Position position = init Position(row: 2, column: 4);
Buffer* buffer = new Buffer(1024);
```

`within(context)` can supply an explicit allocation context for construction.

## Initializer Lists

Initializer lists use braces and may be positional or named, depending on the
target type.

```camp
Position home = { .row = 0, .column = 0 };
int[3] scores = { 10, 20, 30 };
```

Do not mix named and positional entries in expanded initializer contexts.

## `sizeof`, `vtableof`, `typenameof`, And `symbolof`

`sizeof(T)` provides size information where the type is known or supplied as a
generic capability. `vtableof(T: Interface)` provides interface vtable
capability information for generic interface use. `typenameof(T)` produces a
runtime name string for supported type forms.

`symbolof(Name)` is valid only inside metadata attribute arguments.

## Target Typing

Some expressions take their expected type from context. This includes
`default`, array literals, lambdas, initializer lists, and some literals.

```camp
delegate bool(int value) isPositive = value => value > 0;
```
