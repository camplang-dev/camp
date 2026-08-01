# Outstanding Bugs

Next bug number: BUG-068.

## BUG-067: Interpolation holes do not implicitly bind `prep` property getters

### Summary

Interpolation holes implicitly use prep methods when the method provides the
hole result. This works for ordinary calls such as `$"{join(values)}"`, but it
does not work for property getter or indexed property getter syntax where the
getter has a `prep` parameter.

This is inconsistent with both prep property access and the interpolation
diagnostic for a bare `prep` prefix. `$"{prep value.Text}"` is rejected with a
message telling the user to remove `prep`, but `$"{value.Text}"` then fails
because the member is not found.

### General Repro

```camp
using Std;

export int main()
{
	Label label = { .prefix = "Item" };
	Console.writeLine($"text={label.Text}");
	Console.writeLine($"item={label.Item[3]}");
	return 0;
}

struct Label
{
	string prefix;

	nuint getText(this, prep char[] buffer = default)
	{
		return writeJoined(this.prefix, 0, buffer);
	}

	nuint getItem(this, @index int index, prep char[] buffer = default)
	{
		return writeJoined(this.prefix, index, buffer);
	}
}

nuint writeJoined(string prefix, int index, prep char[] buffer = default)
{
	const char[] text = prefix;
	nuint required = text.length + 2;
	for (nuint i = 0; i < text.length && i < buffer.length; i++)
		buffer[i] = text[i];
	if (text.length < buffer.length)
		buffer[text.length] = '-';
	if (text.length + 1 < buffer.length)
		buffer[text.length + 1] = (char)('0' + index);
	return required;
}
```

Run:

```bash
bin/campc run repro.camp
```

### Current Behavior

Reproduced on macOS. The plain property hole fails to bind:

```text
error: Member 'Text' could not be found on type 'Label'.
error: Cannot select overload `writeLine` because the selector expression has no independent static type. Add an explicit cast.
error: Multiple candidates found for member call 'writeLine'.
```

The explicit bare-prep form also does not provide a usable workaround:

```camp
Console.writeLine($"text={prep label.Text}");
```

That form is rejected with the redundant-prep diagnostic:

```text
error: Interpolation holes already use prep methods implicitly when the method provides the hole result; remove the redundant 'prep' prefix.
```

Parenthesized explicit prep does work:

```camp
Console.writeLine($"text={(prep label.Text)}");
```

### Expected Behavior

For a bare interpolation hole, member/property binding should consider prep
property getters and indexed prep property getters as eligible producers of the
hole text, the same way ordinary prep-producing calls are eligible. The sample
should print:

```text
text=Item-0
item=Item-3
```
