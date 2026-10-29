using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly record struct NumericConstant(string Type, BigInteger Integer, double Floating = 0)
	{
		public bool IsFloating => Type is "float" or "double";
		public double AsDouble => IsFloating ? Floating : (double)Integer;
	}

	// This evaluator deliberately ignores destination types. A constant operation
	// happens in its natural type; contextual conversion applies to its result.
	bool TryEvaluateNumericConstant(Expression? expression, out NumericConstant value)
	{
		value = default;
		if (expression is ParenthesizedExpression parenthesized)
			return TryEvaluateNumericConstant(parenthesized.Expression, out value);
		if (IsUnsuffixedIntegerLiteral(expression) && TryParseIntegerLiteralValue(expression, out BigInteger signed, out _))
		{
			value = new(InferUnsuffixedIntegerType(signed), signed);
			return true;
		}
		if (expression is LiteralExpression { Kind: LiteralKind.Number } literal)
		{
			string type = GetNumberLiteralType(literal.Text, null);
			if (NumericLiteralParser.TryParseIntegerMagnitude(literal.Text, out BigInteger integer))
				value = new(type, integer);
			else if (double.TryParse(literal.Text.TrimEnd('f', 'F', 'd', 'D').Replace("_", "", StringComparison.Ordinal),
				NumberStyles.Float, CultureInfo.InvariantCulture, out double floating))
				value = new(type, default, type == "float" ? (float)floating : floating);
			else
				return false;
			return true;
		}
		if (expression is UnaryExpression unary && TryEvaluateNumericConstant(unary.Operand, out NumericConstant operand))
		{
			if (unary.Operator == UnaryOperator.Plus)
				value = operand;
			else if (unary.Operator == UnaryOperator.Minus)
				value = operand.IsFloating ? new(operand.Type, default, -operand.Floating) : new(PromoteInteger(operand.Type), -operand.Integer);
			else if (unary.Operator == UnaryOperator.BitwiseNot && !operand.IsFloating)
				value = new(PromoteInteger(operand.Type), ~operand.Integer);
			else
				return false;
			return true;
		}
		if (expression is not BinaryExpression binary
			|| !TryEvaluateNumericConstant(binary.Left, out NumericConstant left)
			|| !TryEvaluateNumericConstant(binary.Right, out NumericConstant right))
			return false;
		string common = binary.Operator is BinaryOperator.LeftShift or BinaryOperator.RightShift
			? PromoteInteger(left.Type) : UsualArithmeticConversion(left.Type, right.Type);
		if (common == ErrorType)
			return false;
		if (left.IsFloating || right.IsFloating)
		{
			double result;
			switch (binary.Operator)
			{
				case BinaryOperator.Add: result = left.AsDouble + right.AsDouble; break;
				case BinaryOperator.Subtract: result = left.AsDouble - right.AsDouble; break;
				case BinaryOperator.Multiply: result = left.AsDouble * right.AsDouble; break;
				case BinaryOperator.Divide: result = left.AsDouble / right.AsDouble; break;
				default: return false;
			}
			value = new(common, default, common == "float" ? (float)result : result);
			return true;
		}
		BigInteger l = left.Integer, r = right.Integer, computed;
		switch (binary.Operator)
		{
			case BinaryOperator.Add: computed = l + r; break;
			case BinaryOperator.Subtract: computed = l - r; break;
			case BinaryOperator.Multiply: computed = l * r; break;
			case BinaryOperator.Divide when r != 0: computed = l / r; break;
			case BinaryOperator.Modulo when r != 0: computed = l % r; break;
			case BinaryOperator.BitwiseAnd: computed = l & r; break;
			case BinaryOperator.BitwiseOr: computed = l | r; break;
			case BinaryOperator.BitwiseXor: computed = l ^ r; break;
			case BinaryOperator.LeftShift when r >= 0 && r < 64: computed = l << (int)r; break;
			case BinaryOperator.RightShift when r >= 0 && r < 64: computed = l >> (int)r; break;
			default: return false;
		}
		value = new(common, computed);
		return true;
	}

	static bool ContainsOnlyUnsuffixedNumericLiterals(Expression? expression) => expression switch
	{
		LiteralExpression { Kind: LiteralKind.Number } literal => NumericLiteralParser.GetSuffix(literal.Text).Length == 0,
		ParenthesizedExpression parenthesized => ContainsOnlyUnsuffixedNumericLiterals(parenthesized.Expression),
		UnaryExpression { Operator: UnaryOperator.Plus or UnaryOperator.Minus or UnaryOperator.BitwiseNot } unary => ContainsOnlyUnsuffixedNumericLiterals(unary.Operand),
		BinaryExpression binary => ContainsOnlyUnsuffixedNumericLiterals(binary.Left) && ContainsOnlyUnsuffixedNumericLiterals(binary.Right),
		_ => false
	};

	bool LiteralShapeFitsInteger(Expression? expression, string type)
	{
		if (expression is ParenthesizedExpression parenthesized)
			return LiteralShapeFitsInteger(parenthesized.Expression, type);
		if (expression is ConditionalExpression conditional)
			return LiteralShapeFitsInteger(conditional.WhenTrue, type) && LiteralShapeFitsInteger(conditional.WhenFalse, type);
		return ContainsOnlyUnsuffixedNumericLiterals(expression) && TryEvaluateNumericConstant(expression, out NumericConstant value)
			&& !value.IsFloating && TryGetIntegerTypeBounds(type, out BigInteger min, out BigInteger max)
			&& value.Integer >= min && value.Integer <= max;
	}

	bool IsIntegerLiteralShape(Expression? expression) => expression switch
	{
		ParenthesizedExpression parenthesized => IsIntegerLiteralShape(parenthesized.Expression),
		ConditionalExpression conditional => IsIntegerLiteralShape(conditional.WhenTrue) && IsIntegerLiteralShape(conditional.WhenFalse),
		_ => ContainsOnlyUnsuffixedNumericLiterals(expression) && TryEvaluateNumericConstant(expression, out NumericConstant value) && !value.IsFloating
	};

	bool TryAnalyzeLiteralConstantTarget(Expression expression, BodyScope scope, AnalysisScope typeScope, string? targetType, out string type)
	{
		type = ErrorType;
		if (targetType is null || !IsNumericTypeName(targetType)
			|| expression is not (BinaryExpression or UnaryExpression)
			|| IsUnsuffixedIntegerLiteral(expression)
			|| !ContainsOnlyUnsuffixedNumericLiterals(expression)
			|| !TryEvaluateNumericConstant(expression, out NumericConstant value))
			return false;
		string target = StripTopLevelValueQualifiers(targetType);
		if (value.IsFloating && target is not ("float" or "double"))
			return false;
		if (!value.IsFloating && TryGetIntegerTypeBounds(target, out BigInteger min, out BigInteger max)
			&& (value.Integer < min || value.Integer > max))
		{
			Report(GetRange(expression.SourceSyntax), $"Integer constant '{value.Integer}' is outside the range of type '{targetType}'.");
			expression.ResolvedType = ErrorType;
			return true;
		}
		string natural = BodyAnalyzeExpression(expression, scope, typeScope);
		if (natural == ErrorType)
			return true;
		if (natural != target)
		{
			// Clone only the root so a rewrite never refers back to its own key.
			Expression inner = expression switch
			{
				BinaryExpression binary => new BinaryExpression { SourceSyntax = binary.SourceSyntax, Left = binary.Left, Operator = binary.Operator, Right = binary.Right, ResolvedType = natural },
				UnaryExpression unary => new UnaryExpression { SourceSyntax = unary.SourceSyntax, Operand = unary.Operand, Operator = unary.Operator, ResolvedType = natural },
				_ => throw new InvalidOperationException()
			};
			expressionRewrites[expression] = new CastExpression
			{
				SourceSyntax = expression.SourceSyntax, Kind = CastKind.Type, Type = TypeArgumentReference(target), Expression = inner, ResolvedType = target
			};
		}
		type = target;
		expression.ResolvedType = type;
		return true;
	}
}
