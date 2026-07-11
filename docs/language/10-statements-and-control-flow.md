# Statements And Control Flow

## Blocks And Empty Statements

A block is a sequence of statements in braces. A semicolon by itself is an
empty statement.

```camp
{
	int count = 0;
	;
}
```

## Local Declarations

Local declarations introduce variables inside a function body.

```camp
int remaining = total;
auto doubled = remaining * 2;
```

`auto` asks the compiler to infer the local type from the initializer. Local
variable types do not carry lifetime annotations; use lifetime casts when a
local value needs an explicit lifetime fact.

## `if`, `while`, `do`, And `for`

Camp supports ordinary conditional and loop statements.

```camp
if (count == 0)
	return;

while (count > 0)
	count--;

for (int index = 0; index < count; index++)
	sum += values[index];
```

## `switch`, `case`, And `default`

`switch` selects among `case` labels and an optional `default` label.

```camp
switch (state)
{
	case 0:
		return;
	default:
		break;
}
```

## `break`, `continue`, `goto`, And Labels

`break` exits a loop or switch. `continue` advances a loop. Labels use
`name:`. `goto name;` jumps to a visible label.

## `return` And `yield`

`return` exits a function. `yield` produces iterator values from generator
functions.

```camp
return value;
yield current;
```

## `foreach`

`foreach` iterates over arrays and iterator-compatible values.

```camp
foreach (int value in values)
	sum += value;
```

Iterator details are covered in
[Iterators, `foreach`, And Generators](18-iterators-foreach-and-generators.md).

## Statement Conditions

Conditions may contain declarations where the grammar permits them. Statement
conditions are analyzed as part of body flow and lifetime checking.
