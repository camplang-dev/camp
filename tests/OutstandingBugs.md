# Outstanding Bugs

Next bug number: BUG-085.

## BUG-084: Array return with `thrown` lowers call-site hidden parameters in the wrong order

Status: open

Observed with the development compiler.

Minimal repro:

```camp
namespace Repro;

public enum ReadError: int
{
	OK = 0,
	FAILED = 1,
}

byte[] readBytes(thrown ReadError error)
{
	byte[] bytes = new byte[1];
	bytes[0] = 42;
	return bytes;
}

export int main()
{
	ReadError error = default;
	byte[] bytes = readBytes(catch error);
	if (error != default)
		return 1;
	if (bytes.length != 1 || bytes[0] != 42)
		return 2;
	delete bytes;
	return 0;
}
```

Expected behavior:

- The call site should pass the hidden array-length/result slot and the thrown
  error slot in the same order expected by the lowered callee.
- The program should return `0`.

Observed behavior:

- The lowered callee signature places the thrown error pointer before the hidden
  array-length result pointer, while the call site passes the hidden length
  pointer before the thrown error pointer.
- The callee writes the array length through the error pointer and writes the
  thrown error result through the array-length pointer, corrupting both values.

Workaround:

- Avoid returning array views directly from functions that also use `thrown`.
  Use an explicit success/failure return with `out byte[]` and `out Error`
  slots, or otherwise separate the array result from the thrown channel.
