# Your First Camp Program

The first page gave you the map. This chapter gives you a place to stand.

We will start with a complete file, pull it apart line by line, and then grow
it just enough to show the habits that matter in everyday Camp: using
standard-library names, declaring `main`, returning a status, binding local
values, calling helpers, and reading command-line arguments when your program
has them.

The goal is not to explain every rule. It is to make a Camp source file feel
less like an artifact and more like a room you can walk around in.

## A Complete File

Save this as a `.camp` file and it is a whole program:

```camp
export int main()
{
	Console.writeLine("Hello, Camp.");
	return 0;
}
```

There are three ideas here:

1. `export int main()` declares the executable entry point.
2. The body writes a line and returns a process-style status code.
3. `Console` is available because the standard library is included by default.

Nothing runs at the top level. A Camp file is made of declarations. Execution
starts because the exported `main` function is selected as the program entry
point by the build.

## Standard Library Names Are Available By Default

Unless the build uses `--nostdlib`, the compiler prepares the bundled standard
library and adds an implicit root import equivalent to `using Std;` for ordinary
source files. It does not allocate anything, run initialization code, or create
a runtime namespace object. It simply lets you write names from `Std` without
qualifying them every time.

The tiny program relies on that default import:

```camp
export int main()
{
	Console.writeLine("ready");
	return 0;
}
```

`Console` is not syntax. It is an ordinary standard-library class with static
helpers such as `write`, `writeLine`, and `newLine`. That distinction matters:
the language gives you imports and calls; the library gives you a console API.

As programs grow, explicit imports can help keep source lookup precise, but the
deeper namespace rules come later. The first rule is enough for now: ordinary
standard-library names are available by default.

## `main` Is An Ordinary Function At The Boundary

The program entry point is not a magic block. It is a function declaration:

```camp
export int main()
{
	return 0;
}
```

`export` places the function on the external boundary. `int` is the return type.
`main` is the name. `()` is the parameter list. The braces contain the function
body.

Returning `0` conventionally means success. Returning another value gives the
surrounding process a failure or status code:

```camp
export int main()
{
	Console.writeLine("configuration file missing");
	return 2;
}
```

Camp also supports `main` with arguments:

```camp
export int main(string[] args)
{
	if (args.length == 0)
	{
		Console.writeLine("Pass a name.");
		return 2;
	}

	Console.write("Hello, ");
	Console.writeLine(args[0]);
	return 0;
}
```

`string[]` is an array view of strings. The array chapter will explain the
shape in detail; for this first program, the useful facts are that `args.length`
is the count and `args[0]` reads the first argument.

## Local Values And Calls

Inside a function, Camp should feel unsurprising. You can bind local values,
call functions, branch, and return.

```camp
export int main()
{
	int answer = 40 + 2;
	auto message = "The answer is:";

	Console.writeLine(message);
	Console.writeLine(answer);
	return 0;
}
```

Use an explicit type when the type is part of the point:

```camp
int answer = 42;
```

Use `auto` when the initializer already tells the story:

```camp
auto message = "ready";
```

The examples here are small, but this habit scales. Public signatures should
usually say their types out loud. Local code can be lighter when the nearby
initializer makes the type obvious.

Function calls use the familiar `name(arguments)` shape. Static members use
the same member-access operator as instance members:

```camp
Console.writeLine("saved");
```

Camp does not use C's `->` spelling for pointer member access. Member access
uses `.` throughout; pointer, value, class, and interface receiver details are
handled by the language rules behind that one source form.

## Splitting Work Into Helpers

A helper is just another declaration. You do not need to turn small programs
into classes or invent a framework before you can name a piece of work.

```camp
export int main(string[] args)
{
	if (args.length == 0)
	{
		Console.writeLine("Pass a name.");
		return 2;
	}

	return greet(args[0]);
}

int greet(string name)
{
	Console.write("Hello, ");
	Console.writeLine(name);
	return 0;
}
```

This program has two declarations: `main` and `greet`. `main` decides whether
the program has enough input, then calls the helper. `greet` owns the greeting
itself.

The declarations are ordered by use: the entry point comes first, and the
helper appears when the reader has seen why it exists. Camp does not need the
C habit of putting callees first just to avoid forward declarations. Prefer the
order that explains the program.

## What The Compiler Sees

At this scale, it is useful to know the compiler's broad view without diving
into compiler internals.

For the `greet` example, the compiler sees:

- the implicit root `Std` import;
- one internal helper function, `greet`;
- one exported entry point, `main`;
- calls to standard-library console functions;
- an array argument whose count and elements are used by `main`;
- integer return values that become status codes.

That is already more structure than the surface size suggests. Camp is designed
so that this structure stays visible as the program grows. A declaration says
what it exports. A signature says what it needs. A call says what it passes.
Later chapters build on those same habits rather than replacing them.

## A Slightly Less Tiny Program

Here is a complete program that prints each argument with its index:

```camp
export int main(string[] args)
{
	return printArguments(args);
}

int printArguments(string[] args)
{
	if (args.length == 0)
	{
		Console.writeLine("No arguments.");
		return 1;
	}

	for (nuint index = 0; index < args.length; index++)
	{
		Console.write("arg ");
		Console.write(index);
		Console.write(": ");
		Console.writeLine(args[index]);
	}

	return 0;
}
```

There is not much new syntax, but the program has the shape of real code:

- `main` is thin and delegates the work.
- `printArguments` reports failure with a status code.
- `args` is treated as an array view.
- `nuint` is used for the index because array lengths are natural unsigned
  counts.
- Console output is ordinary library code, not a special language form.

This is the rhythm of Camp at small scale. Put contracts in declarations. Keep
the entry point simple. Use helpers when a name makes the program easier to
read. Let ordinary code stay ordinary.

## What You Have Now

After this chapter, you should be able to read a small Camp file and identify
the moving pieces:

- optional prelude directives and imports at the top;
- declarations below them;
- `main` as the executable entry point;
- local variables inside function bodies;
- standard-library calls through the implicit root `Std` import;
- status codes returned with `return`;
- arrays as values with `.length` and indexed access.

The next step is to widen the picture. Camp programs are built from
declarations, and declarations are where the language starts to show its real
shape: functions, structs, classes, interfaces, enums, newtypes, aliases,
constants, and the members that live inside them.
