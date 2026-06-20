using System.Collections.Generic;
using System.Linq;

namespace Camp.Compiler;

public sealed partial class BindableNodeAnalyzer
{
	sealed record LifetimeFact(string Kind, IReadOnlyList<string> Anchors, string Source);

	static string MakeLifetimeFact(string kind, string? anchor, string source)
	{
		return new BoundLifetime(kind, string.IsNullOrWhiteSpace(anchor) ? [] : [anchor], source).ToString();
	}

	static bool TryParseLifetimeFact(string? text, out LifetimeFact fact)
	{
		fact = new LifetimeFact("", [], "");
		if (string.IsNullOrWhiteSpace(text))
			return false;

		string source = "";
		string body = text;
		int sourceIndex = text.IndexOf(':');
		if (sourceIndex >= 0)
		{
			body = text[..sourceIndex];
			source = text[(sourceIndex + 1)..];
		}

		string kind = body;
		List<string> anchors = [];
		int anchorStart = body.IndexOf('(');
		if (anchorStart >= 0 && body.EndsWith(')'))
		{
			kind = body[..anchorStart];
			string anchorText = body[(anchorStart + 1)..^1];
			anchors.AddRange(anchorText.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries));
		}

		if (string.IsNullOrWhiteSpace(kind))
			return false;

		fact = new LifetimeFact(kind, anchors, source);
		return true;
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
			ConstructionExpression construction => IsPointerBearingResolvedType(resolvedType) ? GetConstructionLifetimeFact(construction) : null,
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
			ConstructionKind.New => MakeLifetimeFact("unknown", null, "new"),
			ConstructionKind.Init => MakeLifetimeFact("scoped", null, "init"),
			_ => null
		};
	}

	string? GetUnaryLifetimeFact(UnaryExpression unary, string resolvedType)
	{
		if (unary.Operator == UnaryOperator.AddressOf)
		{
			if (GetExpressionLifetimeFact(unary.Operand) is string operandFact)
				return operandFact;
			if (unary.Operand is not null
				&& TryGetStorageNode(unary.Operand, out BindableNode? storage)
				&& GetStorageLifetimeAnchor(storage) is string anchor)
				return MakeLifetimeFact("scoped", anchor, "address-of");
			return MakeLifetimeFact("scoped", null, "address-of");
		}
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

	void CheckLifetimeAssignment(Expression? target, Expression? value, SyntaxNode? syntax, BodyScope scope, string context)
	{
		if (target is null || value is null)
			return;

		string? valueFactText = GetExpressionLifetimeFact(value);
		if (!TryParseLifetimeFact(valueFactText, out LifetimeFact valueFact))
			return;

		if (valueFact.Kind == "escaped")
			return;
		if (valueFact.Kind == "unknown")
			return;

		if (TryGetEscapedStorageTarget(target))
		{
			Report(GetRange(syntax), $"{context} cannot store a non-escaped pointer-bearing value in escaped storage.");
			return;
		}

		if (TryGetReceiverAnchoredStorageTarget(target, out string? anchor))
		{
			if (!ValueOutlivesAnchor(valueFact, anchor))
				Report(GetRange(syntax), $"{context} cannot store a scoped pointer-bearing value in storage tied to '{anchor}'.");
		}
	}

	void CheckLifetimeResult(Expression? value, SyntaxNode? syntax, BodyScope scope, string context)
	{
		string? valueFactText = GetExpressionLifetimeFact(value);
		if (!TryParseLifetimeFact(valueFactText, out LifetimeFact valueFact))
			return;

		if (valueFact.Kind == "escaped" || valueFact.Kind == "unscoped")
			return;

		string? localAnchor = valueFact.Anchors.FirstOrDefault(anchor => IsLocalLifetimeAnchor(anchor, scope));
		if (localAnchor is not null)
			Report(GetRange(syntax), $"{context} cannot return a pointer-bearing value tied to local storage '{localAnchor}'.");
	}

	void CheckLifetimeDeleteAgainstFree(Expression? expression, SyntaxNode? syntax, BodyScope scope)
	{
		if (!IsPointerBearingResolvedType(expression?.ResolvedType))
			return;

		FunctionDefinition? free = FindFreeFunction(syntax);
		if (free is null)
			return;

		ParameterDefinition? pointerParameter = GetPatternValueParameters(free).FirstOrDefault();
		if (pointerParameter?.LifetimeBinding is not string requiredLifetime)
			return;

		if (!TryParseLifetimeFact(requiredLifetime, out LifetimeFact requiredFact))
			return;

		if (requiredFact.Kind != "escaped")
			return;

		string? factText = GetExpressionLifetimeFact(expression);
		if (!TryParseLifetimeFact(factText, out LifetimeFact actualFact))
			return;
		if (actualFact.Kind is "escaped" or "unknown")
			return;

		if (actualFact.Kind == "scoped" && actualFact.Anchors.Any(anchor => IsLocalLifetimeAnchor(anchor, scope)))
			Report(GetRange(syntax), "Delete target cannot satisfy free parameter lifetime 'escaped'.");
	}

	bool TryGetEscapedStorageTarget(Expression target)
	{
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
			return TryGetEscapedStorageTarget(rewritten);

		return target switch
		{
			VariableReferenceExpression { Variable: VariableDefinition } => true,
			MemberReferenceExpression { Member: FieldDefinition { Modifier: FieldModifier.Static } } => true,
			_ => false
		};
	}

	bool TryGetReceiverAnchoredStorageTarget(Expression target, out string anchor)
	{
		anchor = "";
		if (expressionRewrites.TryGetValue(target, out Expression? rewritten) && !ReferenceEquals(rewritten, target))
			return TryGetReceiverAnchoredStorageTarget(rewritten, out anchor);

		if (target is MemberReferenceExpression { Target: not null, Member: FieldDefinition { Modifier: not FieldModifier.Static } } member)
		{
			if (TryParseLifetimeFact(GetExpressionLifetimeFact(member.Target), out LifetimeFact targetFact)
				&& targetFact.Anchors.Count == 1)
			{
				anchor = targetFact.Anchors[0];
				return true;
			}
			anchor = "this";
			return true;
		}

		if (target is IndexExpression index && GetExpressionLifetimeFact(index.Target) is string targetFactText
			&& TryParseLifetimeFact(targetFactText, out LifetimeFact indexedTargetFact)
			&& indexedTargetFact.Anchors.Count == 1)
		{
			anchor = indexedTargetFact.Anchors[0];
			return true;
		}

		return false;
	}

	static bool ValueOutlivesAnchor(LifetimeFact valueFact, string anchor)
	{
		return valueFact.Kind == "escaped"
			|| valueFact.Kind == "unscoped" && (valueFact.Anchors.Count == 0 || valueFact.Anchors.Contains(anchor));
	}

	bool IsLocalLifetimeAnchor(string anchor, BodyScope scope)
	{
		if (!scope.TryLookup(anchor, out BodySymbol symbol))
			return false;

		return symbol.Node is DeclarationStatement or DeclarationTarget or CatchStatement;
	}

	static string? GetStorageLifetimeAnchor(BindableNode? storage)
	{
		return storage switch
		{
			DeclarationStatement { Target.Names.Count: 1 } declaration => declaration.Target.Names[0],
			DeclarationTarget { Names.Count: 1 } target => target.Names[0],
			VariableDefinition variable => variable.Name,
			FieldDefinition field => field.Name,
			ParameterDefinition parameter => parameter.Name,
			_ => null
		};
	}
}
