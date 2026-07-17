# Outstanding Bugs

Next bug number: BUG-054.

## BUG-052: Nested aggregate initializers for expanded fields can type-check but emit invalid C

Nested aggregate initializers involving expanded array fields can type-check but
emit invalid C, often as scalar initializers or with omitted expanded
components.

Minimal repro:

```camp
struct SliceBox
{
	const char[] text;
}

struct TaggedSlice
{
	int tag;
	const char[] text;
}

TaggedSlice makeTagged(const char[] source, nuint count)
{
	return { 7, { source.elements, count } };
}

struct iter SliceBox chunks(const char[] source)
{
	yield { { source.elements, source.length } };
}

export int main()
{
	const char[] source = "abc";
	TaggedSlice tagged = makeTagged(source, 2);
	if (tagged.tag != 7 || tagged.text.length != 2)
		return 1;

	int count = 0;
	foreach (SliceBox box in chunks(source))
	{
		if (box.text.length != 3)
			return 2;
		count++;
	}

	return count == 1 ? 0 : 3;
}
```

Actual: source can pass semantic analysis, but C emission/native compilation can
produce scalar initializer warnings/errors such as "excess elements in scalar
initializer", pointer-to-integer conversion errors, or missing length component
handling.

Expected: nested aggregate initializers for expanded fields should initialize
all expanded components correctly in every aggregate position, including returns
and yields. If this initializer shape is unsupported, source should be rejected
with a clear diagnostic before C emission.

Workaround: add builder helpers that assign fields one at a time, such as
`makeSliceBox(...)` or `makeTaggedSlice(...)`.

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
