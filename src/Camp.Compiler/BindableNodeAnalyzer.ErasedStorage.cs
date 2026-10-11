using System;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	Expression CaptureErasedStorageAccess(Expression expression)
	{
		if (expression is ParenthesizedExpression { Expression: not null } parenthesized)
			return CaptureErasedStorageAccess(parenthesized.Expression);
		if (expression is UnaryExpression { Operator: UnaryOperator.PointerDereference, Operand: not null } dereference)
			return new UnaryExpression { SourceSyntax = expression.SourceSyntax, Operator = dereference.Operator,
				Operand = CaptureRepeatedParamsSourceExpression(dereference.Operand, "erasedPointer"), ResolvedType = expression.ResolvedType };
		if (expression is IndexExpression { Target: not null } index)
		{
			Expression target = index.Target;
			if (TryGetArrayElementType(target.ResolvedType) is not null && TryCreateParamsComponentExpressions(target, out var arrayComponents))
				target = arrayComponents[0];
			IndexExpression captured = new() { SourceSyntax = expression.SourceSyntax,
				Target = CaptureRepeatedParamsSourceExpression(target, "erasedElements"), ResolvedType = expression.ResolvedType };
			foreach (ArgumentExpression argument in index.Arguments)
			{
				ArgumentExpression copy = CloneArgument(argument);
				if (copy.Value is not null) copy.Value = CaptureRepeatedParamsSourceExpression(copy.Value, "erasedIndex");
				captured.Arguments.Add(copy);
			}
			return captured;
		}
		return expression;
	}

	// 025 §7: a void<V>* of an expandable V converts implicitly to the distributive V*, one pointer per
	// component of the one storage object. The reverse conversion is never formed.
	bool TryGetErasedPointerExpansion(string source, string target, out string storageType)
	{
		storageType = "";
		if (!source.Contains("void<", StringComparison.Ordinal) || !TryParseTypeShape(source, out TypeShape sourceShape) || !TryParseTypeShape(target, out TypeShape targetShape)
			|| sourceShape.Kind != TypeShapeKind.Pointer || targetShape.Kind != TypeShapeKind.Pointer
			|| sourceShape.Element is not { Kind: TypeShapeKind.Named } storage
			|| !ErasedTypeIdentity.TryGetPayload(storage.Name, out string payload)
			|| !TryGetParamsComponentShape(null, payload, "value", out _)
			|| !TryParseTypeShape(payload, out TypeShape payloadShape) || targetShape.Element is null
			|| !TypeShapesSameIgnoringValueQualifiers(payloadShape, targetShape.Element))
			return false;
		bool sourceConst = storage.Qualifiers.IsConst || payloadShape.Qualifiers.IsConst;
		bool sourceVolatile = storage.Qualifiers.IsVolatile || payloadShape.Qualifiers.IsVolatile;
		if (sourceConst && !targetShape.Element.Qualifiers.IsConst || sourceVolatile && !targetShape.Element.Qualifiers.IsVolatile)
			return false;
		storageType = TypeShapeParser.Format(storage);
		return true;
	}

	// Replaces a void<V>* value by &*value typed as the distributive V*, so the ordinary component
	// expansion of an address forms one pointer per component. The source is evaluated once.
	Expression? TryExpandErasedPointer(Expression? value, string actual, string expected)
	{
		if (value is null || !TryGetErasedPointerExpansion(actual, expected, out string storageType))
			return null;
		UnaryExpression storage = new()
		{
			SourceSyntax = value.SourceSyntax,
			Operator = UnaryOperator.PointerDereference,
			Operand = value,
			ResolvedType = storageType,
			ValueLifetimeFact = value.ValueLifetimeFact
		};
		return new UnaryExpression
		{
			SourceSyntax = value.SourceSyntax,
			Operator = UnaryOperator.AddressOf,
			Operand = storage,
			ResolvedType = StripLifetimeQualifiers(expected),
			ValueLifetimeFact = value.ValueLifetimeFact
		};
	}

	bool TryGetErasedStorageType(Expression? expression, out string storageType)
	{
		storageType = "";
		if (expression is not null && expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(expression, rewritten))
			return TryGetErasedStorageType(rewritten, out storageType);
		string? candidate = expression switch
		{
			ThisExpression when currentRewriteFunction is not null => GetExplicitThisParameter(currentRewriteFunction)?.ResolvedType,
			ParenthesizedExpression parenthesized => TryGetErasedStorageType(parenthesized.Expression, out storageType) ? storageType : null,
			VariableReferenceExpression variable => variable.Variable?.ResolvedType,
			UnaryExpression { Operator: UnaryOperator.PointerDereference } unary => TryGetPointerElementType(unary.Operand?.ResolvedType),
			IndexExpression { Arguments.Count: 1 } index => TryGetArrayElementType(index.Target?.ResolvedType) ?? TryGetPointerElementType(index.Target?.ResolvedType),
			_ => null
		};
		if (!ErasedTypeIdentity.TryGetPayload(candidate, out _))
			return false;
		storageType = candidate!;
		return true;
	}
}
