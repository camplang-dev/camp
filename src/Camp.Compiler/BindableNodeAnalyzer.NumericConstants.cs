using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	readonly HashSet<Expression> invalidNumericConstantOperations = [];

	readonly record struct NumericConstant(string Type, BigInteger Integer, double Floating = 0)
	{
		public bool IsFloating => Type is "float" or "double";
		public double AsDouble => IsFloating ? Floating : (double)Integer;
	}

	// This evaluator deliberately ignores destination types. A constant operation
	// happens in its natural type; contextual conversion applies to its result.
	bool TryEvaluateNumericConstant(Expression? expression, out NumericConstant value, bool reportErrors = false,
		BindableNode? owner = null, HashSet<BindableNode>? visiting = null)
	{
		value = default;
		bool Evaluate(Expression? child, out NumericConstant result) => TryEvaluateNumericConstant(child, out result, reportErrors, owner, visiting);
		if (expression is ParenthesizedExpression parenthesized)
			return Evaluate(parenthesized.Expression, out value);
		if (expression is NamedExpression or MemberExpression
			&& expressionRewrites.TryGetValue(expression, out Expression? reference))
			return Evaluate(reference, out value);
		BindableNode? definition = expression switch
		{
			VariableReferenceExpression variable => variable.Variable,
			MemberReferenceExpression member => member.Member,
			NamedExpression named when owner is not null && TryResolveNamedConstantReference(named, owner, out BindableNode? node) => node,
			_ => null
		};
		if (definition is VariableDefinition { IsInline: true } variableDefinition)
		{
			if (variableDefinition.ConstantValue is null && owner is not null)
				AnalyzeInlineVariable(variableDefinition, visiting ?? []);
			if (variableDefinition.ConstantValue is ConstantValue.Integer integer)
			{
				value = new(ConstantNumericType(variableDefinition.ResolvedType), integer.Value);
				return true;
			}
		}
		if (definition is FieldDefinition { IsInline: true } field)
		{
			if (field.ConstantValue is null && owner is not null)
				AnalyzeInlineField(field, visiting ?? []);
			if (field.ConstantValue is ConstantValue.Integer integer)
			{
				value = new(ConstantNumericType(field.ResolvedType), integer.Value);
				return true;
			}
		}
		if (definition is VariableDefinition { ConstantValue: ConstantValue.Integer enumValue } enumDefinition)
		{
			value = new(ConstantNumericType(enumDefinition.ResolvedType), enumValue.Value);
			return true;
		}
		if (expression is CastExpression cast && Evaluate(cast.Expression, out NumericConstant source)
			&& !source.IsFloating && cast.Type?.ResolvedType is string castType
			&& TryGetIntegerTypeBounds(castType, out BigInteger castMin, out BigInteger castMax))
		{
			BigInteger width = castMax - castMin + 1;
			BigInteger converted = ((source.Integer - castMin) % width + width) % width + castMin;
			value = new(castType, converted);
			return true;
		}
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
		if (expression is UnaryExpression unary && Evaluate(unary.Operand, out NumericConstant operand))
		{
			if (unary.Operator == UnaryOperator.Plus)
				value = operand;
			else if (unary.Operator == UnaryOperator.Minus)
				value = operand.IsFloating ? new(operand.Type, default, -operand.Floating) : new(PromoteInteger(operand.Type), -operand.Integer);
			else if (unary.Operator == UnaryOperator.BitwiseNot && !operand.IsFloating)
				value = new(PromoteInteger(operand.Type), ~operand.Integer);
			else
				return false;
			return NormalizeNumericConstant(unary, ref value, reportErrors);
		}
		if (expression is not BinaryExpression binary
			|| !Evaluate(binary.Left, out NumericConstant left)
			|| !Evaluate(binary.Right, out NumericConstant right))
			return false;
		if (IsLiteralOperandContextOperator(binary.Operator))
		{
			if (LiteralShapeFitsInteger(binary.Left, right.Type))
				left = left with { Type = right.Type };
			else if (LiteralShapeFitsInteger(binary.Right, left.Type))
				right = right with { Type = left.Type };
		}
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
		if (binary.Operator is BinaryOperator.LeftShift or BinaryOperator.RightShift
			&& TryGetIntegerTypeBounds(common, out BigInteger shiftMin, out BigInteger shiftMax))
		{
			int bits = (int)shiftMax.GetBitLength() + (shiftMin < 0 ? 1 : 0);
			if (r < 0 || r >= bits)
				return InvalidNumericConstant(binary, $"Constant shift count '{r}' must be nonnegative and smaller than the width of type '{common}' ({bits} bits).", reportErrors);
			if (binary.Operator == BinaryOperator.LeftShift && shiftMin < 0 && l < 0)
				return InvalidNumericConstant(binary, $"Constant left shift has a negative operand of type '{common}'.", reportErrors);
		}
		if (binary.Operator is BinaryOperator.Divide or BinaryOperator.Modulo && r == -1
			&& TryGetIntegerTypeBounds(common, out BigInteger divisionMin, out _) && divisionMin < 0 && l == divisionMin)
			return InvalidNumericConstant(binary, $"Integer constant operation overflows type '{common}'.", reportErrors);
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
		return NormalizeNumericConstant(binary, ref value, reportErrors);
	}

	string ConstantNumericType(string? type)
	{
		type = StripTopLevelValueQualifiers(type ?? "int");
		return TryGetUnderlyingNumericType(type, out string? underlying) ? underlying! : type;
	}

	bool NormalizeNumericConstant(Expression operation, ref NumericConstant value, bool reportErrors)
	{
		if (value.IsFloating || !TryGetIntegerTypeBounds(value.Type, out BigInteger min, out BigInteger max))
			return true;
		if (min < 0 && (value.Integer < min || value.Integer > max))
			return InvalidNumericConstant(operation, $"Integer constant operation overflows type '{value.Type}'.", reportErrors);
		if (min == 0)
		{
			BigInteger modulus = max + 1;
			value = value with { Integer = (value.Integer % modulus + modulus) % modulus };
		}
		return true;
	}

	bool InvalidNumericConstant(Expression expression, string message, bool reportErrors)
	{
		if (reportErrors && invalidNumericConstantOperations.Add(expression))
			Report(GetRange(expression.SourceSyntax), message);
		return false;
	}

	bool HasInvalidNumericConstantOperation(Expression? expression) => expression is not null
		&& (invalidNumericConstantOperations.Contains(expression) || expression switch
		{
			ParenthesizedExpression parenthesized => HasInvalidNumericConstantOperation(parenthesized.Expression),
			UnaryExpression unary => HasInvalidNumericConstantOperation(unary.Operand),
			BinaryExpression binary => HasInvalidNumericConstantOperation(binary.Left) || HasInvalidNumericConstantOperation(binary.Right),
			CastExpression cast => HasInvalidNumericConstantOperation(cast.Expression),
			_ => false
		});

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
		string natural = BodyAnalyzeExpression(expression, scope, typeScope);
		if (natural == ErrorType)
			return true;
		if (!value.IsFloating && TryGetIntegerTypeBounds(target, out BigInteger min, out BigInteger max)
			&& (value.Integer < min || value.Integer > max))
		{
			Report(GetRange(expression.SourceSyntax), $"Integer constant '{value.Integer}' is outside the range of type '{targetType}'.");
			expression.ResolvedType = ErrorType;
			return true;
		}
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
