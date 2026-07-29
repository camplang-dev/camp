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
