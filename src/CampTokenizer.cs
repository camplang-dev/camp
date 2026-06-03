using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public readonly record struct TokenValue(string Value, TokenClass Class);

public enum TokenClass
{
	Identifier,
	AttributeIdentifier,
	Number,
	String,
	Symbol,
	LineComment,
	BlockComment,
	Whitespace,
	NewLine,
	Invalid
}

public static class CampTokenizer
{
	const string Punctuation = "~!%^&*()+-={}[]|;:,./<>?$";

	public static IEnumerable<TokenValue> Tokenize(string text)
	{
		ArgumentNullException.ThrowIfNull(text);

		bool inBlockComment = false;
		int i = 0;

		while (i < text.Length)
		{
			int start = i;
			char c = text[i];

			if (inBlockComment)
			{
				i = ReadBlockCommentLine(text, i, ref inBlockComment);
				yield return new TokenValue(text[start..i], TokenClass.BlockComment);
				continue;
			}

			switch (c)
			{
				case '\r' or '\n':
					i = ReadNewLine(text, i);
					yield return new TokenValue(text[start..i], TokenClass.NewLine);
					break;

				case ' ' or '\t' or '\v' or '\f':
					while (i < text.Length && IsHorizontalWhiteSpace(text[i]))
						i++;

					yield return new TokenValue(text[start..i], TokenClass.Whitespace);
					break;

				case '/' when Peek(text, i + 1) == '/':
					i += 2;
					while (i < text.Length && !IsNewLineStart(text, i))
						i++;

					yield return new TokenValue(text[start..i], TokenClass.LineComment);
					break;

				case '/' when Peek(text, i + 1) == '*':
					i = ReadBlockCommentLine(text, i, ref inBlockComment);
					yield return new TokenValue(text[start..i], TokenClass.BlockComment);
					break;

				case '"' or '\'' or '`':
					i = ReadString(text, i, c);
					yield return new TokenValue(text[start..i], TokenClass.String);
					break;

				case '@' when IsIdentifierStart(Peek(text, i + 1)):
					i += 2;
					while (i < text.Length && IsIdentifierPart(text[i]))
						i++;

					yield return new TokenValue(text[start..i], TokenClass.AttributeIdentifier);
					break;

				case >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_':
					i++;
					while (i < text.Length && IsIdentifierPart(text[i]))
						i++;

					yield return new TokenValue(text[start..i], TokenClass.Identifier);
					break;

				case >= '0' and <= '9':
					i = ReadNumber(text, i);
					yield return new TokenValue(text[start..i], TokenClass.Number);
					break;

				case '~' or '!' or '%' or '^' or '&' or '*' or '(' or ')' or '+' or '-' or '=' or '{' or '}' or '[' or ']' or '|' or ';' or ':' or ',' or '.' or '/' or '<' or '>' or '?' or '$' or '#':
					i++;
					yield return new TokenValue(text[start..i], TokenClass.Symbol);
					break;

				default:
					i++;
					while (i < text.Length && !IsNewLineStart(text, i) && !IsHorizontalWhiteSpace(text[i]) && !IsPunctuation(text[i]))
						i++;

					yield return new TokenValue(text[start..i], TokenClass.Invalid);
					break;
			}
		}
	}

	static int ReadString(string text, int i, char quote)
	{
		i++;

		while (i < text.Length && !IsNewLineStart(text, i))
		{
			char c = text[i++];

			if (c == quote)
				break;

			if (c == '\\' && i < text.Length && !IsNewLineStart(text, i))
				i++;
		}

		return i;
	}

	static int ReadNumber(string text, int i)
	{
		if (text[i] == '0' && (Peek(text, i + 1) == 'x' || Peek(text, i + 1) == 'X'))
		{
			i += 2;
			while (i < text.Length && IsAsciiLetterOrDigit(text[i]))
				i++;

			return i;
		}

		bool sawDecimal = false;

		while (i < text.Length)
		{
			char c = text[i];

			switch (c)
			{
				case >= '0' and <= '9':
					i++;
					break;

				case '.' when !sawDecimal && IsDigit(Peek(text, i + 1)):
					sawDecimal = true;
					i++;
					break;

				default:
					goto done;
			}
		}

	done:
		while (i < text.Length && IsAsciiLetter(text[i]))
			i++;

		return i;
	}

	static int ReadBlockCommentLine(string text, int i, ref bool inBlockComment)
	{
		inBlockComment = true;

		if (IsNewLineStart(text, i))
			return ReadNewLine(text, i);

		while (i < text.Length && !IsNewLineStart(text, i))
		{
			if (text[i] == '*' && Peek(text, i + 1) == '/')
			{
				i += 2;
				inBlockComment = false;
				break;
			}

			i++;
		}

		return i;
	}

	static int ReadNewLine(string text, int i)
	{
		return text[i] == '\r' && Peek(text, i + 1) == '\n'
			? i + 2
			: i + 1;
	}

	static bool IsNewLineStart(string text, int i)
	{
		return i < text.Length && (text[i] == '\r' || text[i] == '\n');
	}

	static bool IsHorizontalWhiteSpace(char c)
	{
		return c is ' ' or '\t' or '\v' or '\f';
	}

	static bool IsIdentifierStart(char c)
	{
		return c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
	}

	static bool IsIdentifierPart(char c)
	{
		return IsIdentifierStart(c) || IsDigit(c);
	}

	static bool IsDigit(char c)
	{
		return c is >= '0' and <= '9';
	}

	static bool IsAsciiLetter(char c)
	{
		return c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
	}

	static bool IsAsciiLetterOrDigit(char c)
	{
		return IsAsciiLetter(c) || IsDigit(c);
	}

	static bool IsPunctuation(char c)
	{
		return Punctuation.IndexOf(c) >= 0;
	}

	static char Peek(string text, int i)
	{
		return i < text.Length ? text[i] : '\0';
	}
}
