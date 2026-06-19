using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed class PreprocessResult(IEnumerable<TokenValue> tokens, IReadOnlyList<ParseDiagnostic> diagnostics)
{
	public IEnumerable<TokenValue> Tokens { get; } = tokens;
	public IReadOnlyList<ParseDiagnostic> Diagnostics { get; } = diagnostics;
}

public static class CampPreprocessor
{
	public static PreprocessResult Process(TokenSequence tokens, IEnumerable<string> initialSymbols)
	{
		HashSet<string> symbols = new(initialSymbols.Where(static symbol => !string.IsNullOrWhiteSpace(symbol)), StringComparer.Ordinal);
		symbols.Add("TRUE");

		List<TokenValue> output = [];
		List<ParseDiagnostic> diagnostics = [];
		List<ConditionalFrame> stack = [];

		foreach (List<Token> line in EnumerateLines(tokens))
		{
			LineInfo info = AnalyzeLine(line);
			bool active = IsActive(stack);
			if (info.Directive is null)
			{
				if (active)
					AddLine(output, line);
				else
					AddNewLines(output, line);
				continue;
			}

			string directive = info.Directive.Value.Value;
			switch (directive)
			{
				case "define":
					if (active)
						ApplyDefine(line, info.ExpressionStart, symbols, diagnostics);
					break;

				case "undef":
					if (active)
						ApplyUndef(line, info.ExpressionStart, symbols, diagnostics);
					break;

				case "if":
					{
						bool parentActive = active;
						bool condition = parentActive && EvaluateExpression(line, info.ExpressionStart, symbols, diagnostics);
						stack.Add(new ConditionalFrame(parentActive, condition, condition));
						break;
					}

				case "elif":
					if (stack.Count == 0)
					{
						AddDiagnostic(diagnostics, info.Directive.Value, "#elif without matching #if.");
						break;
					}
					{
						ConditionalFrame frame = stack[^1];
						if (frame.SeenElse)
						{
							AddDiagnostic(diagnostics, info.Directive.Value, "#elif cannot appear after #else.");
							break;
						}
						bool condition = frame.ParentActive && !frame.BranchTaken && EvaluateExpression(line, info.ExpressionStart, symbols, diagnostics);
						stack[^1] = frame with { CurrentActive = condition, BranchTaken = frame.BranchTaken || condition };
						break;
					}

				case "else":
					if (stack.Count == 0)
					{
						AddDiagnostic(diagnostics, info.Directive.Value, "#else without matching #if.");
						break;
					}
					{
						ConditionalFrame frame = stack[^1];
						if (frame.SeenElse)
						{
							AddDiagnostic(diagnostics, info.Directive.Value, "Duplicate #else in conditional compilation block.");
							break;
						}
						bool condition = frame.ParentActive && !frame.BranchTaken;
						stack[^1] = frame with { CurrentActive = condition, BranchTaken = true, SeenElse = true };
						break;
					}

				case "endif":
					if (stack.Count == 0)
						AddDiagnostic(diagnostics, info.Directive.Value, "#endif without matching #if.");
					else
						stack.RemoveAt(stack.Count - 1);
					break;

				case "build":
					break;

				default:
					if (active)
						AddDiagnostic(diagnostics, info.Directive.Value, $"Unknown preprocessor directive '#{directive}'.");
					break;
			}

			AddNewLines(output, line);
		}

		if (stack.Count > 0)
			diagnostics.Add(new ParseDiagnostic(null, "Missing #endif for conditional compilation block."));

		return new PreprocessResult(output, diagnostics);
	}

	static bool IsActive(List<ConditionalFrame> stack)
	{
		return stack.Count == 0 || stack[^1].CurrentActive;
	}

	static void ApplyDefine(List<Token> line, int expressionStart, HashSet<string> symbols, List<ParseDiagnostic> diagnostics)
	{
		List<Token> tokens = SignificantTokens(line, expressionStart).ToList();
		if (tokens.Count != 1 || tokens[0].Class != TokenClass.Identifier)
		{
			AddDiagnostic(diagnostics, tokens.FirstOrDefault(), "#define requires a single symbol name.");
			return;
		}
		symbols.Add(tokens[0].Value);
	}

	static void ApplyUndef(List<Token> line, int expressionStart, HashSet<string> symbols, List<ParseDiagnostic> diagnostics)
	{
		List<Token> tokens = SignificantTokens(line, expressionStart).ToList();
		if (tokens.Count != 1 || tokens[0].Class != TokenClass.Identifier)
		{
			AddDiagnostic(diagnostics, tokens.FirstOrDefault(), "#undef requires a single symbol name.");
			return;
		}
		if (tokens[0].Value != "TRUE")
			symbols.Remove(tokens[0].Value);
	}

	static bool EvaluateExpression(List<Token> line, int expressionStart, HashSet<string> symbols, List<ParseDiagnostic> diagnostics)
	{
		List<Token> tokens = SignificantTokens(line, expressionStart).ToList();
		if (tokens.Count == 0)
		{
			AddDiagnostic(diagnostics, line.LastOrDefault(), "Preprocessor condition requires an expression.");
			return false;
		}

		ExpressionParser parser = new(tokens, symbols, diagnostics);
		bool value = parser.ParseOr();
		if (!parser.AtEnd)
			AddDiagnostic(diagnostics, parser.Current, "Unexpected token in preprocessor condition.");
		return value;
	}

	static IEnumerable<Token> SignificantTokens(List<Token> line, int start)
	{
		for (int i = start; i < line.Count; i++)
		{
			Token token = line[i];
			if (token.Class is TokenClass.Whitespace or TokenClass.LineComment or TokenClass.BlockComment or TokenClass.NewLine)
				continue;
			yield return token;
		}
	}

	static LineInfo AnalyzeLine(List<Token> line)
	{
		int i = 0;
		while (i < line.Count && line[i].Class is TokenClass.Whitespace or TokenClass.LineComment or TokenClass.BlockComment)
			i++;

		if (i >= line.Count || line[i].Value != "#")
			return new LineInfo(null, 0);

		i++;
		while (i < line.Count && line[i].Class == TokenClass.Whitespace)
			i++;

		if (i >= line.Count || line[i].Class != TokenClass.Identifier)
			return new LineInfo(line[Math.Max(0, i - 1)], i);

		Token directive = line[i];
		return new LineInfo(directive, i + 1);
	}

	static IEnumerable<List<Token>> EnumerateLines(TokenSequence tokens)
	{
		List<Token> line = [];
		for (int i = 0; i < tokens.Count; i++)
		{
			Token token = tokens[i];
			line.Add(token);
			if (token.Class == TokenClass.NewLine)
			{
				yield return line;
				line = [];
			}
		}
		if (line.Count > 0)
			yield return line;
	}

	static void AddLine(List<TokenValue> output, List<Token> line)
	{
		foreach (Token token in line)
			output.Add(new TokenValue(token.Value, token.Class));
	}

	static void AddNewLines(List<TokenValue> output, List<Token> line)
	{
		foreach (Token token in line)
		{
			if (token.Class == TokenClass.NewLine)
				output.Add(new TokenValue(token.Value, token.Class));
		}
	}

	static void AddDiagnostic(List<ParseDiagnostic> diagnostics, Token token, string message)
	{
		diagnostics.Add(new ParseDiagnostic(token.Range, message));
	}

	static void AddDiagnostic(List<ParseDiagnostic> diagnostics, Token? token, string message)
	{
		diagnostics.Add(new ParseDiagnostic(token?.Range, message));
	}

	readonly record struct ConditionalFrame(bool ParentActive, bool CurrentActive, bool BranchTaken, bool SeenElse = false);
	readonly record struct LineInfo(Token? Directive, int ExpressionStart);

	sealed class ExpressionParser(List<Token> tokens, HashSet<string> symbols, List<ParseDiagnostic> diagnostics)
	{
		int index;

		public bool AtEnd => index >= tokens.Count;
		public Token? Current => AtEnd ? null : tokens[index];

		public bool ParseOr()
		{
			bool value = ParseAnd();
			while (Match("||"))
				value = ParseAnd() || value;
			return value;
		}

		bool ParseAnd()
		{
			bool value = ParseUnary();
			while (Match("&&"))
				value = ParseUnary() && value;
			return value;
		}

		bool ParseUnary()
		{
			if (Match("!"))
				return !ParseUnary();
			return ParsePrimary();
		}

		bool ParsePrimary()
		{
			if (Match("("))
			{
				bool value = ParseOr();
				if (!Match(")"))
					AddDiagnostic(diagnostics, Current, "Expected ')' in preprocessor condition.");
				return value;
			}

			if (Current is Token { Class: TokenClass.Identifier } token)
			{
				index++;
				return token.Value == "TRUE" || symbols.Contains(token.Value);
			}

			AddDiagnostic(diagnostics, Current, "Expected symbol name in preprocessor condition.");
			if (!AtEnd)
				index++;
			return false;
		}

		bool Match(string value)
		{
			if (value is "&&" or "||")
			{
				string part = value[0].ToString();
				if (index + 1 >= tokens.Count || tokens[index].Value != part || tokens[index + 1].Value != part)
					return false;
				index += 2;
				return true;
			}
			if (AtEnd || tokens[index].Value != value)
				return false;
			index++;
			return true;
		}
	}
}
