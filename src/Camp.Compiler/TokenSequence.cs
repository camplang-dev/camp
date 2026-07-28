using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Camp.Compiler;

public readonly record struct LinePosition(int LineNumber, int Column);

public class TokenSequence : IReadOnlyList<Token>
{
	readonly List<TokenValue> values = [];
	readonly List<LinePosition> linePositions = [];

	public TokenSequence(IEnumerable<TokenValue> values)
		: this(values, 1, 1)
	{
	}

	public TokenSequence(IEnumerable<TokenValue> values, int startLineNumber, int startColumn)
	{
		ArgumentNullException.ThrowIfNull(values);

		int lineNumber = startLineNumber;
		int column = startColumn;

		foreach (TokenValue value in values)
		{
			this.values.Add(value);
			linePositions.Add(new LinePosition(lineNumber, column));

			if (CanContainLineBreak(value.Class))
				AdvanceAcrossPossibleLineBreaks(value.Value, ref lineNumber, ref column);
			else
				column += value.Value.Length;
		}
	}

	public IReadOnlyList<TokenValue> Values => values;
	public IReadOnlyList<LinePosition> LinePositions => linePositions;
	public int Count => values.Count;

	public Token this[int index] => new(this, index);

	public string GetValue(int index) => values[index].Value;

	public string GetValue(int index, int count)
	{
		StringBuilder builder = new();

		for (int i = 0; i < count; i++)
			builder.Append(values[index + i].Value);

		return builder.ToString();
	}

	public int GetValueLength(int index, int count)
	{
		int length = 0;

		for (int i = 0; i < count; i++)
			length += values[index + i].Value.Length;

		return length;
	}

	public TokenClass GetClass(int index) => values[index].Class;

	public int GetLineNumber(int index) => linePositions[index].LineNumber;

	public int GetColumn(int index) => linePositions[index].Column;

	public int GetEndLineNumber(int index, int count)
	{
		int lineNumber = GetLineNumber(index);
		int column = GetColumn(index);

		AdvanceAcrossRange(index, count, ref lineNumber, ref column);

		return lineNumber;
	}

	public int GetEndColumn(int index, int count)
	{
		int lineNumber = GetLineNumber(index);
		int column = GetColumn(index);

		AdvanceAcrossRange(index, count, ref lineNumber, ref column);

		return column;
	}

	public IEnumerator<Token> GetEnumerator()
	{
		for (int i = 0; i < Count; i++)
			yield return this[i];
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	static bool CanContainLineBreak(TokenClass tokenClass)
	{
		return tokenClass is TokenClass.NewLine or TokenClass.BlockComment;
	}

	void AdvanceAcrossRange(int index, int count, ref int lineNumber, ref int column)
	{
		for (int i = 0; i < count; i++)
			AdvanceAcrossPossibleLineBreaks(values[index + i].Value, ref lineNumber, ref column);
	}

	static void AdvanceAcrossPossibleLineBreaks(string value, ref int lineNumber, ref int column)
	{
		for (int i = 0; i < value.Length; i++)
		{
			switch (value[i])
			{
				case '\r':
					if (i + 1 < value.Length && value[i + 1] == '\n')
						i++;

					lineNumber++;
					column = 1;
					break;

				case '\n':
					lineNumber++;
					column = 1;
					break;

				default:
					column++;
					break;
			}
		}
	}
}

public readonly record struct Token(TokenSequence Sequence, int Index)
{
	public string Value => Sequence.GetValue(Index);
	public TokenClass Class => Sequence.GetClass(Index);
	public int LineNumber => Sequence.GetLineNumber(Index);
	public int Column => Sequence.GetColumn(Index);
	public TokenRange Range => new(Sequence, Index, 1);
}

public readonly record struct TokenRange(TokenSequence Sequence, int Index, int Count)
{
	public string Value => Sequence.GetValue(Index, Count);
	public int ValueLength => Sequence.GetValueLength(Index, Count);
	public int StartLineNumber => Sequence.GetLineNumber(Index);
	public int StartColumn => Sequence.GetColumn(Index);
	public int EndLineNumber => Sequence.GetEndLineNumber(Index, Count);
	public int EndColumn => Sequence.GetEndColumn(Index, Count);
}
