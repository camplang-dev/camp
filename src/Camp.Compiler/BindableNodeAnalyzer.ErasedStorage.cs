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
