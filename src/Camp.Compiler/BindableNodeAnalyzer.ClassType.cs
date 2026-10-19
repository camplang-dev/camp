namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	void CheckClassTypeProducedResult(TypeReference? targetType, Expression? expression, SyntaxNode? syntax, string context)
	{
		if (!ContainsClassTypeReference(targetType) || expression is null || expression.ResolvedType == ErrorType)
			return;
		if (!ExpressionPreservesClassType(expression))
			Report(GetRange(syntax), $"{context} cannot implicitly convert an enclosing-class value to 'classtype'; use an explicit classtype cast.");
	}

	bool ExpressionPreservesClassType(Expression? expression)
	{
		expression = UnwrapParenthesizedExpression(expression);
		if (expression is not null && expressionRewrites.TryGetValue(expression, out Expression? rewrite) && !ReferenceEquals(rewrite, expression))
			return ExpressionPreservesClassType(rewrite);
		return expression switch
		{
			ThisExpression or DefaultExpression or LiteralExpression { Kind: LiteralKind.Null } => true,
			CastExpression cast => ContainsClassTypeReference(cast.Type),
			ConditionalExpression conditional => ExpressionPreservesClassType(conditional.WhenTrue) && ExpressionPreservesClassType(conditional.WhenFalse),
			AssignmentExpression assignment => ExpressionPreservesClassType(assignment.Value),
			CallExpression { Target: MemberReferenceExpression { Member: FieldDefinition { GeneratedInfo: { Category: GeneratedDeclarationCategory.VirtualDispatch, Reason: "virtual slot field" } } } } => true,
			CallExpression call when callTargets.TryGetValue(call, out FunctionDefinition? function) && ContainsClassTypeReference(function.ReturnType)
				=> IsClassRelativeCallReceiver(call.Target),
			_ => ContainsClassTypeReference(GetCallableSourceType(expression))
		};
	}

	bool IsClassRelativeCallReceiver(Expression? target)
	{
		if (target is not null && expressionRewrites.TryGetValue(target, out Expression? rewrite) && !ReferenceEquals(rewrite, target))
			return IsClassRelativeCallReceiver(rewrite);
		return target switch
		{
			MemberExpression member => ExpressionPreservesClassType(member.Target),
			MemberReferenceExpression member => ExpressionPreservesClassType(member.Target),
			NamedExpression or MethodReferenceExpression => true,
			_ => false
		};
	}

	string RefineClassTypeCallReturn(FunctionDefinition function, Expression? callTarget, string returnType)
		=> RefineClassTypeCallType(function, function.ReturnType, callTarget, returnType);

	string RefineClassTypeCallType(FunctionDefinition function, TypeReference? type, Expression? callTarget, string resolvedType)
	{
		if (!ContainsClassTypeReference(type)
			|| FindContainingType(function) is not ClassDefinition owner
			|| !TryGetClassTypeCallSiteName(callTarget, function, out string classTypeName)
			|| classTypeName == owner.Name && !resolvedType.Contains("classtype", System.StringComparison.Ordinal))
			return resolvedType;

		string sourceReturnType = FormatTypeReference(type);
		if (!TryParseTypeShape(sourceReturnType, out TypeShape shape))
			return resolvedType;

		TypeShape substituted = SubstituteClassTypeShape(shape, owner.Name, classTypeName);
		return TypeShapeParser.Format(substituted);
	}

	string RefineThisTypeCallReturn(FunctionDefinition function, Expression? callTarget, string returnType)
	{
		if (function.ReturnType is not ThisTypeReference)
			return returnType;

		return TryGetReceiverType(callTarget, out string receiverType) ? receiverType : returnType;
	}

	static bool TryGetReceiverType(Expression? callTarget, out string receiverType)
	{
		callTarget = callTarget is ParenthesizedExpression parenthesized ? parenthesized.Expression : callTarget;
		switch (callTarget)
		{
			case MemberExpression member when member.Target?.ResolvedType is { } type:
				receiverType = type;
				return true;

			case MemberReferenceExpression member when member.Target?.ResolvedType is { } type:
				receiverType = type;
				return true;

			default:
				receiverType = "";
				return false;
		}
	}

	TypeShape SubstituteClassTypeShape(TypeShape shape, string ownerName, string classTypeName)
	{
		TypeShape? element = shape.Element is null ? null : SubstituteClassTypeShape(shape.Element, ownerName, classTypeName);
		string name = shape.Kind == TypeShapeKind.Named && (shape.Name == ownerName || shape.Name == "classtype") ? classTypeName : shape.Name;
		return shape with { Name = name, Element = element };
	}
}
