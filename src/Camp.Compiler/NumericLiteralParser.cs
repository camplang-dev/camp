using System;
using System.Globalization;
using System.Numerics;

namespace Camp.Compiler;

static class NumericLiteralParser
{
	public static bool TryParseIntegerMagnitude(string text, out BigInteger magnitude)
	{
		return TryParseIntegerMagnitude(text, out magnitude, out _);
	}

	public static bool TryParseIntegerMagnitude(string text, out BigInteger magnitude, out bool unsignedSuffix)
	{
		magnitude = BigInteger.Zero;
		unsignedSuffix = false;
		if (string.IsNullOrWhiteSpace(text)
			|| text.Contains('.', StringComparison.Ordinal)
			|| text.Contains('p', StringComparison.OrdinalIgnoreCase)
			|| text.Contains('e', StringComparison.OrdinalIgnoreCase))
			return false;

		string coreText = text.Replace("_", "", StringComparison.Ordinal).Trim();
		if (coreText.Length == 0)
			return false;

		while (coreText.Length > 0 && coreText[^1] is 'u' or 'U' or 'l' or 'L')
		{
			if (coreText[^1] is 'u' or 'U')
				unsignedSuffix = true;
			coreText = coreText[..^1];
		}

		int radix = 10;
		int start = 0;
		if (coreText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			radix = 16;
			start = 2;
		}
		else if (coreText.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
		{
			radix = 2;
			start = 2;
		}

		if (start >= coreText.Length)
			return false;

		for (int i = start; i < coreText.Length; i++)
		{
			int digit = GetDigit(coreText[i]);
			if (digit < 0 || digit >= radix)
				return false;

			magnitude = magnitude * radix + digit;
		}

		return true;
	}

	public static bool TryParseIntegerConstant(string text, out BigInteger value)
	{
		if (TryParseIntegerMagnitude(text, out value))
			return true;
		value = BigInteger.Zero;
		return BigInteger.TryParse(text.Replace("_", "", StringComparison.Ordinal).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
	}

	static int GetDigit(char ch)
	{
		return ch switch
		{
			>= '0' and <= '9' => ch - '0',
			>= 'a' and <= 'f' => ch - 'a' + 10,
			>= 'A' and <= 'F' => ch - 'A' + 10,
			_ => -1
		};
	}
}
