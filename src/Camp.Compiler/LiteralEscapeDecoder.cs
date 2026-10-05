using System;
using System.Text;

namespace Camp.Compiler;

static class LiteralEscapeDecoder
{
	public static bool TryDecode(ReadOnlySpan<char> text, out string value, bool allowSurrogateEscapes = false)
	{
		StringBuilder builder = new();
		bool valid = true;
		for (int i = 0; i < text.Length; i++)
		{
			char ch = text[i];
			if (ch != '\\')
			{
				builder.Append(ch);
				continue;
			}
			if (++i == text.Length)
			{
				valid = false;
				break;
			}
			switch (text[i])
			{
				case '\'': case '"': case '\\': builder.Append(text[i]); break;
				case '0': builder.Append('\0'); break;
				case 'a': builder.Append('\a'); break;
				case 'b': builder.Append('\b'); break;
				case 'e': builder.Append((char)27); break;
				case 'f': builder.Append('\f'); break;
				case 'n': builder.Append('\n'); break;
				case 'r': builder.Append('\r'); break;
				case 't': builder.Append('\t'); break;
				case 'v': builder.Append('\v'); break;
				case 'x': case 'u': case 'U':
				{
					char escape = text[i];
					int maximum = escape == 'U' ? 8 : 4;
					int digits = 0;
					uint scalar = 0;
					while (digits < maximum && i + 1 < text.Length && HexValue(text[i + 1]) is int digit && digit >= 0)
					{
						scalar = scalar * 16 + (uint)digit;
						i++;
						digits++;
					}
					bool surrogate = scalar is >= 0xD800 and <= 0xDFFF;
					if (digits == 0 || (escape != 'x' && digits != maximum) || scalar > 0x10FFFF
						|| (surrogate && (escape == 'U' || !allowSurrogateEscapes)))
						valid = false;
					else if (surrogate)
						builder.Append((char)scalar);
					else
						builder.Append(char.ConvertFromUtf32((int)scalar));
					break;
				}
				default: valid = false; break;
			}
		}
		value = builder.ToString();
		return valid;
	}

	static int HexValue(char ch) => ch switch
	{
		>= '0' and <= '9' => ch - '0',
		>= 'a' and <= 'f' => ch - 'a' + 10,
		>= 'A' and <= 'F' => ch - 'A' + 10,
		_ => -1
	};
}
