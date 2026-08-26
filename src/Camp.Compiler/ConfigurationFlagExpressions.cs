using System;
using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public enum ConfigurationFlagExpressionKind
{
	Flag,
	Literal,
	Not,
	And,
	Or,
	Xor
}

public sealed class ConfigurationFlagExpression
{
	public ConfigurationFlagExpressionKind Kind { get; init; }
	public string? FlagName { get; init; }
	public bool LiteralValue { get; init; }
	public ConfigurationFlagExpression? Left { get; init; }
	public ConfigurationFlagExpression? Right { get; init; }

	public bool Evaluate(ConfigurationFlagSet flags) =>
		Kind switch
		{
			ConfigurationFlagExpressionKind.Flag => FlagName is not null && flags.IsConfiguredTrue(FlagName),
			ConfigurationFlagExpressionKind.Literal => LiteralValue,
			ConfigurationFlagExpressionKind.Not => Left is not null && !Left.Evaluate(flags),
			ConfigurationFlagExpressionKind.And => Left is not null && Right is not null && Left.Evaluate(flags) && Right.Evaluate(flags),
			ConfigurationFlagExpressionKind.Or => Left is not null && Right is not null && (Left.Evaluate(flags) || Right.Evaluate(flags)),
			ConfigurationFlagExpressionKind.Xor => Left is not null && Right is not null && Left.Evaluate(flags) ^ Right.Evaluate(flags),
			_ => false
		};

	public override string ToString() =>
		Kind switch
		{
			ConfigurationFlagExpressionKind.Flag => FlagName ?? "",
			ConfigurationFlagExpressionKind.Literal => LiteralValue ? "true" : "false",
			ConfigurationFlagExpressionKind.Not => "!" + Parenthesize(Left),
			ConfigurationFlagExpressionKind.And => Parenthesize(Left) + " && " + Parenthesize(Right),
			ConfigurationFlagExpressionKind.Or => Parenthesize(Left) + " || " + Parenthesize(Right),
			ConfigurationFlagExpressionKind.Xor => Parenthesize(Left) + " ^ " + Parenthesize(Right),
			_ => ""
		};

	static string Parenthesize(ConfigurationFlagExpression? expression) =>
		expression is null
			? ""
			: expression.Kind is ConfigurationFlagExpressionKind.Flag or ConfigurationFlagExpressionKind.Literal or ConfigurationFlagExpressionKind.Not
				? expression.ToString()
				: "(" + expression + ")";
}

public static class ConfigurationFlagExpressionBinder
{
	public static ConfigurationFlagExpression? And(ConfigurationFlagExpression? left, ConfigurationFlagExpression? right)
	{
		left = Normalize(left);
		right = Normalize(right);
		if (left is null)
			return right;
		if (right is null)
			return left;
		if (IsLiteral(left, false) || IsLiteral(right, false))
			return False();
		if (IsLiteral(left, true))
			return right;
		if (IsLiteral(right, true))
			return left;
		return new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.And, Left = left, Right = right };
	}

	public static ConfigurationFlagExpression? Normalize(ConfigurationFlagExpression? expression) =>
		expression is null || IsLiteral(expression, true) ? null : expression;

	static bool IsLiteral(ConfigurationFlagExpression expression, bool value) =>
		expression.Kind == ConfigurationFlagExpressionKind.Literal && expression.LiteralValue == value;

	public static ConfigurationFlagExpression Flag(string name) =>
		new() { Kind = ConfigurationFlagExpressionKind.Flag, FlagName = name };

	public static ConfigurationFlagExpression True() =>
		new() { Kind = ConfigurationFlagExpressionKind.Literal, LiteralValue = true };

	public static ConfigurationFlagExpression Or(ConfigurationFlagExpression? left, ConfigurationFlagExpression? right)
	{
		if (left is null)
			return right ?? False();
		if (right is null)
			return left;
		return new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Or, Left = left, Right = right };
	}

	public static ConfigurationFlagExpression False() =>
		new() { Kind = ConfigurationFlagExpressionKind.Literal, LiteralValue = false };

	public static bool TryParse(string text, ConfigurationFlagSet flags, Action<string> report, out ConfigurationFlagExpression? expression)
	{
		List<TokenValue> tokens = CampTokenizer.Tokenize(text)
			.Where(static token => token.Class is not TokenClass.Whitespace and not TokenClass.NewLine and not TokenClass.LineComment and not TokenClass.BlockComment)
			.ToList();
		FlagExpressionTextParser parser = new(tokens, flags, report);
		if (!parser.TryParseExpression(out expression))
			return false;
		if (!parser.IsEnd)
		{
			report($"Unexpected token '{parser.CurrentValue}' in configuration flag expression.");
			expression = null;
			return false;
		}
		return true;
	}

	public static bool Implies(ConfigurationFlagExpression? context, ConfigurationFlagExpression required, ConfigurationFlagSet flags)
	{
		if (required.Evaluate(flags))
			return true;
		if (context is null)
			return false;

		HashSet<string> names = new(StringComparer.Ordinal);
		CollectFlagNames(context, names);
		CollectFlagNames(required, names);
		string[] flagNames = [.. names.Order(StringComparer.Ordinal)];
		if (flagNames.Length > 20)
			return false;
		Dictionary<string, bool> assignment = new(StringComparer.Ordinal);
		return !HasCounterexample(0);

		bool HasCounterexample(int index)
		{
			if (index == flagNames.Length)
				return Evaluate(context, assignment, flags) && !Evaluate(required, assignment, flags);
			string name = flagNames[index];
			assignment[name] = false;
			if (HasCounterexample(index + 1))
				return true;
			assignment[name] = true;
			if (HasCounterexample(index + 1))
				return true;
			assignment.Remove(name);
			return false;
		}
	}

	public static void CollectFlagNames(ConfigurationFlagExpression? expression, HashSet<string> names)
	{
		if (expression is null)
			return;
		if (expression.Kind == ConfigurationFlagExpressionKind.Flag && expression.FlagName is not null)
			names.Add(expression.FlagName);
		CollectFlagNames(expression.Left, names);
		CollectFlagNames(expression.Right, names);
	}

	static bool Evaluate(ConfigurationFlagExpression expression, IReadOnlyDictionary<string, bool> assignment, ConfigurationFlagSet fallback) =>
		expression.Kind switch
		{
			ConfigurationFlagExpressionKind.Flag => expression.FlagName is not null
				&& (assignment.TryGetValue(expression.FlagName, out bool value) ? value : fallback.IsConfiguredTrue(expression.FlagName)),
			ConfigurationFlagExpressionKind.Literal => expression.LiteralValue,
			ConfigurationFlagExpressionKind.Not => expression.Left is not null && !Evaluate(expression.Left, assignment, fallback),
			ConfigurationFlagExpressionKind.And => expression.Left is not null && expression.Right is not null && Evaluate(expression.Left, assignment, fallback) && Evaluate(expression.Right, assignment, fallback),
			ConfigurationFlagExpressionKind.Or => expression.Left is not null && expression.Right is not null && (Evaluate(expression.Left, assignment, fallback) || Evaluate(expression.Right, assignment, fallback)),
			ConfigurationFlagExpressionKind.Xor => expression.Left is not null && expression.Right is not null && Evaluate(expression.Left, assignment, fallback) ^ Evaluate(expression.Right, assignment, fallback),
			_ => false
		};

	public static bool TryBind(Expression? expression, ConfigurationFlagSet flags, Action<TokenRange?, string> report, out ConfigurationFlagExpression? result)
	{
		result = null;
		if (expression is null)
		{
			report(null, "Configuration flag expression is missing.");
			return false;
		}

		switch (expression)
		{
			case ParenthesizedExpression parenthesized:
				return TryBind(parenthesized.Expression, flags, report, out result);

			case LiteralExpression { Kind: LiteralKind.True }:
				result = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Literal, LiteralValue = true };
				return true;

			case LiteralExpression { Kind: LiteralKind.False }:
				result = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Literal, LiteralValue = false };
				return true;

			case NamedExpression { Qualifiers.Count: 0 } named:
				if (!flags.Declarations.ContainsKey(named.Name))
				{
					report(GetRange(named), $"Unknown configuration flag '{named.Name}'.");
					return false;
				}
				result = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Flag, FlagName = named.Name };
				return true;

			case UnaryExpression { Operator: UnaryOperator.LogicalNot } unary:
				if (!TryBind(unary.Operand, flags, report, out ConfigurationFlagExpression? operand))
					return false;
				result = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Not, Left = operand };
				return true;

			case BinaryExpression binary:
				ConfigurationFlagExpressionKind kind = binary.Operator switch
				{
					BinaryOperator.LogicalAnd => ConfigurationFlagExpressionKind.And,
					BinaryOperator.LogicalOr => ConfigurationFlagExpressionKind.Or,
					BinaryOperator.BitwiseXor => ConfigurationFlagExpressionKind.Xor,
					_ => 0
				};
				if (kind == 0)
				{
					report(GetRange(binary), "Configuration flag expressions may use only !, &&, ||, ^, parentheses, true, false, and declared flag names.");
					return false;
				}
				bool leftOk = TryBind(binary.Left, flags, report, out ConfigurationFlagExpression? left);
				bool rightOk = TryBind(binary.Right, flags, report, out ConfigurationFlagExpression? right);
				if (!leftOk || !rightOk)
					return false;
				result = new ConfigurationFlagExpression { Kind = kind, Left = left, Right = right };
				return true;

			default:
				report(GetRange(expression), "Configuration flag expressions may use only !, &&, ||, ^, parentheses, true, false, and declared flag names.");
				return false;
		}
	}

	static TokenRange? GetRange(Expression expression) =>
		expression.SourceSyntax is not null && SyntaxNodeTraversal.TryGetRange(expression.SourceSyntax, out TokenRange range)
			? range
			: null;

	sealed class FlagExpressionTextParser(List<TokenValue> tokens, ConfigurationFlagSet flags, Action<string> report)
	{
		int index;
		public bool IsEnd => index >= tokens.Count;
		public string CurrentValue => IsEnd ? "<end>" : tokens[index].Value;

		public bool TryParseExpression(out ConfigurationFlagExpression? expression) =>
			TryParseOr(out expression);

		bool TryParseOr(out ConfigurationFlagExpression? expression)
		{
			if (!TryParseXor(out expression))
				return false;
			while (Take("||"))
			{
				if (!TryParseXor(out ConfigurationFlagExpression? right))
					return false;
				expression = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Or, Left = expression, Right = right };
			}
			return true;
		}

		bool TryParseXor(out ConfigurationFlagExpression? expression)
		{
			if (!TryParseAnd(out expression))
				return false;
			while (Take("^"))
			{
				if (!TryParseAnd(out ConfigurationFlagExpression? right))
					return false;
				expression = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Xor, Left = expression, Right = right };
			}
			return true;
		}

		bool TryParseAnd(out ConfigurationFlagExpression? expression)
		{
			if (!TryParseUnary(out expression))
				return false;
			while (Take("&&"))
			{
				if (!TryParseUnary(out ConfigurationFlagExpression? right))
					return false;
				expression = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.And, Left = expression, Right = right };
			}
			return true;
		}

		bool TryParseUnary(out ConfigurationFlagExpression? expression)
		{
			if (Take("!"))
			{
				if (!TryParseUnary(out ConfigurationFlagExpression? operand))
				{
					expression = null;
					return false;
				}
				expression = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Not, Left = operand };
				return true;
			}
			return TryParsePrimary(out expression);
		}

		bool TryParsePrimary(out ConfigurationFlagExpression? expression)
		{
			expression = null;
			if (Take("("))
			{
				if (!TryParseExpression(out expression))
					return false;
				if (!Take(")"))
				{
					report("Configuration flag expression is missing ')'.");
					expression = null;
					return false;
				}
				return true;
			}
			if (IsEnd)
			{
				report("Configuration flag expression is incomplete.");
				return false;
			}
			TokenValue token = tokens[index++];
			if (token.Value == "true" || token.Value == "false")
			{
				expression = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Literal, LiteralValue = token.Value == "true" };
				return true;
			}
			if (token.Class == TokenClass.Identifier)
			{
				if (!flags.Declarations.ContainsKey(token.Value))
				{
					report($"Unknown configuration flag '{token.Value}'.");
					return false;
				}
				expression = new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.Flag, FlagName = token.Value };
				return true;
			}
			report($"Unexpected token '{token.Value}' in configuration flag expression.");
			return false;
		}

		bool Take(string value)
		{
			if (value.Length == 2 && index + 1 < tokens.Count && tokens[index].Value + tokens[index + 1].Value == value)
			{
				index += 2;
				return true;
			}
			if (IsEnd || tokens[index].Value != value)
				return false;
			index++;
			return true;
		}
	}
}
