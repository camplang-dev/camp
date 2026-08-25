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
	public static ConfigurationFlagExpression And(ConfigurationFlagExpression? left, ConfigurationFlagExpression? right)
	{
		if (left is null)
			return right ?? True();
		if (right is null)
			return left;
		return new ConfigurationFlagExpression { Kind = ConfigurationFlagExpressionKind.And, Left = left, Right = right };
	}

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

	static TokenRange? GetRange(Expression expression) => null;
}
