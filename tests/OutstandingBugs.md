# Outstanding Bugs

Next bug number: BUG-059.

## BUG-054: `const char[] this` callable ascription lowering still mishandles expanded array operations

An extension method such as:

```camp
public nuint escapedLength(const char[] this, overload char[] buffer) : StringFormatter
{
	return this.length + 1;
}
```

can be accepted as a `StringFormatter`, but using it as a formatter still has lowering holes. Direct calls with expanded array parameters now pass the pointer/length arguments, but compiling fuller formatter bodies can stack overflow in `TryCreateIndexedParamsComponentExpressions` when an expanded array parameter is indexed. The intended shape is still:

```camp
StringFormatter formatter = "abc".escapedLength;
formatter();
```

and:

```camp
string copied = "abc".escapedLength.copyString() finally delete;
```

Both should eventually materialize the expanded `const char[]` receiver into a delegate context and invoke the formatter through an adapter without emitting raw `&"literal"` context pointers.

## BUG-056: Interpolation formatter capture re-evaluates receiver chains with side effects

Given:

```camp
export void main()
{
	Console.writeLine($"Result: {Greeter.instance().action1().action2().complete()}");
}

public struct Greeter
{
	public static Greeter instance()
	{
		Console.writeLine("instance");
		return default;
	}

	public Greeter action1()
	{
		Console.writeLine("action1");
		return *this;
	}

	public Greeter action2()
	{
		Console.writeLine("action2");
		return *this;
	}

	public string complete()
	{
		Console.writeLine("complete");
		return "complete";
	}
}
```

the current output is:

```text
instance
instance
instance
action1
action1
action1
action2
action2
action2
complete
Result: complete
```

`instance()`, `action1()`, `action2()`, and `complete()` should each be evaluated exactly once, in source order, before the interpolated string formatter is produced. Runtime interpolation is allowed to call component formatter delegates more than once for sizing and writing, but it must not re-evaluate source expressions that produce those formatter components.

This likely belongs in interpolation lowering and callable-ascription receiver materialization. In particular, receiver expressions used to form formatter delegates must be captured into generated locals before the formatter is invoked for size and output.

## BUG-057: Generated interpolation formatter bodies cannot see same-file private members

The same repro in `BUG-056` fails if `Greeter` and its methods are left private:

```camp
struct Greeter
{
	static Greeter instance() { return default; }
	Greeter action1() { return *this; }
	Greeter action2() { return *this; }
	string complete() { return "complete"; }
}
```

Current diagnostic:

```text
error: Member 'instance' is declared in another file but is not exported.
```

The interpolation expression appears in the same source file and should be able to use same-file private declarations exactly as the surrounding source expression can. Generated formatter/lambda bodies produced for interpolation should preserve the source visibility context of the interpolation expression.

This is related to generated-code reanalysis and source visibility. The compiler should not require declarations to be `public`, `internal`, or `extern` merely because they are referenced from inside an interpolation expression.

## BUG-058: Interpolation emits one assignment per constant text character

Interpolated string lowering currently emits one character assignment and one offset increment for every character in constant text segments. For:

```camp
Console.writeLine($"Result: {value}");
```

the emitted C for the literal prefix is shaped like:

```c
buffer[_interpolatedOffset14] = 'R';
_interpolatedOffset14 = (_interpolatedOffset14 + 1);
buffer[_interpolatedOffset14] = 'e';
_interpolatedOffset14 = (_interpolatedOffset14 + 1);
buffer[_interpolatedOffset14] = 's';
_interpolatedOffset14 = (_interpolatedOffset14 + 1);
```

This is correct but scales poorly. Longer literal segments can generate kilobytes of C for a single interpolation expression. Literal text segments should be emitted through a compact block-copy operation, or through an internal helper that copies a known byte sequence into the target buffer and advances the offset once.

This likely belongs in `BuildInterpolatedFormatterBody`/`AddLiteralWrites`. The replacement should preserve the existing formatter contract, null terminator behavior, bounds check behavior, and support for the formatter element type.
