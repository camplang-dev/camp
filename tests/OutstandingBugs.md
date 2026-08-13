# Outstanding Bugs

Next bug number: BUG-078.

## BUG-077: Ternary char-array literal passed to interface writer method can emit too few C arguments

Status: Open.

Summary: Passing a ternary expression whose branches are char-array/string literals directly to an interface method expecting `const char[]` can bind successfully but emit invalid C that omits the length argument.

Minimal repro shape:

```camp
void writeSeparator(CharWriter writer, bool compact)
{
	writer.write(compact ? ":" : ": ");
}

export int main(string[] args)
{
	auto buffer = new List<char>() finally delete;
	writeSeparator(buffer.CharWriter, false);
	char[] text = buffer.copyArray() finally delete;
	return text.compareTo(": ") == 0 ? 0 : 1;
}
```

Repro instructions:

1. Create a temporary executable project using the standard library.
2. Add the source above.
3. Run `campc build`.
4. The compiler reaches native C compilation and emits an interface call with the ternary expression as a single pointer argument, but without the companion length argument.

Expected behavior: The ternary expression should lower as a complete `const char[]` value, preserving both pointer and length when passed through the interface call.

Workaround: Split the branch before the call and call the writer separately in each branch.
