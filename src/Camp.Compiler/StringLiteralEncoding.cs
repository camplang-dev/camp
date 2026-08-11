using System.Text;

namespace Camp.Compiler;

static class StringLiteralEncoding
{
	static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	public static int GetElementCount(string value, string elementType)
	{
		elementType = StripTopLevelValueQualifiers(elementType);
		return elementType switch
		{
			"char" => Utf8NoBom.GetByteCount(value),
			"wchar" => Encoding.Unicode.GetByteCount(value) / sizeof(char),
			"achar" => Encoding.ASCII.GetByteCount(value),
			_ => value.Length
		};
	}

	public static byte[] GetBytes(string value, string elementType)
	{
		elementType = StripTopLevelValueQualifiers(elementType);
		return elementType switch
		{
			"char" => Utf8NoBom.GetBytes(value),
			"achar" => Encoding.ASCII.GetBytes(value),
			_ => Utf8NoBom.GetBytes(value)
		};
	}

	public static ushort[] GetUtf16Units(string value)
	{
		byte[] bytes = Encoding.Unicode.GetBytes(value);
		ushort[] units = new ushort[bytes.Length / sizeof(ushort)];
		for (int i = 0; i < units.Length; i++)
			units[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
		return units;
	}

	static string StripTopLevelValueQualifiers(string type)
	{
		type = type.Trim();
		while (true)
		{
			if (type.StartsWith("const ", System.StringComparison.Ordinal))
				type = type["const ".Length..].TrimStart();
			else if (type.StartsWith("volatile ", System.StringComparison.Ordinal))
				type = type["volatile ".Length..].TrimStart();
			else
				return type;
		}
	}
}
