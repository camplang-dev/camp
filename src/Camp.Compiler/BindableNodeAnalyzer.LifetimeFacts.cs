using System.Collections.Generic;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	static string MakeLifetimeFact(string kind, string? anchor, string source)
	{
		return new BoundLifetime(kind, string.IsNullOrWhiteSpace(anchor) ? [] : [anchor], source).ToString();
	}

	string? CreateDeclarationSlotLifetimeFact(string? name, TypeReference? type, string? resolvedType, bool isFixedStorage, AnalysisScope typeScope)
	{
		if (!IsLifetimeTrackedType(type, resolvedType, isFixedStorage, typeScope))
			return null;

		return MakeLifetimeFact("scoped", string.IsNullOrWhiteSpace(name) ? null : name, "slot");
	}

	string? CreateGlobalSlotLifetimeFact(string? name, TypeReference? type, string? resolvedType, AnalysisScope typeScope)
	{
		if (!IsLifetimeTrackedType(type, resolvedType, isFixedStorage: false, typeScope))
			return null;

		return MakeLifetimeFact("escaped", string.IsNullOrWhiteSpace(name) ? null : name, "slot");
	}

	bool IsLifetimeTrackedType(TypeReference? type, string? resolvedType, bool isFixedStorage, AnalysisScope typeScope)
	{
		if (isFixedStorage || IsDirectFixedArrayType(type))
			return true;

		if (type is not null && IsPointerBearingType(type, typeScope))
			return true;

		if (IsPointerBearingResolvedType(resolvedType))
			return true;

		string baseName = BaseTypeName(StripTopLevelValueQualifiers(resolvedType ?? ""));
		return !string.IsNullOrWhiteSpace(baseName)
			&& typeScope.GenericParameters.TryGetValue(baseName, out GenericParameter? parameter)
			&& parameter.Constraint is AnyTypeReference;
	}

	static bool IsPointerBearingResolvedType(string? type)
	{
		if (string.IsNullOrWhiteSpace(type) || type == ErrorType || type == TargetType)
			return false;

		string normalized = StripTopLevelValueQualifiers(type);
		return normalized.Contains('*', System.StringComparison.Ordinal)
			|| normalized.Contains("[]", System.StringComparison.Ordinal)
			|| normalized.StartsWith("delegate ", System.StringComparison.Ordinal)
			|| normalized.StartsWith("iter ", System.StringComparison.Ordinal)
			|| normalized is "string" or "astring" or "wstring"
			|| normalized.EndsWith("?", System.StringComparison.Ordinal);
	}

	void InitializeParameterLifetimeFacts(ParameterDefinition parameter, AnalysisScope typeScope)
	{
		if (!IsLifetimeTrackedType(parameter.Type, parameter.ResolvedType, isFixedStorage: false, typeScope))
			return;

		string fact = parameter.LifetimeBinding ?? MakeLifetimeFact("scoped", parameter.Name, "parameter");
		parameter.SlotLifetimeFact = fact;
		parameter.ValueLifetimeFact = fact;
	}

	void InitializeVariableLifetimeFacts(VariableDefinition definition, AnalysisScope scope)
	{
		string? fact = CreateGlobalSlotLifetimeFact(definition.Name, definition.Type, definition.ResolvedType, scope);
		definition.SlotLifetimeFact = fact;
		definition.ValueLifetimeFact ??= fact;
	}

	void InitializeFieldLifetimeFacts(FieldDefinition definition, AnalysisScope scope)
	{
		string? fact = definition.Modifier == FieldModifier.Static
			? CreateGlobalSlotLifetimeFact(definition.Name, definition.Type, definition.ResolvedType, scope)
			: CreateDeclarationSlotLifetimeFact(definition.Name, definition.Type, definition.ResolvedType, isFixedStorage: false, scope);
		definition.SlotLifetimeFact = fact;
		definition.ValueLifetimeFact ??= fact;
	}

	void InitializeLocalLifetimeFacts(DeclarationStatement declaration, AnalysisScope typeScope)
	{
		string? name = declaration.Target.Names.Count == 1 ? declaration.Target.Names[0] : null;
		string? slotFact = CreateDeclarationSlotLifetimeFact(name, declaration.Target.Type, declaration.Target.ResolvedType, declaration.IsFixedStorage, typeScope);
		declaration.Target.SlotLifetimeFact = slotFact;
		declaration.Target.ValueLifetimeFact = GetExpressionLifetimeFact(declaration.InitialValue) ?? slotFact;
		declaration.SlotLifetimeFact = slotFact;
		declaration.ValueLifetimeFact = declaration.Target.ValueLifetimeFact;
	}

	void ApplyExpressionLifetimeFact(Expression expression, string resolvedType, BodyScope scope, AnalysisScope typeScope)
	{
		string? fact = expression switch
		{
			VariableReferenceExpression variable => GetStorageValueLifetimeFact(variable.Variable),
			NamedExpression named => GetNamedExpressionLifetimeFact(named),
			ThisExpression => scope.CurrentFunction.ReceiverLifetimeBinding ?? MakeLifetimeFact("scoped", "this", "receiver"),
			LiteralExpression literal => GetLiteralLifetimeFact(literal),
			DefaultExpression => IsPointerBearingResolvedType(resolvedType) ? "default" : null,
			ArrayExpression => MakeLifetimeFact("scoped", null, "array literal"),
			InitializerExpression => MakeLifetimeFact("scoped", null, "initializer"),
			ParenthesizedExpression parenthesized => GetExpressionLifetimeFact(parenthesized.Expression),
			CastExpression { LifetimeCastKind: not null } cast => cast.LifetimeBinding,
			CastExpression cast => cast.LifetimeBinding ?? GetExpressionLifetimeFact(cast.Expression),
			ConstructionExpression construction => GetConstructionLifetimeFact(construction),
			WithinExpression within => GetExpressionLifetimeFact(within.Expression),
			FinallyDeleteExpression finallyDelete => GetExpressionLifetimeFact(finallyDelete.Expression),
			UnaryExpression unary => GetUnaryLifetimeFact(unary, resolvedType),
			IndexExpression index => GetIndexLifetimeFact(index, resolvedType),
			NamelessIndexerExpression indexer => GetExpressionLifetimeFact(indexer.Target),
			MemberExpression member => GetMemberLifetimeFact(member, resolvedType),
			CallExpression call => GetCallLifetimeFact(call, resolvedType),
			LambdaExpression => MakeLifetimeFact("scoped", null, "lambda"),
			AssignmentExpression assignment => GetExpressionLifetimeFact(assignment.Value),
			ConditionalExpression conditional => GetExpressionLifetimeFact(conditional.WhenTrue) ?? GetExpressionLifetimeFact(conditional.WhenFalse),
			_ => null
		};

		if (fact is not null)
			expression.ValueLifetimeFact = fact;
	}

	string? GetNamedExpressionLifetimeFact(NamedExpression named)
	{
		if (expressionRewrites.TryGetValue(named, out Expression? rewritten) && !ReferenceEquals(rewritten, named))
			return GetExpressionLifetimeFact(rewritten);

		return named.ValueLifetimeFact;
	}

	static string? GetStorageValueLifetimeFact(BindableNode? storage)
	{
		return storage?.ValueLifetimeFact ?? storage?.SlotLifetimeFact;
	}

	string? GetExpressionLifetimeFact(Expression? expression)
	{
		if (expression is null)
			return null;
		if (expression.ValueLifetimeFact is not null)
			return expression.ValueLifetimeFact;
		if (expressionRewrites.TryGetValue(expression, out Expression? rewritten) && !ReferenceEquals(rewritten, expression))
			return GetExpressionLifetimeFact(rewritten);
		return null;
	}

	static string? GetLiteralLifetimeFact(LiteralExpression literal)
	{
		return literal.Kind switch
		{
			LiteralKind.Null => "null",
			LiteralKind.String => MakeLifetimeFact("escaped", null, "literal"),
			_ => null
		};
	}

	static string? GetConstructionLifetimeFact(ConstructionExpression construction)
	{
		return construction.Kind switch
		{
			ConstructionKind.New => MakeLifetimeFact("escaped", null, "new"),
			ConstructionKind.Init => MakeLifetimeFact("scoped", null, "init"),
			_ => null
		};
	}

	string? GetUnaryLifetimeFact(UnaryExpression unary, string resolvedType)
	{
		if (unary.Operator == UnaryOperator.AddressOf)
			return GetExpressionLifetimeFact(unary.Operand) ?? MakeLifetimeFact("scoped", null, "address-of");
		if (IsPointerBearingResolvedType(resolvedType))
			return GetExpressionLifetimeFact(unary.Operand);
		return null;
	}

	string? GetIndexLifetimeFact(IndexExpression index, string resolvedType)
	{
		string? targetFact = GetExpressionLifetimeFact(index.Target);
		if (TryGetArrayElementType(resolvedType) is not null)
			return targetFact is not null ? targetFact + ":slice" : MakeLifetimeFact("scoped", null, "slice");
		return targetFact is not null ? targetFact + ":element" : null;
	}

	string? GetMemberLifetimeFact(MemberExpression member, string resolvedType)
	{
		if (!IsPointerBearingResolvedType(resolvedType))
			return null;

		string? targetFact = GetExpressionLifetimeFact(member.Target);
		return targetFact is not null ? targetFact + ":member" : MakeLifetimeFact("scoped", null, "member");
	}

	string? GetCallLifetimeFact(CallExpression call, string resolvedType)
	{
		if (!IsPointerBearingResolvedType(resolvedType))
			return null;

		if (callTargets.TryGetValue(call, out FunctionDefinition? function))
		{
			string? returnFact = function.ReturnType?.LifetimeBinding ?? function.ReceiverLifetimeBinding;
			if (returnFact is not null)
				return returnFact + ":call";
		}

		return MakeLifetimeFact("unknown", null, "call");
	}

	void UpdateAssignmentLifetimeFact(Expression? target, string? valueFact)
	{
		if (valueFact is null || target is null)
			return;

		if (TryGetStorageNode(target, out BindableNode? storage) && storage is not null)
			storage.ValueLifetimeFact = valueFact;
	}

	bool TryGetStorageNode(Expression target, out BindableNode? storage)
	{
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
			return TryGetStorageNode(rewritten, out storage);

		switch (target)
		{
			case VariableReferenceExpression variable:
				storage = variable.Variable;
				return storage is not null;

			case MemberReferenceExpression { Member: not null } member:
				storage = member.Member;
				return true;

			case ParenthesizedExpression parenthesized when parenthesized.Expression is not null:
				return TryGetStorageNode(parenthesized.Expression, out storage);

			default:
				storage = null;
				return false;
		}
	}
}
