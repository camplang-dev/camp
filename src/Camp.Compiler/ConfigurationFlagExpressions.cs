using System;
using System.Collections.Generic;

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
}

public static class ConfigurationFlagExpressionBinder
{
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
