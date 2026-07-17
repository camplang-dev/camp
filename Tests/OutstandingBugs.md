# Outstanding Bugs

Next bug number: BUG-054.

## BUG-053: Expanded-return calls inline inside initializers or calls can emit missing result slots

Using expanded-return expressions directly inside aggregate initializers or as
call arguments can generate C calls that omit the hidden result component
out-parameters.

Minimal repro:

```camp
using Std;

struct SliceBox
{
	const char[] text;
}

const char[] firstTwo(const char[] source)
{
	return source[..2];
}

SliceBox makeBoxInline(const char[] source)
{
	return { firstTwo(source) };
}

nuint lengthOf(const char[] value)
{
	return value.length;
}

nuint callWithInlineExpandedReturn(const char[] source)
{
	return lengthOf(firstTwo(source));
}

export int main()
{
	const char[] source = "abcd";

	SliceBox box = makeBoxInline(source);
	if (box.text.length != 2)
		return 1;

	if (callWithInlineExpandedReturn(source) != 2)
		return 2;

	return 0;
}
```

Actual: generated C can call expanded-return functions without supplying the
required hidden result component storage, e.g. calling a lowered
`firstTwo(source)` function with too few arguments.

Expected: expanded-return calls should be lowered correctly in expression
contexts, including aggregate initializers and call arguments, or source should
diagnose unsupported contexts.

Workaround: store expanded-return values in locals or assign fields explicitly
before using them.
